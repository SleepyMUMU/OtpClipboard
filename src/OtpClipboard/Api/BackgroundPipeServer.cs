using System.IO.Pipes;
using OtpClipboard.Models;
using OtpClipboard.Services;

namespace OtpClipboard.Api;

internal sealed class BackgroundPipeServer : IAsyncDisposable
{
    private readonly NotificationMonitorService _monitor;
    private readonly BackgroundDispatcher _dispatcher;
    private readonly Func<bool> _trayAvailable;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenerTask;
    private bool _exitRequested;
    private StartupStatus? _startupStatus;
    private string? _listenerFailure;

    public BackgroundPipeServer(
        NotificationMonitorService monitor,
        BackgroundDispatcher dispatcher,
        Func<bool> trayAvailable)
    {
        _monitor = monitor;
        _dispatcher = dispatcher;
        _trayAvailable = trayAvailable;
    }

    public void Start() =>
        _listenerTask ??= Task.Run(() => ServeAsync(_shutdown.Token));

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                BridgeNames.Pipe,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                var request = await PipeCodec.ReadAsync<PipeRequest>(pipe, cancellationToken);
                _listenerFailure = null;
                var response = await HandleAsync(request, cancellationToken);
                await PipeCodec.WriteAsync(pipe, response, cancellationToken);
                if (_exitRequested)
                {
                    _shutdown.Cancel();
                    ExitRequested?.Invoke();
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (EndOfStreamException) { /* A health probe may connect and close without a request. */ }
            catch (Exception ex)
            {
                _listenerFailure = $"后台控制服务异常：{ex.Message}";
                try { await Task.Delay(250, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void RequestStop()
    {
        _shutdown.Cancel();
    }

    private async Task<PipeResponse> HandleAsync(
        PipeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Command)
            {
                case "notification-diagnostics":
                    if (_monitor.Relay is null) return new(false, "通知服务不可用");
                    var diagnostics = await _dispatcher.InvokeAsync(
                        () => _monitor.Relay.GetDiagnosticsAsync(), TimeSpan.FromSeconds(10), cancellationToken);
                    return new(true, Diagnostics: diagnostics);
                case BridgeNames.Snapshot:
                    return new(true, Snapshot: CreateSnapshot());
                case BridgeNames.RequestAccess:
                    await _dispatcher.InvokeAsync(() => _monitor.RequestAccessAsync(),
                        TimeSpan.FromSeconds(10), cancellationToken);
                    return new(true, Snapshot: CreateSnapshot());
                case BridgeNames.SetStartup:
                    return await SetStartupAsync(request);
                case BridgeNames.GetStartup:
                    return await GetStartupAsync();
                case BridgeNames.Scan:
                    await _dispatcher.InvokeAsync(
                        () => _monitor.ScanCurrentNotificationsAsync(),
                        TimeSpan.FromSeconds(15), cancellationToken);
                    return new(true, Snapshot: CreateSnapshot());
                case BridgeNames.UpdatePreferences:
                    await _dispatcher.InvokeAsync(() => _monitor.UpdatePreferencesAsync(
                        request.PhoneLinkOnly ?? true,
                        request.ShowPopup ?? true,
                        request.MinimizeToTray ?? true, request.AutoCopy ?? false),
                        TimeSpan.FromSeconds(10), cancellationToken);
                    return new(true, Snapshot: CreateSnapshot());
                case BridgeNames.Recopy:
                    return await RecopyAsync(request);
                case BridgeNames.Exit:
                    return await StopAfterResponseAsync();
                default:
                    return await RunUiCommandAsync(request);
            }
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, CreateSnapshot());
        }
    }

    private async Task<PipeResponse> RecopyAsync(PipeRequest request)
    {
        var item = _monitor.Records.FirstOrDefault(record =>
            record.Time == request.Time && record.Code == request.Code);
        if (item is null) return new(false, "验证码记录已不存在", CreateSnapshot());
        bool copied = await _dispatcher.InvokeAsync(() => _monitor.RecopyAsync(item),
            TimeSpan.FromSeconds(10));
        return new(copied, copied ? null : "重新复制失败", CreateSnapshot());
    }

    private async Task<PipeResponse> SetStartupAsync(PipeRequest request)
    {
        if (request.StartupEnabled is not bool enabled)
            return new(false, "缺少自动启动设置。");
        _startupStatus = await _dispatcher.InvokeAsync(
            () => StartupService.SetEnabledAsync(enabled), TimeSpan.FromSeconds(15));
        return new(_startupStatus.Succeeded, _startupStatus.Error?.Message, CreateSnapshot());
    }

    private async Task<PipeResponse> GetStartupAsync()
    {
        _startupStatus = await _dispatcher.InvokeAsync(
            StartupService.QueryAsync, TimeSpan.FromSeconds(10));
        return new(_startupStatus.Succeeded, _startupStatus.Error?.Message, CreateSnapshot());
    }

    private async Task<PipeResponse> RunUiCommandAsync(PipeRequest request)
    {
        var action = UiActions.GetValueOrDefault(request.Command);
        if (action is null) return new(false, "未知请求");
        await _dispatcher.InvokeAsync(() => { action(request); return Task.CompletedTask; },
            TimeSpan.FromSeconds(10));
        return new(true, Snapshot: CreateSnapshot());
    }

    private async Task<PipeResponse> StopAfterResponseAsync()
    {
        _exitRequested = true;
        return new(true);
    }

    private SnapshotResponse CreateSnapshot() => new(
        _monitor.IsMonitoring,
        (int)_monitor.AccessStatus,
        _trayAvailable(),
        _monitor.Preferences.PhoneLinkOnly,
        _monitor.Preferences.ShowPopup,
        _monitor.Preferences.MinimizeToTray,
        _monitor.Records.ToArray(),
        _startupStatus?.IsEnabled,
        _startupStatus?.CanChange ?? false,
        _listenerFailure ?? _startupStatus?.Message ?? "正在读取开机自启动状态。",
        _listenerFailure ?? _startupStatus?.Error?.Message, _monitor.Preferences.AutoCopy);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_listenerTask is not null)
        {
            try { await _listenerTask; }
            catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
    }

    /// <summary>
    /// 由后台控制器注入；所有委托都在后台 STA 线程执行，不得直接操作管道线程的对象。
    /// </summary>
    public required IReadOnlyDictionary<string, Action<PipeRequest>> UiActions { get; init; }

    /// <summary>退出响应已经写回客户端后再触发，确保 UI 不会因后台先退出而丢失响应。</summary>
    public Action? ExitRequested { get; init; }
}

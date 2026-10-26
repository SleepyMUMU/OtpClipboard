using OtpClipboard.Api;
using OtpClipboard.Desktop;
using OtpClipboard.Services;

namespace OtpClipboard;

/// <summary>始终运行的后台控制器；不创建 WinUI 控件或 DispatcherQueue。</summary>
internal sealed class BackgroundController : IDisposable
{
    private readonly DesktopHostWindow _host = new();
    private readonly NotificationMonitorService _monitor;
    private readonly DesktopIntegration _desktop;
    private readonly RelayNotificationService _relay;
    private readonly BackgroundPipeServer _pipe;
    private readonly BackgroundDispatcher _dispatcher = new();
    private bool _disposed;
    private bool _stopping;
    private bool _uiLaunching;
    private Task? _startupTask;

    public BackgroundController()
    {
        SynchronizationContext.SetSynchronizationContext(_dispatcher);
        DeliveryTrace.Start();
        _monitor = new NotificationMonitorService(_dispatcher);
        _desktop = new DesktopIntegration(
            _host.Handle,
            () => _ = ShowWindowAsync(),
            RequestExit,
            _monitor.Report);
        _relay = new RelayNotificationService(_dispatcher, _monitor.Report);
        _relay.ShowCodeCard = _desktop.ShowNotificationCard;
        _desktop.CopyRequested = async code =>
        {
            var result = await ClipboardService.TryCopyAsync(code);
            DeliveryTrace.Write(result.Success ? "card-copy-complete" : "card-copy-failed", 0);
            return result.Success;
        };
        _monitor.Relay = _relay;
        _pipe = new BackgroundPipeServer(
            _monitor,
            _dispatcher,
            () => _desktop.IsTrayAvailable)
        {
            UiActions = new Dictionary<string, Action<PipeRequest>>(StringComparer.Ordinal)
            {
                [BridgeNames.Show] = _ => LaunchUi(),
                [BridgeNames.PreviewPopup] = request =>
                {
                    if (request.Code is not null) TryPreviewPopup(request.Code);
                },
                [BridgeNames.DismissPopup] = _ => _desktop.DismissPopup(),
                [BridgeNames.ClearRecords] = _ => _monitor.ClearRecords(),
                [BridgeNames.ToggleMonitoring] = _ => _monitor.ToggleMonitoring(),
            },
            ExitRequested = ExitFromPipe,
        };
    }

    public Task StartAsync() =>
        _startupTask ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        await _dispatcher.InvokeAsync(_monitor.InitializeAsync, TimeSpan.FromSeconds(15));
        _pipe.Start();
    }

    public void RunMessageLoop()
    {
        while (!_disposed && _host.PumpPendingMessages())
        {
            _host.WaitForHostMessage(_dispatcher.WaitHandle);
            _dispatcher.Drain();
        }
    }

    private bool LaunchUi()
    {
        if (!_host.IsMessageThread)
        {
            _ = _dispatcher.InvokeAsync(() =>
            {
                LaunchUi();
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(10));
            return true;
        }
        if (_uiLaunching) return true;
        _uiLaunching = true;
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "OtpClipboard.exe");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                ArgumentList = { "--ui" },
            });
            return true;
        }
        catch (Exception ex)
        {
            _monitor.Report($"无法打开主界面：{ex.Message}");
            return false;
        }
        finally { _uiLaunching = false; }
    }

    private Task ShowWindowAsync()
    {
        LaunchUi();
        return Task.CompletedTask;
    }

    private bool TryPreviewPopup(string code)
    {
        try { return _relay.Show("本地虚构短信：验证码 246810，用于部署测试。", code, preview: true); }
        catch (Exception ex) { _monitor.Report($"无法预览提示：{ex.Message}"); return false; }
    }

    private void ExitFromPipe()
    {
        if (_stopping) return;
        _stopping = true;
        _pipe.RequestStop();
        _ = Task.Run(ShutdownAsync);
    }

    private async Task ShutdownAsync()
    {
        try { await _pipe.DisposeAsync(); }
        finally
        {
            try { await _dispatcher.InvokeAsync(_monitor.ShutdownAsync, TimeSpan.FromSeconds(10)); }
            finally
            {
                try
                {
                    await _dispatcher.InvokeAsync(
                        () => { Dispose(); return Task.CompletedTask; },
                        TimeSpan.FromSeconds(10));
                }
                catch { }
                _host.PostClose();
            }
        }
    }

    private void RequestExit() => ExitFromPipe();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _relay.Dispose();
        _monitor.Dispose();
        _desktop.Dispose();
        _host.Dispose();
        _dispatcher.Dispose();
    }
}

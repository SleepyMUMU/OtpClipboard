using System.Globalization;
using OtpClipboard.Api;
using OtpClipboard.Core;
using OtpClipboard.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace OtpClipboard.Services;

/// <summary>
/// 后台通知处理：识别、来源筛选、剪贴板写入和内存记录。
/// 通知正文与验证码只保留在当前进程内存，不写入历史文件。
/// </summary>
public sealed class NotificationMonitorService : IDisposable
{
    private const int SeenCapacity = 512;
    private const int PendingCapacity = 200;
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly HashSet<uint> _seen = [];
    private readonly Queue<uint> _seenOrder = new();
    private readonly Queue<UserNotification> _pending = new();
    private readonly SynchronizationContext _context;
    private readonly List<MonitorRecord> _records = [];
    private bool _processing;
    private bool _disposed;
    private bool _monitoring;
    private bool _saveFailed;

    public NotificationMonitorService(SynchronizationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public RelayNotificationService? Relay { get; set; }
    public event Action? StateChanged;
    public event Action<string>? CodeCopied;
    public IReadOnlyList<MonitorRecord> Records => _records;
    public AppPreferences Preferences { get; private set; } = new();
    public bool IsMonitoring => _monitoring;
    public UserNotificationListenerAccessStatus AccessStatus
    {
        get
        {
            try { return _listener.GetAccessStatus(); }
            catch { return UserNotificationListenerAccessStatus.Unspecified; }
        }
    }

    public async Task InitializeAsync()
    {
        try { Preferences = await AppPreferences.LoadAsync(); }
        catch (Exception ex) { AddRecord(EventKind.Failed, "失败", "设置读取失败", ErrorText.Describe(ex)); }

        try
        {
            foreach (var entry in await NotificationHistoryStore.LoadAsync())
            {
                var kind = Enum.TryParse<EventKind>(entry.Kind, true, out var parsed) && Enum.IsDefined(parsed)
                    ? parsed : EventKind.Info;
                _records.Add(CreateRestoredRecord(kind, entry.Time));
            }
        }
        catch (Exception ex) { AddRecord(EventKind.Failed, "失败", "读取历史记录失败", ErrorText.Describe(ex)); }
        RefreshAccess();
    }

    public void RefreshAccess()
    {
        if (_disposed) return;
        if (AccessStatus == UserNotificationListenerAccessStatus.Allowed) StartMonitoring();
        else StopMonitoring();
        RaiseStateChanged();
    }

    public async Task<UserNotificationListenerAccessStatus> RequestAccessAsync()
    {
        var status = await _listener.RequestAccessAsync();
        if (status == UserNotificationListenerAccessStatus.Allowed) StartMonitoring();
        RaiseStateChanged();
        return status;
    }

    public bool ToggleMonitoring()
    {
        if (_monitoring) StopMonitoring();
        else if (AccessStatus == UserNotificationListenerAccessStatus.Allowed) StartMonitoring();
        RaiseStateChanged();
        return _monitoring;
    }

    public async Task<AppPreferences> UpdatePreferencesAsync(
        bool phoneLinkOnly, bool showPopup, bool minimizeToTray, bool autoCopy)
    {
        Preferences.AutoCopy = autoCopy;
        Preferences.PhoneLinkOnly = phoneLinkOnly;
        Preferences.ShowPopup = showPopup;
        Preferences.MinimizeToTray = minimizeToTray;
        await Preferences.SaveAsync();
        RaiseStateChanged();
        return Preferences;
    }

    public async Task<int> ScanCurrentNotificationsAsync()
    {
        if (AccessStatus != UserNotificationListenerAccessStatus.Allowed)
            throw new InvalidOperationException("请先授权通知访问。");
        var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        int handled = 0;
        foreach (var notification in notifications.OrderBy(n => n.CreationTime))
        {
            if (!RememberNotification(notification.Id)) continue;
            await QueueNotificationAsync(notification);
            handled++;
        }
        AddRecord(EventKind.Info, "扫描完成", "通知中心扫描完成",
            $"共 {notifications.Count} 条，本次新处理 {handled} 条。");
        return handled;
    }

    public MonitorRecord? LatestCopied() =>
        _records.FirstOrDefault(item => item.Kind == EventKind.Copied.ToString() &&
            !string.IsNullOrEmpty(item.Code));

    public async Task<bool> RecopyAsync(MonitorRecord item)
    {
        if (string.IsNullOrEmpty(item.Code)) return false;
        var result = await ClipboardService.TryCopyAsync(item.Code, _shutdown.Token);
        if (!result.Success)
        {
            AddRecord(EventKind.Failed, "失败", "重新复制失败", result.Error ?? "未知错误");
            return false;
        }

        AddRecord(EventKind.Copied, "已复制", $"验证码 {OtpParser.Mask(item.Code)} 已重新复制",
            $"来自 {item.Time} 的记录", item.Code, DateTime.Now);
        CodeCopied?.Invoke(item.Code);
        return true;
    }

    public void ClearRecords()
    {
        _records.Clear();
        _seen.Clear();
        _seenOrder.Clear();
        _ = SaveAsync();
        RaiseStateChanged();
    }

    public void Report(string message) =>
        AddRecord(EventKind.Info, "提示", message, DateTime.Now.ToString("HH:mm:ss"));

    private void StartMonitoring()
    {
        if (_monitoring || _disposed) return;
        _listener.NotificationChanged -= OnNotificationChanged;
        _listener.NotificationChanged += OnNotificationChanged;
        _monitoring = true;
    }

    private void StopMonitoring()
    {
        if (_monitoring)
        {
            _monitoring = false;
            _listener.NotificationChanged -= OnNotificationChanged;
        }
        _pending.Clear();
    }

    private void OnNotificationChanged(
        UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added || !_monitoring) return;
        uint id = args.UserNotificationId;
        var received = System.Diagnostics.Stopwatch.StartNew();
        DeliveryTrace.Write("listener-callback", id);
        _context.Post(async _ =>
        {
        DeliveryTrace.Write("dispatcher-ready", id, received.Elapsed.TotalMilliseconds);
        if (_disposed || !_monitoring) return;
        if (!RememberNotification(id)) return;
        try
        {
            var notification = _listener.GetNotification(id);
            if (notification is not null) await QueueNotificationAsync(notification);
        }
        catch (Exception ex)
        {
            AddRecord(EventKind.Failed, "失败", $"读取新通知失败（ID {id}）", ErrorText.Describe(ex));
        }
        }, null);
    }

    private async Task QueueNotificationAsync(UserNotification notification)
    {
        lock (_pending)
        {
            if (_pending.Count >= PendingCapacity) _pending.Dequeue();
            _pending.Enqueue(notification);
            if (_processing) return;
            _processing = true;
        }

        try
        {
            while (true)
            {
                UserNotification next;
                lock (_pending)
                {
                    if (!_pending.TryDequeue(out next!))
                    {
                        _processing = false;
                        return;
                    }
                }
                await ProcessAsync(next);
            }
        }
        catch
        {
            lock (_pending) _processing = false;
            throw;
        }
    }

    private async Task ProcessAsync(UserNotification notification)
    {
        string appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";
        string appId = notification.AppInfo?.AppUserModelId ?? "";
        string family = notification.AppInfo?.PackageFamilyName ?? "";
        string time = notification.CreationTime.LocalDateTime.ToString(
            HistoryArchive.TimeFormat, CultureInfo.InvariantCulture);
        string context = $"来源：{NotificationSource.Describe(appName, family)}　时间：{time}";

        if (family == Windows.ApplicationModel.Package.Current.Id.FamilyName) return;
        if (Preferences.PhoneLinkOnly && !NotificationSource.IsPhoneLink(family, appId))
        {
            AddRecord(EventKind.Filtered, "已过滤", "来源不是手机连接，未做处理", context, null,
                notification.CreationTime.LocalDateTime);
            return;
        }

        string body = ReadText(notification);
        DeliveryTrace.Write("phone-notification-read", notification.Id,
            (DateTimeOffset.Now - notification.CreationTime).TotalMilliseconds, notification.CreationTime);
        string preview = OtpParser.Preview(body);
        context += preview.Length > 0 ? $"\n内容：{preview}" : "\n内容：（通知没有可读取的文本）";
        if (body.Length == 0)
        {
            AddRecord(EventKind.NoMatch, "未匹配", "通知里没有文本内容", context, null,
                notification.CreationTime.LocalDateTime);
            return;
        }

        // Do not replay or auto-copy stale notifications after a restart/manual scan.
        if (DateTimeOffset.Now - notification.CreationTime > TimeSpan.FromMinutes(2)) return;
        string? code = OtpParser.Extract(body);
        if (code is null)
        {
            RelayIfEnabled(notification, body, null, false);
            AddRecord(EventKind.NoMatch, "未匹配", "通知里没有识别到验证码", context, null,
                notification.CreationTime.LocalDateTime);
            return;
        }

        // Submit the banner before clipboard persistence or UI/history work can delay it.
        RelayIfEnabled(notification, body, code, Preferences.AutoCopy);
        bool copied = false;
        var clipboardTimer = System.Diagnostics.Stopwatch.StartNew();
        if (Preferences.AutoCopy)
        {
            var result = await ClipboardService.TryCopyAsync(code, _shutdown.Token);
            copied = result.Success;
            if (!copied) AddRecord(EventKind.Failed, "失败", "自动复制失败；可点击通知重试", result.Error ?? "", code);
        }
        if (Relay is not null) Relay.ClipboardMilliseconds = clipboardTimer.Elapsed.TotalMilliseconds;
        DeliveryTrace.Write(copied ? "clipboard-complete" : "clipboard-skipped-or-failed",
            notification.Id, clipboardTimer.Elapsed.TotalMilliseconds);
        AddRecord(copied ? EventKind.Copied : EventKind.Info, copied ? "已复制" : "待复制",
            copied ? "验证码已自动复制" : "已识别验证码，点击通知按钮复制", context, code,
            notification.CreationTime.LocalDateTime);
    }

    private void RelayIfEnabled(UserNotification original, string body, string? code, bool autoCopyEnabled)
    {
        if (!Preferences.ShowPopup || Relay is null) return;
        try
        {
            // Preserve the original in the notification center during initial field testing.
            DeliveryTrace.Write("relay-start", original.Id);
            if (!Relay.Show(body, code, autoCopyEnabled)) Report("通知未能提交，请检查助手通知设置。");
        }
        catch (Exception ex) { Report("通知接管失败：" + ex.Message); }
    }

    private bool RememberNotification(uint id)
    {
        if (!_seen.Add(id)) return false;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > SeenCapacity) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    private static string ReadText(UserNotification notification)
    {
        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is null) return "";
        return string.Join("\n", binding.GetTextElements().Select(e => e.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
    }

    private static MonitorRecord CreateRestoredRecord(EventKind kind, string time) => new(
        kind.ToString(),
        kind switch
        {
            EventKind.Copied => "已复制",
            EventKind.NoMatch => "未匹配",
            EventKind.Filtered => "已过滤",
            EventKind.Failed => "失败",
            _ => "提示",
        },
        "上次运行的处理记录",
        "为保护隐私，重启后仅恢复状态和时间，不保留通知内容或验证码。",
        null,
        time);

    private void AddRecord(EventKind kind, string status, string title, string detail,
        string? code = null, DateTime? time = null)
    {
        void Apply()
        {
            _records.Insert(0, new MonitorRecord(kind.ToString(), status, title, detail, code,
                (time ?? DateTime.Now).ToString(HistoryArchive.TimeFormat, CultureInfo.InvariantCulture)));
            while (_records.Count > NotificationHistoryStore.MaxRecords)
                _records.RemoveAt(_records.Count - 1);
            RaiseStateChanged();
            _ = SaveAsync();
        }

        if (SynchronizationContext.Current == _context) Apply();
        else _context.Post(_ => Apply(), null);
    }

    private void RaiseStateChanged()
    {
        if (_disposed) return;
        if (SynchronizationContext.Current == _context) StateChanged?.Invoke();
        else _context.Post(_ => { if (!_disposed) StateChanged?.Invoke(); }, null);
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var payload = _records.Select(item => new HistoryEntry(item.Kind, item.Time)).ToList();
            await NotificationHistoryStore.SaveAsync(payload);
            _saveFailed = false;
        }
        catch (Exception ex)
        {
            if (_saveFailed) return;
            _saveFailed = true;
            AddRecord(EventKind.Failed, "失败", "保存记录失败", ErrorText.Describe(ex));
        }
        finally { _saveLock.Release(); }
    }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        StopMonitoring();
        _shutdown.Cancel();
        await SaveAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.NotificationChanged -= OnNotificationChanged; } catch { }
        _shutdown.Dispose();
        _saveLock.Dispose();
    }
}

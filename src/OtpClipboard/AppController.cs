using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OtpClipboard.Api;
using OtpClipboard.Models;
using OtpClipboard.Services;
using OtpClipboard.Views;
using Windows.ApplicationModel;
using Windows.UI.Notifications.Management;

namespace OtpClipboard;

/// <summary>按需 UI 进程的生命周期控制器；后台状态通过本机管道访问。</summary>
public sealed class AppController : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly BackgroundClient _client = new();
    private readonly ObservableCollection<EventItem> _records = [];
    private MainWindow? _window;
    private readonly CancellationTokenSource _backgroundWatch = new();
    private Task? _backgroundWatchTask;
    private bool _disposed;
    private bool _exiting;

    public AppController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public ObservableCollection<EventItem> Records => _records;
    public AppPreferences Preferences { get; private set; } = new();
    public bool IsMonitoring { get; private set; }
    public bool IsTrayAvailable { get; private set; }
    public bool? StartupEnabled { get; private set; }
    public bool StartupCanChange { get; private set; }
    public string StartupMessage { get; private set; } = "";
    public Exception? StartupError { get; private set; }
    public UserNotificationListenerAccessStatus AccessStatus { get; private set; }
    public event Action? StateChanged;

    public async Task InitializeAsync()
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { Report($"无法连接后台进程：{ex.Message}"); }
    }

    public void ShowMainWindow()
    {
        if (_disposed) return;
        if (_window is not null)
        {
            _window.Activate();
            return;
        }
        var window = new MainWindow(this);
        _window = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window)) return;
            _window = null;
            if (!_exiting) RequestExit();
        };
        window.Activate();
    }

    public async Task RefreshAsync()
    {
        var snapshot = await _client.GetSnapshotAsync();
        Apply(snapshot);
    }

    public async Task<UserNotificationListenerAccessStatus> RequestAccessAsync()
    {
        Apply(await _client.RequestAccessAsync());
        return AccessStatus;
    }

    public async Task ToggleMonitoringAsync() => Apply(await _client.ToggleMonitoringAsync());
    public async Task ScanCurrentNotificationsAsync() => Apply(await _client.ScanAsync());
    public async Task ClearRecordsAsync() => Apply(await _client.ClearRecordsAsync());

    public async Task RecopyAsync(EventItem item)
    {
        if (!string.IsNullOrEmpty(item.Code))
            Apply(await _client.RecopyAsync(item.Code, item.Time));
    }

    public async Task UpdatePreferencesAsync(bool phoneLinkOnly, bool showPopup, bool minimizeToTray, bool autoCopy) =>
        Apply(await _client.UpdatePreferencesAsync(phoneLinkOnly, showPopup, minimizeToTray, autoCopy));

    public async Task SetStartupAsync(bool enabled) =>
        Apply(await _client.SetStartupAsync(enabled));
    public async Task RefreshStartupAsync()
    {
        var snapshot = await _client.GetStartupAsync();
        Apply(snapshot);
    }

    public async Task ShowPreviewAsync(string code) => await _client.PreviewPopupAsync(code);

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            await _client.ExitAsync();
            Application.Current.Exit();
        }
        catch
        {
            Application.Current.Exit();
        }
    }

    public void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        if (_dispatcher.HasThreadAccess) Application.Current.Exit();
        else _dispatcher.TryEnqueue(() => Application.Current.Exit());
    }

    public void Report(string message)
    {
        if (_dispatcher.HasThreadAccess) AddReport(message);
        else _dispatcher.TryEnqueue(() => AddReport(message));
    }

    private void AddReport(string message)
    {
        _records.Insert(0, EventItem.Create(EventKind.Info, "提示", message,
            DateTime.Now.ToString("HH:mm:ss")));
        StateChanged?.Invoke();
    }

    private void Apply(SnapshotResponse snapshot)
    {
        void Update()
        {
            IsMonitoring = snapshot.Monitoring;
            AccessStatus = (UserNotificationListenerAccessStatus)snapshot.AccessStatus;
            IsTrayAvailable = snapshot.TrayAvailable;
            StartupEnabled = snapshot.StartupEnabled;
            StartupCanChange = snapshot.StartupCanChange;
            StartupMessage = snapshot.StartupMessage;
            StartupError = string.IsNullOrEmpty(snapshot.StartupError)
                ? null
                : new InvalidOperationException(snapshot.StartupError);
            Preferences = new AppPreferences
            {
                AutoCopy = snapshot.AutoCopy,
                PhoneLinkOnly = snapshot.PhoneLinkOnly,
                ShowPopup = snapshot.ShowPopup,
                MinimizeToTray = snapshot.MinimizeToTray,
            };
            var items = snapshot.Records.Select(item =>
            {
                var kind = Enum.TryParse<EventKind>(item.Kind, true, out var parsed)
                    && Enum.IsDefined(parsed) ? parsed : EventKind.Info;
                return EventItem.Create(
                    kind, item.StatusText, item.Title, item.Detail, item.Code, item.Time);
            }).ToArray();
            _records.Clear();
            foreach (var item in items) _records.Add(item);
            StateChanged?.Invoke();
        }
        if (_dispatcher.HasThreadAccess) Update();
        else _dispatcher.TryEnqueue(Update);
    }

    public void StartBackgroundWatch()
    {
        _backgroundWatchTask ??= Task.Run(async () =>
        {
            while (!_backgroundWatch.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _backgroundWatch.Token);
                if (_client.IsAvailable()) continue;
                _dispatcher.TryEnqueue(() => Application.Current.Exit());
                return;
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backgroundWatch.Cancel();
        _backgroundWatch.Dispose();
        _window = null;
    }
}

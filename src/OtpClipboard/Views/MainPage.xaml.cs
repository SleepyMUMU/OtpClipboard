using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OtpClipboard.Models;
using OtpClipboard.Services;
using Windows.ApplicationModel;
using Windows.UI.Notifications.Management;

namespace OtpClipboard.Views;

/// <summary>概览、记录和设置；所有后台操作通过 AppController 发送给常驻进程。</summary>
public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<EventItem> _visible = [];
    private AppController? _controller;
    private bool _ready;
    private bool _initializing = true;
    private bool _refreshingPhone;

    public MainPage()
    {
        InitializeComponent();
        EventList.ItemsSource = _visible;
        _initializing = false;
        _ready = true;
        SetStatus(StatusKind.Neutral, "正在连接后台", "正在读取通知与监听状态。");
        UpdateSummary();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not AppController controller) return;
        _controller = controller;
        controller.StateChanged += OnStateChanged;
        ApplyState();
        await Task.WhenAll(RefreshPhoneAsync(), RefreshStartupAsync());
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_controller is not null) _controller.StateChanged -= OnStateChanged;
        _controller = null;
        _visible.Clear();
        base.OnNavigatedFrom(e);
    }

    private void OnStateChanged()
    {
        if (DispatcherQueue.HasThreadAccess) ApplyState();
        else DispatcherQueue.TryEnqueue(ApplyState);
    }

    private void ApplyState()
    {
        if (_controller is null || !_ready) return;
        _initializing = true;
        AutoCopyToggle.IsOn = _controller.Preferences.AutoCopy;
        PhoneLinkOnlyToggle.IsOn = _controller.Preferences.PhoneLinkOnly;
        PopupToggle.IsOn = _controller.Preferences.ShowPopup;
        TrayToggle.IsOn = _controller.Preferences.MinimizeToTray;
        _initializing = false;
        UpdateAccessUi(_controller.AccessStatus);
        SetMonitorButton(_controller.IsMonitoring);
        RebuildVisible();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (OverviewPanel is null || HistoryPanel is null || SettingsPanel is null) return;
        var selected = sender.SelectedItem;
        OverviewPanel.Visibility = selected == OverviewTab ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = selected == HistoryTab ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = selected == SettingsTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum StatusKind { Neutral, Success, Caution }

    private void SetStatus(StatusKind kind, string title, string message)
    {
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        StatusGlyph.Glyph = kind switch
        {
            StatusKind.Success => "\uE73E",
            StatusKind.Caution => "\uE7BA",
            _ => "\uE946",
        };
        VisualStateManager.GoToState(this, "Status" + kind, false);
    }

    private void SetMonitorButton(bool monitoring)
    {
        MonitorGlyph.Glyph = monitoring ? "\uE71A" : "\uE768";
        MonitorLabel.Text = monitoring ? "停止监听" : "开始监听";
    }

    private void UpdateAccessUi(UserNotificationListenerAccessStatus status)
    {
        bool allowed = status == UserNotificationListenerAccessStatus.Allowed;
        MonitorButton.IsEnabled = allowed;
        AuthorizeButton.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        if (_controller?.IsMonitoring == true)
            SetStatus(StatusKind.Success, "正在监听",
                "后台接管手机通知；验证码通知附带复制按钮，自动复制由设置开关控制。");
        else if (allowed)
            SetStatus(StatusKind.Neutral, "通知访问已授权", "点击“开始监听”恢复通知接管。");
        else
            SetStatus(StatusKind.Caution, "需要通知访问权限",
                "点击“授权通知访问”，并在系统提示中选择允许。");
    }

    private async void AuthorizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        try
        {
            var status = await _controller.RequestAccessAsync();
            UpdateAccessUi(status);
            _controller.Report(status == UserNotificationListenerAccessStatus.Allowed
                ? "通知读取已授权，后台已开始监听。"
                : "未获得通知读取权限。");
        }
        catch (Exception ex) { ReportError($"无法申请通知权限：{ex.Message}"); }
    }

    private async void MonitorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        try { await _controller.ToggleMonitoringAsync(); }
        catch (Exception ex) { ReportError($"无法切换监听：{ex.Message}"); }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        try
        {
            await _controller.ScanCurrentNotificationsAsync();
            Tabs.SelectedItem = HistoryTab;
        }
        catch (Exception ex) { ReportError($"扫描失败：{ex.Message}"); }
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearFlyout.Hide();
        if (_controller is null) return;
        try { await _controller.ClearRecordsAsync(); }
        catch (Exception ex) { ReportError($"清空失败：{ex.Message}"); }
    }

    private async void CopyRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is not null && sender is FrameworkElement { DataContext: EventItem item })
            await RecopyAsync(item);
    }

    private async void LatestCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (LatestCopied() is { } item) await RecopyAsync(item);
    }

    private async Task RecopyAsync(EventItem item)
    {
        if (_controller is null || string.IsNullOrEmpty(item.Code)) return;
        try { await _controller.RecopyAsync(item); }
        catch (Exception ex) { ReportError($"重新复制失败：{ex.Message}"); }
    }

    private async void Preference_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _initializing || _controller is null) return;
        try
        {
            await _controller.UpdatePreferencesAsync(
                PhoneLinkOnlyToggle.IsOn, PopupToggle.IsOn, TrayToggle.IsOn, AutoCopyToggle.IsOn);
        }
        catch (Exception ex) { ReportError($"设置保存失败：{ex.Message}"); }
    }

    private async Task RefreshStartupAsync()
    {
        try
        {
            if (_controller is null) return;
            await _controller.RefreshStartupAsync();
            ApplyState();
            ApplyStartupState(new StartupStatus(
                _controller.StartupEnabled switch
                {
                    true => StartupTaskState.Enabled,
                    false => StartupTaskState.Disabled,
                    null => null,
                },
                _controller.StartupError is null,
                _controller.StartupMessage,
                _controller.StartupError));
        }
        catch (Exception ex) { StartupDetail.Text = $"无法读取启动设置：{ex.Message}"; }
    }

    private void ApplyStartupState(StartupStatus state)
    {
        _initializing = true;
        StartupToggle.IsOn = state.IsEnabled == true;
        StartupToggle.IsEnabled = state.CanChange;
        StartupDetail.Text = state.Message;
        _initializing = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _initializing) return;
        bool requested = StartupToggle.IsOn;
        StartupToggle.IsEnabled = false;
        try
        {
            if (_controller is not null)
            {
                await _controller.SetStartupAsync(requested);
                ApplyState();
                ApplyStartupState(new StartupStatus(
                    _controller.StartupEnabled switch
                    {
                        true => StartupTaskState.Enabled,
                        false => StartupTaskState.Disabled,
                        null => null,
                    },
                    _controller.StartupError is null,
                    _controller.StartupMessage,
                    _controller.StartupError));
            }
            else ApplyStartupState(await StartupService.SetEnabledAsync(requested));
        }
        catch (Exception ex)
        {
            StartupDetail.Text = $"无法修改自动启动：{ex.Message}";
            await RefreshStartupAsync();
        }
    }

    private async Task RefreshPhoneAsync()
    {
        if (_refreshingPhone) return;
        _refreshingPhone = true;
        RefreshPhoneButton.IsEnabled = false;
        try
        {
            var state = await PhoneLinkService.RefreshAsync();
            PhoneLinkStatus.Text = state.IsRunning == true
                ? "手机连接 · 应用正在运行"
                : state.IsInstalled == false ? "手机连接 · 未检测到安装"
                : state.IsRunning == false ? "手机连接 · 应用未运行" : "手机连接 · 状态未知";
            PhoneLinkDetail.Text = state.Message;
        }
        catch (Exception ex) { PhoneLinkDetail.Text = $"检测失败：{ex.Message}"; }
        finally
        {
            _refreshingPhone = false;
            RefreshPhoneButton.IsEnabled = true;
        }
    }

    private async void RefreshPhoneButton_Click(object sender, RoutedEventArgs e) => await RefreshPhoneAsync();

    private async void OpenPhoneButton_Click(object sender, RoutedEventArgs e)
    {
        OpenPhoneButton.IsEnabled = false;
        try
        {
            if (!(await PhoneLinkService.OpenAsync()).Succeeded)
                ReportError("未能打开手机连接，请从开始菜单打开或安装该应用。");
            await RefreshPhoneAsync();
        }
        catch (Exception ex) { ReportError($"打开手机连接失败：{ex.Message}"); }
        finally { OpenPhoneButton.IsEnabled = true; }
    }

    private async void StartupSettings_Click(object sender, RoutedEventArgs e)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps")); }
        catch (Exception ex) { ReportError($"无法打开系统设置：{ex.Message}"); }
    }

    private async void TrayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        try
        {
            await _controller.UpdatePreferencesAsync(
                PhoneLinkOnlyToggle.IsOn, PopupToggle.IsOn, true, AutoCopyToggle.IsOn);
            _controller.RequestExit();
        }
        catch (Exception ex) { ReportError($"无法隐藏主界面：{ex.Message}"); }
    }

    private async void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is not null) await _controller.ExitAsync();
    }

    private async void PreviewPopup_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        try { await _controller.ShowPreviewAsync("246810"); }
        catch (Exception ex) { ReportError($"无法预览提示：{ex.Message}"); }
    }

    private void ReportError(string message)
    {
        _controller?.Report(message);
        SetStatus(StatusKind.Caution, "操作失败", message);
    }
}

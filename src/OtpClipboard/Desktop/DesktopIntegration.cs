using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using static OtpClipboard.Desktop.DesktopNativeMethods;

namespace OtpClipboard.Desktop;

/// <summary>
/// 原生托盘与无激活验证码浮窗。必须在 owner 所属 UI 线程创建、调用和释放，
/// 并在主窗口销毁前 Dispose；调用者负责将验证码复制到剪贴板。
/// showMain 应恢复并激活主窗口，exit 应执行应用自己的退出流程。
/// 不依赖 WinForms、WPF 或 WinUI 控件，也不修改 owner 的窗口过程。
/// </summary>
/// <remarks>
/// 依据 Microsoft Learn：notification-area、TaskbarCreated、GetGUIThreadInfo、
/// LogicalToPhysicalPointForPerMonitorDPI、WS_EX_NOACTIVATE、SendInput。
/// 浮窗是不透明的普通弹出窗口：圆角、阴影和描边交给 DWM（与系统菜单、工具提示一致），
/// 内容用 GDI ClearType 双缓冲绘制；分层属性只用于整窗透明度（淡入淡出），不手绘分层位图。
/// SendInput 只有全局输入队列接口，没有指定 HWND 的原子投递能力；本类通过
/// 短期事件钩子和发送前核验拒绝已观察到的目标变化，不尝试抢回目标焦点。
/// </remarks>
public sealed class DesktopIntegration : IDisposable
{
    private const int HotKeyId = 1;
    private const uint LifetimeMs = 8000;
    // 动画计时器挂在 host 上，与浮窗按序号递增的生命周期计时器互不冲突。
    private const int AnimationTimerId = 2;
    private const double ShowMs = 167, HideMs = 100; // Fluent 的 fast 时长；出现减速、消失加速。
    private readonly Action _showMain;
    private readonly Action _exit;
    private readonly Action<string> _status;
    private readonly uint _uiThread;
    private readonly IntPtr _module;
    private readonly string _className = "OtpClipboard.Desktop." + Guid.NewGuid().ToString("N");
    private readonly WindowProc _windowProc;
    private readonly WinEventProc _eventProc;
    private GCHandle _nativeRoot;
    private IntPtr _host, _popup, _icon, _foregroundHook, _focusHook;
    private uint _taskbarCreated;
    private bool _classRegistered, _trayV4, _hotKeyRegistered, _disposed, _placing, _menuOpen;
    private bool _visible, _pressed, _hover, _canFill, _preview, _roundedByDwm;
    private bool _copyOnly, _cardCopied;
    public Func<string, Task<bool>>? CopyRequested { get; set; }
    private string _code = string.Empty;
    private bool _lightTheme = true;
    private uint _accentColor, _dpi = 96;
    private int _hintWidth;
    private static string? s_iconFace;
    private IntPtr _target, _targetFocus;
    private uint _targetProcess, _targetThread, _shownAt;
    private UIntPtr _timer;
    private uint _timerSequence;
    private POINT _anchor;
    private bool _animating, _hiding;
    private long _animationStart;
    private int _popupX, _popupY, _slide;
    private byte _alpha = 255, _hideFrom;

    /// <summary>最近一次 Shell_NotifyIcon 注册是否成功；为 false 时调用者不应隐藏唯一主界面。</summary>
    public bool IsTrayAvailable { get; private set; }

    public DesktopIntegration(IntPtr owner, Action showMain, Action exit, Action<string> status)
    {
        ArgumentNullException.ThrowIfNull(showMain);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(status);
        _uiThread = GetCurrentThreadId();
        if (!IsWindow(owner) || GetWindowThreadProcessId(owner, out uint process) != _uiThread ||
            process != (uint)Environment.ProcessId)
            throw new ArgumentException("owner 必须是当前 UI 线程的有效窗口。", nameof(owner));

        _showMain = showMain;
        _exit = exit;
        _status = status;
        _module = GetModuleHandle(null);
        _windowProc = WndProc;
        _eventProc = OnTargetEvent;
        // 原生窗口/钩子不会被 GC 识别为引用。显式保活直到 Dispose，避免回调委托被回收。
        _nativeRoot = GCHandle.Alloc(this);
        try
        {
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var cls = new WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
                Instance = _module,
                ClassName = _className,
                Style = CS_DROPSHADOW, // 仅浮窗可见；Win11 上阴影随 DWM 圆角裁剪。
                Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32649)) // IDC_HAND，共享，无须销毁。
            };
            if (RegisterClassEx(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _classRegistered = true;
            IntPtr oldContext = SetThreadDpiAwarenessContext(PerMonitorV2);
            try
            {
                // 必须是隐藏顶层窗口：HWND_MESSAGE 收不到 Explorer 的广播。
                _host = CreateWindowEx(WS_EX_TOOLWINDOW, _className, "OtpClipboard 桌面集成",
                    WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
                if (_host == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _popup = CreateWindowEx(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED,
                    _className, "验证码提示", WS_POPUP, 0, 0, 1, 1,
                    _host, IntPtr.Zero, _module, IntPtr.Zero);
                if (_popup == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                SetAlpha(255); // 分层窗口在设置透明度之前不会显示。
                int corner = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND；Win10 上失败，改为自绘描边。
                _roundedByDwm = DwmSetWindowAttribute(_popup, 33, ref corner, sizeof(int)) == 0;
            }
            finally { if (oldContext != IntPtr.Zero) SetThreadDpiAwarenessContext(oldContext); }

            ChangeWindowMessageFilterEx(_host, _taskbarCreated, 1, IntPtr.Zero);
            _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
                1, 32, 32, 0x10); // IMAGE_ICON / LR_LOADFROMFILE，拥有该 HICON。
            if (_icon == IntPtr.Zero) _icon = CopyIcon(LoadIcon(IntPtr.Zero, new IntPtr(32512)));
            AddTrayIcon();
            _hotKeyRegistered = RegisterHotKey(_host, HotKeyId, 0x4003, 'O'); // Ctrl+Alt+O，无重复。
        }
        catch
        {
            Dispose();
            throw;
        }
        if (!IsTrayAvailable) Report("托盘暂不可用，请保留主界面；Explorer 恢复后会重新注册。");
        if (!_hotKeyRegistered) Report("Ctrl+Alt+O 快捷键注册失败，可通过托盘打开主界面。");
    }

    /// <summary>显示 8 秒。只接受 1～64 个非空白、非控制字符；重新调用会替换旧验证码与目标。</summary>
    public void ShowCode(string code) => ShowCode(code, preview: false);

    public bool ShowNotificationCard(string code)
    {
        ShowCode(code, preview: false, copyOnly: true);
        return _visible;
    }

    /// <summary>
    /// preview 为 true 时仅预览提示，不填充、不声称已复制。普通调用前由宿主复制验证码。
    /// 无外部输入焦点时仍显示提示，但禁止自动填充。
    /// </summary>
    public void ShowCode(string code, bool preview, bool copyOnly = false)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length is < 1 or > 64)
            throw new ArgumentException("验证码长度必须为 1～64 个 UTF-16 字符。", nameof(code));
        foreach (char c in code)
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                throw new ArgumentException("验证码不能包含空白或控制字符。", nameof(code));

        DismissCore();
        _copyOnly = copyOnly;
        _cardCopied = false;
        if (!IsTrayAvailable) AddTrayIcon(); // 仅在实际操作时重试，不轮询。
        _target = GetForegroundWindow();
        _targetThread = GetWindowThreadProcessId(_target, out _targetProcess);
        var info = GetTargetInfo();
        _targetFocus = info.Focus;
        _preview = preview;
        _canFill = !copyOnly && !preview && TargetMatches();
        _code = code;
        _lightTheme = UsesLightTheme();
        _accentColor = GetAccentColor(_lightTheme);
        _shownAt = GetTickCount();
        // 仅可填充提示订阅事件；纯提示不依赖外部焦点，也不安装任何钩子。
        if (_canFill)
        {
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _eventProc, 0, 0, 0);
            _focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS,
                IntPtr.Zero, _eventProc, 0, 0, 0);
            if (_foregroundHook == IntPtr.Zero || _focusHook == IntPtr.Zero)
            {
                UnhookTargetEvents();
                _canFill = false; // 监测失败时安全降级为手动粘贴提示。
            }
        }

        IntPtr oldContext = SetThreadDpiAwarenessContext(PerMonitorV2);
        try
        {
            if (!(copyOnly ? GetCursorPos(out _anchor) : TryCaretAnchor(info, out _anchor) || GetCursorPos(out _anchor)))
                throw new Win32Exception("无法获取验证码浮窗位置。");
            _visible = true;
            if (!PlacePopup(animate: !copyOnly) || (_canFill && !TargetMatches()))
                throw new Win32Exception("无法安全显示验证码浮窗。");
            if (!_visible) return; // 原生调用期间可能已收到目标变化事件。
            // 每次使用不同的 ID，使上一个浮窗已经排队的 WM_TIMER 不会关闭新浮窗。
            if (++_timerSequence == 0) ++_timerSequence;
            _timer = SetTimer(_popup, new UIntPtr(_timerSequence), LifetimeMs, IntPtr.Zero);
            if (_timer == UIntPtr.Zero) throw new Win32Exception("无法设置浮窗关闭时间。");
            InvalidateRect(_popup, IntPtr.Zero, false);
        }
        catch (Exception ex)
        {
            DismissCore();
            Report(ex.Message + " 请手动粘贴验证码。");
        }
        finally { if (oldContext != IntPtr.Zero) SetThreadDpiAwarenessContext(oldContext); }
    }

    public void DismissPopup()
    {
        VerifyAccess();
        if (!_disposed) DismissCore();
    }

    private GUITHREADINFO GetTargetInfo()
    {
        var info = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (_targetThread == 0 || !GetGUIThreadInfo(_targetThread, ref info)) return default;
        return info;
    }

    private bool TargetMatches()
    {
        if (_target == IntPtr.Zero || _targetFocus == IntPtr.Zero ||
            _targetProcess == (uint)Environment.ProcessId || GetForegroundWindow() != _target ||
            !IsWindow(_target) || !IsWindow(_targetFocus) ||
            GetWindowThreadProcessId(_target, out uint pid) != _targetThread || pid != _targetProcess)
            return false;
        var info = GetTargetInfo();
        GetWindowThreadProcessId(_targetFocus, out uint focusPid);
        return focusPid != 0 && focusPid != (uint)Environment.ProcessId &&
            info.Focus == _targetFocus && GetAncestor(info.Focus, 2) == _target &&
            (info.Flags & 0x1E) == 0; // 拒绝菜单、移动和缩放交互。
    }

    private static bool TryCaretAnchor(GUITHREADINFO info, out POINT point)
    {
        point = default;
        if (info.Caret == IntPtr.Zero || !IsWindow(info.Caret) ||
            info.CaretRect.Bottom <= info.CaretRect.Top) return false;
        // rcCaret 属于目标窗口的逻辑坐标，先在目标 DPI 上下文转换，再转物理屏幕坐标。
        IntPtr oldContext = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(info.Caret));
        if (oldContext == IntPtr.Zero) return false;
        try
        {
            point = new POINT(info.CaretRect.Left, info.CaretRect.Bottom);
            return ClientToScreen(info.Caret, ref point) &&
                LogicalToPhysicalPointForPerMonitorDPI(info.Caret, ref point);
        }
        finally { SetThreadDpiAwarenessContext(oldContext); }
    }

    private string PopupHint() =>
        _copyOnly ? (_cardCopied ? "已复制，可再次复制" : "复制验证码") :
        _preview ? "预览，不会复制" : _canFill ? "点击填入" : "已复制，按 Ctrl+V 粘贴";

    // Segoe Fluent Icons / MDL2 Assets 共用码位：View、Keyboard、Copy。
    private string PopupGlyph() => _preview ? "\uE890" : _canFill ? "\uE765" : "\uE8C8";

    private int Scale(int dip) => (int)Math.Round(dip * _dpi / 96.0);

    /// <summary>浮窗所用的 GDI 字体，按当前 DPI 创建，用完即释放。</summary>
    private sealed class PopupFonts : IDisposable
    {
        public readonly IntPtr Icon, Code, Hint;

        public PopupFonts(DesktopIntegration owner, IntPtr dc)
        {
            Icon = owner.CreatePopupFont(16, 400, IconFace(dc));
            Code = owner.CreatePopupFont(18, 600, "Segoe UI");
            Hint = owner.CreatePopupFont(12, 400, "Segoe UI"); // 中文经系统字体链接回退到微软雅黑 UI。
        }

        public void Dispose()
        {
            foreach (IntPtr font in new[] { Icon, Code, Hint })
                if (font != IntPtr.Zero) DeleteObject(font);
        }
    }

    private IntPtr CreatePopupFont(int dip, int weight, string face) =>
        CreateFont(-Scale(dip), 0, 0, 0, weight, 0, 0, 0, 1 /* DEFAULT_CHARSET */,
            0, 0, 5 /* CLEARTYPE_QUALITY */, 0, face);

    /// <summary>Win11 使用 Segoe Fluent Icons；Win10 没有时回退到 Segoe MDL2 Assets。</summary>
    private static string IconFace(IntPtr dc)
    {
        if (s_iconFace is not null) return s_iconFace;
        const string fluent = "Segoe Fluent Icons";
        IntPtr font = CreateFont(-16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, fluent);
        if (font == IntPtr.Zero) return "Segoe MDL2 Assets";
        IntPtr old = SelectObject(dc, font);
        var face = new char[64];
        int length = GetTextFace(dc, face.Length, face);
        SelectObject(dc, old);
        DeleteObject(font);
        s_iconFace = length > 0 && new string(face, 0, length - 1) == fluent ? fluent : "Segoe MDL2 Assets";
        return s_iconFace;
    }

    private const int CodeSpacing = 1;

    /// <summary>测量内容所需宽度（物理像素）。布局：图标 | 验证码 | 分隔线 | 提示。</summary>
    private (int Width, int Height) MeasurePopup()
    {
        IntPtr dc = GetDC(_popup);
        if (dc == IntPtr.Zero) return (0, 0);
        try
        {
            using var fonts = new PopupFonts(this, dc);
            IntPtr old = SelectObject(dc, fonts.Code);
            SetTextCharacterExtra(dc, Scale(CodeSpacing));
            GetTextExtentPoint32(dc, _code, _code.Length, out SIZE code);
            SetTextCharacterExtra(dc, 0);
            SelectObject(dc, fonts.Hint);
            string hint = PopupHint();
            GetTextExtentPoint32(dc, hint, hint.Length, out SIZE hintSize);
            SelectObject(dc, old);
            _hintWidth = hintSize.Width;
            int width = Scale(12) + Scale(16) + Scale(10) + code.Width + Scale(12) + Math.Max(1, Scale(1)) +
                Scale(12) + _hintWidth + Scale(14);
            return (Math.Clamp(width, Scale(140), Scale(420)), Scale(40));
        }
        finally { ReleaseDC(_popup, dc); }
    }

    private static bool UsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch { return true; }
    }

    /// <summary>与 WinUI AccentFillColorDefault 一致：浅色主题取 Dark1，深色主题取 Light2。</summary>
    private static uint GetAccentColor(bool light)
    {
        try
        {
            // AccentPalette 为 8 个 RGBA：Light3、Light2、Light1、Base、Dark1、Dark2、Dark3、保留。
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 32)
            {
                int i = (light ? 4 : 1) * 4;
                return ColorRgb(palette[i], palette[i + 1], palette[i + 2]);
            }
        }
        catch { }
        return light ? ColorRgb(0, 95, 184) : ColorRgb(96, 205, 255);
    }

    private static uint ColorRgb(byte red, byte green, byte blue) =>
        red | (uint)(green << 8) | (uint)(blue << 16);

    private static bool IsHighContrast() => GetSystemMetrics(SM_HIGHCONTRAST) != 0;

    private bool PlacePopup(bool animate)
    {
        if (_placing) return true;
        _placing = true;
        try
        {
            // 上一个浮窗可能仍在淡出；先让它不可见，避免下面临时缩成 1×1 时闪一下。
            StopAnimation();
            if (animate) SetAlpha(0);
            var monitor = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromPoint(_anchor, 2), ref monitor)) return false;
            // 先把隐藏窗口放入目标显示器，再获取该窗口真实的每显示器 DPI。
            if (!SetWindowPos(_popup, HwndTopmost, _anchor.X, _anchor.Y, 1, 1, SWP_NOACTIVATE)) return false;
            _dpi = GetDpiForWindow(_popup);
            if (_dpi == 0) _dpi = 96;
            RECT work = monitor.Work;
            var size = MeasurePopup();
            int width = Math.Min(size.Width, work.Right - work.Left);
            int height = Math.Min(size.Height, work.Bottom - work.Top);
            if (width <= 0 || height <= 0) return false;
            int x = Math.Clamp(_anchor.X + Scale(10), work.Left, work.Right - width);
            int y = _anchor.Y + Scale(10);
            if ((long)y + height > work.Bottom) y = _anchor.Y - height - Scale(10);
            y = Math.Clamp(y, work.Top, work.Bottom - height);
            if (_copyOnly)
            {
                x = Math.Max(work.Left, work.Right - width - Scale(16));
                y = Math.Max(work.Top, work.Bottom - height - Scale(16));
            }
            // 从锚点一侧滑入：浮窗在锚点下方时自上而下，在上方时自下而上。
            _popupX = x;
            _popupY = y;
            _slide = animate && AnimationsEnabled() ? (y >= _anchor.Y ? -Scale(8) : Scale(8)) : 0;
            if (_slide == 0) SetAlpha(255);
            if (!SetWindowPos(_popup, HwndTopmost, x, y + _slide, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW)) return false;
            // 立即同步绘制，避免 DWM 先合成出未绘制的空白帧。
            InvalidateRect(_popup, IntPtr.Zero, false);
            UpdateWindow(_popup);
            if (_slide != 0) StartAnimation(hiding: false);
            return true;
        }
        finally { _placing = false; }
    }

    /// <summary>遵循“设置 → 辅助功能 → 动画效果”；读取失败时按开启处理。</summary>
    private static bool AnimationsEnabled() =>
        !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out int enabled, 0) || enabled != 0;

    private void SetAlpha(byte alpha)
    {
        _alpha = alpha;
        SetLayeredWindowAttributes(_popup, 0, alpha, LWA_ALPHA);
    }

    private void StartAnimation(bool hiding)
    {
        _hiding = hiding;
        _hideFrom = _alpha;
        _animationStart = Stopwatch.GetTimestamp();
        _animating = SetTimer(_host, new UIntPtr(AnimationTimerId), 10, IntPtr.Zero) != UIntPtr.Zero;
        if (!_animating) FinishAnimation(); // 计时器不可用时直接跳到终态。
    }

    private void StopAnimation()
    {
        if (_animating) KillTimer(_host, new UIntPtr(AnimationTimerId));
        _animating = false;
    }

    private void FinishAnimation()
    {
        StopAnimation();
        if (_hiding) { ShowWindow(_popup, 0); SetAlpha(255); }
        else
        {
            SetAlpha(255);
            SetWindowPos(_popup, IntPtr.Zero, _popupX, _popupY, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOZORDER);
        }
    }

    private void AnimationTick()
    {
        double t = Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds / (_hiding ? HideMs : ShowMs);
        if (t >= 1) { FinishAnimation(); return; }
        if (_hiding)
        {
            SetAlpha((byte)Math.Round(_hideFrom * (1 - t * t))); // 加速淡出
            return;
        }
        double eased = 1 - Math.Pow(1 - t, 3); // 三次缓出
        SetAlpha((byte)Math.Round(255 * eased));
        SetWindowPos(_popup, IntPtr.Zero, _popupX, _popupY + (int)Math.Round(_slide * (1 - eased)), 0, 0,
            SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOZORDER);
    }

    private void OnTargetEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId,
        int childId, uint thread, uint time)
    {
        // OUTOFCONTEXT 回调在安装钩子的线程执行；无 AttachThreadInput、无永久计时器。
        try
        {
            if (_disposed || !_visible || !_canFill || unchecked((int)(time - _shownAt)) < 0) return;
            if ((eventType == EVENT_SYSTEM_FOREGROUND && hwnd != _target) ||
                (eventType == EVENT_OBJECT_FOCUS && hwnd != _targetFocus) || !TargetMatches())
            {
                DismissCore();
                Report("输入目标已变化，已取消自动填充，请手动粘贴验证码。");
            }
        }
        catch { DismissCore(); } // 不能把托管异常传播进原生事件分发。
    }

    private void FillCode()
    {
        if (!_visible) return;
        if (_copyOnly)
        {
            if (unchecked(GetTickCount() - _shownAt) >= LifetimeMs) { DismissCore(); return; }
            _ = CopyCardAsync(_code, _timerSequence);
            return;
        }
        if (!_canFill)
        {
            bool preview = _preview;
            DismissCore();
            Report(preview ? "预览已关闭；预览不会复制或填入验证码。" : "验证码已复制，请手动粘贴。");
            return;
        }
        if (unchecked(GetTickCount() - _shownAt) >= LifetimeMs || !TargetMatches() || ModifiersDown())
        {
            DismissCore();
            Report("输入目标已变化、验证码已超时或修饰键未释放，请手动粘贴验证码。");
            return;
        }
        var inputs = new INPUT[_code.Length * 2];
        for (int i = 0; i < _code.Length; i++)
        {
            inputs[i * 2] = UnicodeInput(_code[i], false);
            inputs[i * 2 + 1] = UnicodeInput(_code[i], true);
        }
        // 构造完全部事件后再核验；一次 SendInput 投递，不逐字切换目标，不发送 Enter。
        if (!TargetMatches())
        {
            DismissCore();
            Report("输入目标已变化，请手动粘贴验证码。");
            return;
        }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        DismissCore();
        Report(sent == inputs.Length ? "已发送验证码填充，请确认目标输入框。" :
            "自动填充失败或仅完成部分输入（可能受权限限制），请检查输入框后手动粘贴验证码。");
    }

    private async Task CopyCardAsync(string code, uint generation)
    {
        try
        {
            bool copied = CopyRequested is not null && await CopyRequested(code);
            if (_disposed || !_visible || !_copyOnly || generation != _timerSequence) return;
            _cardCopied = copied;
            if (!copied) Report("复制失败，请再次点击复制验证码。");
            PlacePopup(animate: false);
        }
        catch (Exception ex) { Report("复制失败：" + ex.Message); }
    }

    private static INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        Type = 1,
        Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Scan = c, Flags = keyUp ? 6u : 4u } }
    };

    private static bool ModifiersDown() =>
        (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    private NOTIFYICONDATA TrayData() => new()
    {
        Size = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), Window = _host, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, Callback = WM_APP_TRAY, Icon = _icon,
        Tip = "验证码剪贴板 — 打开主界面", Info = string.Empty, InfoTitle = string.Empty
    };

    private void AddTrayIcon()
    {
        var data = TrayData();
        IsTrayAvailable = _icon != IntPtr.Zero &&
            (Shell_NotifyIcon(NIM_ADD, ref data) || Shell_NotifyIcon(NIM_MODIFY, ref data));
        data.Version = 4;
        _trayV4 = IsTrayAvailable && Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private void ShowTrayMenu(UIntPtr position)
    {
        if (_menuOpen) return;
        DismissCore();
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) { Report("无法打开托盘菜单，请使用 Ctrl+Alt+O。"); return; }
        uint command = 0;
        try
        {
            if (!AppendMenu(menu, 0, new UIntPtr(1), "打开(&O)") ||
                !AppendMenu(menu, 0, new UIntPtr(2), "退出(&X)")) return;
            POINT point;
            if (_trayV4)
                point = new POINT(unchecked((short)position.ToUInt64()), unchecked((short)(position.ToUInt64() >> 16)));
            else if (!GetCursorPos(out point)) return;
            if (point.X == -1 && point.Y == -1 && !GetCursorPos(out point)) return;
            // 仅用户主动打开托盘菜单时激活 host，满足 TrackPopupMenu 的菜单消失要求。
            if (!SetForegroundWindow(_host)) return;
            _menuOpen = true;
            command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _host, IntPtr.Zero);
        }
        finally
        {
            _menuOpen = false;
            DestroyMenu(menu);
            if (!_disposed)
            {
                PostMessage(_host, WM_NULL, UIntPtr.Zero, IntPtr.Zero);
                var data = TrayData();
                Shell_NotifyIcon(NIM_SETFOCUS, ref data);
            }
        }
        if (_disposed) return;
        if (command == 1) _showMain();
        else if (command == 2) _exit();
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, UIntPtr wp, IntPtr lp)
    {
        try
        {
            if (!_disposed && hwnd == _host)
            {
                if (message == _taskbarCreated)
                {
                    IsTrayAvailable = false;
                    AddTrayIcon();
                    if (!IsTrayAvailable) Report("托盘恢复失败，请通过 Ctrl+Alt+O 打开主界面。");
                    return IntPtr.Zero;
                }
                if (message == WM_TIMER && wp.ToUInt64() == AnimationTimerId)
                { if (_animating) AnimationTick(); return IntPtr.Zero; }
                if (message == WM_HOTKEY && wp.ToUInt64() == HotKeyId)
                { DismissCore(); _showMain(); return IntPtr.Zero; }
                if (message == WM_APP_TRAY)
                {
                    uint notification = _trayV4 ? (uint)(lp.ToInt64() & 0xFFFF) : unchecked((uint)lp.ToInt64());
                    if (notification == WM_CONTEXTMENU || (!_trayV4 && notification == WM_RBUTTONUP)) ShowTrayMenu(wp);
                    else if (notification is NIN_SELECT or NIN_KEYSELECT || (!_trayV4 && notification == WM_LBUTTONUP))
                    { DismissCore(); _showMain(); }
                    return IntPtr.Zero;
                }
            }
            if (!_disposed && hwnd == _popup)
            {
                switch (message)
                {
                    case WM_MOUSEACTIVATE: return new IntPtr(3); // MA_NOACTIVATE，仍接收点击。
                    case WM_ERASEBKGND: return new IntPtr(1);
                    case WM_PAINT:
                        // 淡出时状态已清空，保留最后一帧，不重绘成空白。
                        if (_visible) PaintPopup(); else ConsumeInvalidatedRegion(hwnd);
                        return IntPtr.Zero;
                    case WM_MOUSEMOVE:
                        if (_visible && !_hover)
                        {
                            var track = new TRACKMOUSEEVENT
                            {
                                Size = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = TME_LEAVE, Window = _popup
                            };
                            _hover = TrackMouseEvent(ref track);
                            InvalidateRect(_popup, IntPtr.Zero, false);
                        }
                        return IntPtr.Zero;
                    case WM_MOUSELEAVE:
                        _hover = false;
                        InvalidateRect(_popup, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case WM_LBUTTONDOWN:
                        if (_visible) { _pressed = true; SetCapture(_popup); InvalidateRect(_popup, IntPtr.Zero, false); }
                        return IntPtr.Zero;
                    case WM_LBUTTONUP:
                        bool pressed = _pressed;
                        _pressed = false;
                        if (GetCapture() == _popup) ReleaseCapture();
                        GetClientRect(_popup, out RECT client);
                        int x = unchecked((short)lp.ToInt64()), y = unchecked((short)(lp.ToInt64() >> 16));
                        if (pressed && x >= 0 && x < client.Right && y >= 0 && y < client.Bottom) FillCode();
                        return IntPtr.Zero;
                    case WM_CAPTURECHANGED:
                        _pressed = false;
                        InvalidateRect(_popup, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case WM_RBUTTONUP:
                    case WM_CLOSE: DismissCore(); return IntPtr.Zero;
                    case WM_TIMER:
                        if (_timer != UIntPtr.Zero && wp == _timer) DismissCore();
                        return IntPtr.Zero;
                    case WM_DPICHANGED:
                        if (_visible && !_placing && !PlacePopup(animate: false)) DismissCore();
                        InvalidateRect(_popup, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    case WM_DISPLAYCHANGE:
                    case WM_SETTINGCHANGE:
                        if (_visible) DismissCore();
                        return IntPtr.Zero;
                }
            }
        }
        catch (Exception)
        {
            DismissCore();
            Report("桌面集成操作失败，请打开主界面或手动粘贴验证码。");
        }
        return DefWindowProc(hwnd, message, wp, lp);
    }

    private readonly record struct PopupColors(uint Background, uint Hover, uint Pressed, uint Text,
        uint Secondary, uint Divider, uint Border, uint Accent);

    /// <summary>取自 Win11 浮出控件的实色回退色；高对比度时完全使用系统颜色。</summary>
    private PopupColors Palette()
    {
        if (IsHighContrast())
        {
            uint window = GetSysColor(5), text = GetSysColor(8);
            return new(window, window, window, text, text, text, text, GetSysColor(13));
        }
        return _lightTheme
            ? new(ColorRgb(252, 252, 252), ColorRgb(243, 243, 243), ColorRgb(237, 237, 237), ColorRgb(27, 27, 27),
                ColorRgb(96, 96, 96), ColorRgb(224, 224, 224), ColorRgb(200, 200, 200), _accentColor)
            : new(ColorRgb(44, 44, 44), ColorRgb(56, 56, 56), ColorRgb(50, 50, 50), ColorRgb(255, 255, 255),
                ColorRgb(206, 206, 206), ColorRgb(70, 70, 70), ColorRgb(80, 80, 80), _accentColor);
    }

    private void PaintPopup()
    {
        IntPtr dc = BeginPaint(_popup, out PAINTSTRUCT paint);
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        try
        {
            if (dc == IntPtr.Zero || !GetClientRect(_popup, out RECT client)) return;
            int width = client.Right, height = client.Bottom;
            if (width <= 0 || height <= 0) return;
            // 双缓冲：悬停/按下重绘时不闪烁。
            memory = CreateCompatibleDC(dc);
            bitmap = memory == IntPtr.Zero ? IntPtr.Zero : CreateCompatibleBitmap(dc, width, height);
            if (bitmap == IntPtr.Zero) return;
            oldBitmap = SelectObject(memory, bitmap);
            DrawPopup(memory, width, height);
            BitBlt(dc, 0, 0, width, height, memory, 0, 0, SRCCOPY);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memory, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (dc != IntPtr.Zero) EndPaint(_popup, ref paint);
        }
    }

    /// <summary>
    /// 隐藏窗口也必须通过 BeginPaint/EndPaint 清空无效区；仅调用 ValidateRect
    /// 时，系统可能在隐藏的分层窗口上反复投递 WM_PAINT，造成消息循环空转。
    /// </summary>
    private static void ConsumeInvalidatedRegion(IntPtr hwnd)
    {
        IntPtr dc = BeginPaint(hwnd, out PAINTSTRUCT paint);
        if (dc != IntPtr.Zero) EndPaint(hwnd, ref paint);
    }

    private void DrawPopup(IntPtr dc, int width, int height)
    {
        var colors = Palette();
        var all = new RECT { Right = width, Bottom = height };
        FillSolid(dc, all, _pressed ? colors.Pressed : _hover ? colors.Hover : colors.Background);
        if (!_roundedByDwm) FrameSolid(dc, all, colors.Border); // Win11 由 DWM 绘制圆角描边。
        if (!_visible) return;

        using var fonts = new PopupFonts(this, dc);
        const uint Line = DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX;
        SetBkMode(dc, 1); // TRANSPARENT
        IntPtr oldFont = SelectObject(dc, fonts.Icon);
        try
        {
            var icon = new RECT { Left = Scale(12), Right = Scale(12) + Scale(16), Bottom = height };
            SetTextColor(dc, colors.Accent);
            string glyph = PopupGlyph();
            DrawText(dc, glyph, glyph.Length, ref icon, Line | DT_CENTER);

            // 自右向左排布：提示文字完整显示，过长的验证码以省略号截断。
            int divider = Math.Max(1, Scale(1));
            var hint = new RECT { Right = width - Scale(14), Bottom = height };
            hint.Left = Math.Max(icon.Right, hint.Right - _hintWidth);
            var line = new RECT { Right = hint.Left - Scale(12), Top = (height - Scale(16)) / 2 };
            line.Left = line.Right - divider;
            line.Bottom = line.Top + Scale(16);
            var code = new RECT { Left = icon.Right + Scale(10), Right = line.Left - Scale(12), Bottom = height };

            SelectObject(dc, fonts.Code);
            SetTextColor(dc, colors.Text);
            SetTextCharacterExtra(dc, Scale(CodeSpacing));
            DrawText(dc, _code, _code.Length, ref code, Line | DT_END_ELLIPSIS);
            SetTextCharacterExtra(dc, 0);

            FillSolid(dc, line, colors.Divider);

            SelectObject(dc, fonts.Hint);
            // 可点击填入时提示用强调色，表明这是一个操作。
            SetTextColor(dc, _canFill ? colors.Accent : colors.Secondary);
            string text = PopupHint();
            DrawText(dc, text, text.Length, ref hint, Line);
        }
        finally { SelectObject(dc, oldFont); }
    }

    private static void FillSolid(IntPtr dc, RECT rect, uint color)
    {
        IntPtr brush = CreateSolidBrush(color);
        if (brush == IntPtr.Zero) return;
        FillRect(dc, ref rect, brush);
        DeleteObject(brush);
    }

    private static void FrameSolid(IntPtr dc, RECT rect, uint color)
    {
        IntPtr brush = CreateSolidBrush(color);
        if (brush == IntPtr.Zero) return;
        FrameRect(dc, ref rect, brush);
        DeleteObject(brush);
    }

    private void DismissCore()
    {
        _visible = false;
        _pressed = _hover = false;
        if (_timer != UIntPtr.Zero) { KillTimer(_popup, _timer); _timer = UIntPtr.Zero; }
        UnhookTargetEvents();
        if (_popup != IntPtr.Zero)
        {
            if (GetCapture() == _popup) ReleaseCapture();
            bool shown = IsWindowVisible(_popup);
            StopAnimation();
            // 已清空状态，淡出期间点击不再生效；释放时直接隐藏。
            if (shown && !_disposed && AnimationsEnabled()) StartAnimation(hiding: true);
            else ShowWindow(_popup, 0);
        }
        _code = string.Empty;
        _canFill = _preview = _copyOnly = _cardCopied = false;
        _target = _targetFocus = IntPtr.Zero;
        _targetProcess = _targetThread = 0;
    }

    private void UnhookTargetEvents()
    {
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_focusHook != IntPtr.Zero) { UnhookWinEvent(_focusHook); _focusHook = IntPtr.Zero; }
    }

    private void VerifyAccess()
    {
        if (GetCurrentThreadId() != _uiThread)
            throw new InvalidOperationException("请通过主窗口 DispatcherQueue 在创建线程调用 DesktopIntegration。");
    }

    private void Report(string text)
    {
        // status 是宿主提供的代码，禁止其异常越过原生回调边界。
        try { _status(text); } catch { }
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        DismissCore();
        if (_menuOpen) EndMenu();
        if (_hotKeyRegistered) { UnregisterHotKey(_host, HotKeyId); _hotKeyRegistered = false; }
        if (_host != IntPtr.Zero)
        {
            var data = TrayData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        IsTrayAvailable = false;
        if (_popup != IntPtr.Zero) { DestroyWindow(_popup); _popup = IntPtr.Zero; }
        if (_host != IntPtr.Zero) { DestroyWindow(_host); _host = IntPtr.Zero; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_classRegistered) { UnregisterClass(_className, _module); _classRegistered = false; }
        if (_nativeRoot.IsAllocated) _nativeRoot.Free();
        GC.SuppressFinalize(this);
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using static OtpClipboard.Desktop.DesktopNativeMethods;

namespace OtpClipboard.Desktop;

/// <summary>后台模式的原生消息窗口，只负责托盘、快捷键和消息分发。</summary>
internal sealed partial class DesktopHostWindow : IDisposable
{
    private const string ClassName = "OtpClipboard.DesktopHost";
    private readonly WindowProc _windowProc;
    private readonly uint _uiThread;
    private GCHandle _root;
    private IntPtr _handle;
    private bool _disposed;

    public DesktopHostWindow()
    {
        _uiThread = GetCurrentThreadId();
        _windowProc = WndProc;
        _root = GCHandle.Alloc(this);
        try
        {
            var windowClass = new WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
                Instance = GetModuleHandle(null),
                ClassName = ClassName,
            };
            if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _handle = CreateWindowEx(WS_EX_TOOLWINDOW, ClassName, "OtpClipboard 后台",
                WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
            if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IntPtr Handle => _handle;
    public bool IsMessageThread => GetCurrentThreadId() == _uiThread;

    public void PostClose()
    {
        if (!_disposed && _handle != IntPtr.Zero) PostMessage(_handle, WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
    }

    public void NotifyHostMessage() =>
        PostMessage(_handle, WM_NULL, UIntPtr.Zero, IntPtr.Zero);

    public void WaitForHostMessage(WaitHandle wake)
    {
        // COM/WinRT can inspect the queue during a callback without consuming all input.
        // Also wake for already-seen input instead of waiting for another user action.
        uint result = MsgWaitForMultipleObjectsEx(1, [wake.SafeWaitHandle.DangerousGetHandle()],
            Infinite, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>取出并分发已排队的窗口消息。消息队列必须清空，否则 MsgWait 会立即返回并忙等。</summary>
    public bool PumpPendingMessages()
    {
        while (PeekMessage(out MSG message, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (message.Message == WM_QUIT) return false;
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WM_CLOSE)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
        if (_root.IsAllocated) _root.Free();
        GC.SuppressFinalize(this);
    }
}

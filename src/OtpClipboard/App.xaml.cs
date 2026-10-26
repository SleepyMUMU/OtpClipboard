using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace OtpClipboard;

public partial class App : Application
{
    private AppController? _controller;
    private AppInstance? _instance;

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Contains("--ui", StringComparer.OrdinalIgnoreCase)) return RunUi();

        using var mutex = new Mutex(true, Api.BridgeNames.Mutex, out bool first);
        if (!first)
        {
            try { new Api.BackgroundClient().ShowWindowAsync().GetAwaiter().GetResult(); }
            catch { }
            return 0;
        }

        using var background = new BackgroundController();
        // StartAsync 会把初始化排入后台 Dispatcher；先进入消息泵，初始化才能得到执行。
        var startup = background.StartAsync();
        background.RunMessageLoop();
        startup.GetAwaiter().GetResult();
        return 0;
    }

    private static int RunUi()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App();
            GC.KeepAlive(app);
        });
        return 0;
    }

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        _instance = AppInstance.FindOrRegisterForKey(Api.BridgeNames.AppInstance);
        if (!_instance.IsCurrent)
        {
            await _instance.RedirectActivationToAsync(activation);
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("无法取得 UI 线程的 DispatcherQueue。");
        _instance.Activated += (_, redirected) =>
        {
            if (redirected.Kind == ExtendedActivationKind.StartupTask) return;
            dispatcher.TryEnqueue(() => _controller?.ShowMainWindow());
        };

        _controller = new AppController(dispatcher);
        await _controller.InitializeAsync();
        _controller.StartBackgroundWatch();
        _controller.ShowMainWindow();
    }
}

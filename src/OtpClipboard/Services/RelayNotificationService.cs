using System.Security;
using System.Diagnostics;
using Microsoft.Windows.AppNotifications;

namespace OtpClipboard.Services;

public sealed record NotificationDeliveryDiagnostics(string Setting, int ActiveCount,
    uint LastId, DateTimeOffset? LastSubmittedAt, double SubmissionMilliseconds,
    bool LastNotificationPresent, double ClipboardMilliseconds);

/// <summary>本机通知接管；按钮参数只携带随机 ID，验证码只在内存保存。</summary>
public sealed class RelayNotificationService : IDisposable
{
    private readonly SynchronizationContext _context;
    private readonly Action<string> _report;
    private readonly Dictionary<string, (string Code, DateTimeOffset Until)> _codes = new();
    private readonly System.Threading.Timer _cleanup;
    private bool _registered;
    private uint _lastId;
    private DateTimeOffset? _lastSubmittedAt;
    private double _submissionMilliseconds;
    public double ClipboardMilliseconds { get; set; }
    public event Action<string>? Copied;
    public Func<string, bool>? ShowCodeCard { get; set; }

    public RelayNotificationService(SynchronizationContext context, Action<string> report)
    {
        _context = context;
        _report = report;
        AppNotificationManager.Default.NotificationInvoked += OnInvoked;
        AppNotificationManager.Default.Register();
        _registered = true;
        _cleanup = new(_ => _context.Post(_ => Prune(), null), null, 60000, 60000);
    }

    private void Prune()
    {
        foreach (var key in _codes.Where(x => x.Value.Until <= DateTimeOffset.Now).Select(x => x.Key).ToArray())
            _codes.Remove(key);
    }

    public bool Show(string body, string? code, bool autoCopyEnabled = false, bool preview = false)
    {
        if (code is not null && ShowCodeCard is not null)
        {
            var display = Stopwatch.StartNew();
            if (ShowCodeCard(code))
            {
                DeliveryTrace.Write("card-shown", 0, display.Elapsed.TotalMilliseconds);
                return true;
            }
            // If the desktop window cannot be displayed, keep the ordinary toast as a fallback.
        }
        Prune();
        string token = Guid.NewGuid().ToString("N");
        string title = preview ? "验证码助手 · 测试通知" : code is null ? "手机通知" : "收到验证码";
        string text = code is null ? body : $"验证码：{code}\n{body}";
        string hint = code is null ? "来自手机连接；回复请打开手机连接" : autoCopyEnabled ? "自动复制已开启，也可点击按钮复制" : "点击下方按钮复制";
        if (text.Length > 500) text = text[..500] + "…";
        string action = code is null ? "" : $"<actions><action content=\"复制验证码\" arguments=\"copy={token}\" activationType=\"foreground\"/></actions>";
        var notification = new AppNotification($"<toast launch=\"open\"><visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text><text placement=\"attribution\">{SecurityElement.Escape(hint)}</text></binding></visual>{action}<audio silent=\"true\"/></toast>")
        {
            // Replace an earlier code instead of letting old code banners queue behind one another.
            Tag = code is null ? token[..16] : "latest-code", Group = "phone-relay", Expiration = DateTimeOffset.Now.AddMinutes(5),
            Priority = AppNotificationPriority.Default,
            SuppressDisplay = false,
        };
        if (code is not null)
        {
            if (_codes.Count >= 200) _codes.Remove(_codes.Keys.First());
            _codes[token] = (code, notification.Expiration);
        }
        try
        {
            var submission = Stopwatch.StartNew();
            AppNotificationManager.Default.Show(notification);
            _submissionMilliseconds = submission.Elapsed.TotalMilliseconds;
            _lastId = notification.Id;
            _lastSubmittedAt = DateTimeOffset.Now;
            // Acceptance is not proof that Windows actually displayed a banner.
            DeliveryTrace.Write("toast-accepted", notification.Id, _submissionMilliseconds);
            if (notification.Id != 0) return true;
            _codes.Remove(token);
            return false;
        }
        catch { _codes.Remove(token); throw; }
    }

    public async Task<NotificationDeliveryDiagnostics> GetDiagnosticsAsync()
    {
        var manager = AppNotificationManager.Default;
        var active = await manager.GetAllAsync();
        return new(manager.Setting.ToString(), active.Count, _lastId, _lastSubmittedAt,
            _submissionMilliseconds, active.Any(n => n.Id == _lastId), ClipboardMilliseconds);
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        string argument = args.Argument;
        _context.Post(async _ =>
        {
            try
            {
                if (!argument.StartsWith("copy=", StringComparison.Ordinal)) return;
                Prune();
                if (!_codes.TryGetValue(argument[5..], out var entry))
                { _report("此通知已过期或后台已重启，请重新获取验证码。"); return; }
                var result = await ClipboardService.TryCopyAsync(entry.Code);
                if (result.Success) { Copied?.Invoke(entry.Code); _report("验证码已复制。"); }
                else _report("复制失败，请重试：" + result.Error);
            }
            catch (Exception ex) { _report("通知操作失败：" + ex.Message); }
        }, null);
    }

    public void Dispose()
    {
        _cleanup.Dispose();
        _codes.Clear();
        if (_registered)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }
}

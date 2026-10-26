using OtpClipboard.Models;

namespace OtpClipboard.Api;

internal sealed record PipeRequest(
    string Command,
    bool? PhoneLinkOnly = null,
    bool? ShowPopup = null,
    bool? MinimizeToTray = null,
    string? Code = null,
    string? Time = null,
    bool? StartupEnabled = null, bool? AutoCopy = null);

internal sealed record PipeResponse(
    bool Success,
    string? Error = null,
    SnapshotResponse? Snapshot = null,
    Services.NotificationDeliveryDiagnostics? Diagnostics = null);

internal sealed record SnapshotResponse(
    bool Monitoring,
    int AccessStatus,
    bool TrayAvailable,
    bool PhoneLinkOnly,
    bool ShowPopup,
    bool MinimizeToTray,
    IReadOnlyList<MonitorRecord> Records,
    bool? StartupEnabled = null,
    bool StartupCanChange = false,
    string StartupMessage = "",
    string? StartupError = null, bool AutoCopy = false);

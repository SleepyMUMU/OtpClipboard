using System.Text.Json;
using System.Threading.Channels;

namespace OtpClipboard.Services;

/// <summary>Bounded timing-only diagnostics. Never accepts message text or OTP values.</summary>
internal static class DeliveryTrace
{
    private static readonly Channel<Entry> Entries = Channel.CreateBounded<Entry>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private static int _started;
    private sealed record Entry(DateTimeOffset At, string Stage, uint Id,
        double? Milliseconds, DateTimeOffset? SourceCreatedAt);

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        string path = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "delivery-timing.jsonl");
        _ = Task.Run(async () =>
        {
            await foreach (var entry in Entries.Reader.ReadAllAsync())
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                        File.Move(path, path + ".previous", overwrite: true);
                    await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Diagnostics must not interrupt notification delivery.
                }
            }
        });
    }

    public static void Write(string stage, uint id, double? milliseconds = null,
        DateTimeOffset? sourceCreatedAt = null) =>
        Entries.Writer.TryWrite(new(DateTimeOffset.Now, stage, id, milliseconds, sourceCreatedAt));
}

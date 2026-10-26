using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Windows.Storage;

namespace OtpClipboard.Services;

/// <summary>
/// UI 用法：await AppPreferences.LoadAsync()；修改属性后 await preferences.SaveAsync()。
/// 文件不存在时使用默认值；读取、解析或保存失败会抛出异常，由调用方 catch 后上报。
/// 失败时不会自动覆盖原文件。所有磁盘访问和 JSON 处理均在后台进行。
/// </summary>
public sealed class AppPreferences
{
    public bool AutoCopy { get; set; } = false;
    public bool PhoneLinkOnly { get; set; } = true;
    public bool ShowPopup { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    public const string FileName = "preferences.json";
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<AppPreferences> LoadAsync()
    {
        // 先排队再调度后台工作，保证同一 UI 线程连续保存/读取的调用顺序。
        await FileGate.WaitAsync();
        try
        {
            return await Task.Run(async () =>
            {
                string path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                string json;
                try
                {
                    json = await File.ReadAllTextAsync(path, Utf8);
                }
                catch (FileNotFoundException)
                {
                    return new AppPreferences();
                }

                return JsonSerializer.Deserialize<AppPreferences>(json, JsonOptions)
                    ?? throw new JsonException("设置文件内容为 null，无法读取设置。");
            });
        }
        finally
        {
            FileGate.Release();
        }
    }

    public async Task SaveAsync()
    {
        // 在调用线程捕获快照，避免后台序列化期间 UI 修改属性。
        var snapshot = new AppPreferences
        {
            AutoCopy = AutoCopy,
            PhoneLinkOnly = PhoneLinkOnly,
            ShowPopup = ShowPopup,
            MinimizeToTray = MinimizeToTray,
        };
        await FileGate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                string? temporaryPath = null;
                try
                {
                    string path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                    temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions), Utf8);
                    // 同目录临时文件写完再替换，避免写到一半破坏已有设置。
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    try
                    {
                        if (temporaryPath is not null) File.Delete(temporaryPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 清理失败不能掩盖原始保存异常。
                        Debug.WriteLine($"清理设置临时文件失败：{ex}");
                    }
                }
            });
        }
        finally
        {
            FileGate.Release();
        }
    }
}

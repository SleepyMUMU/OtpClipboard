using Windows.ApplicationModel;

namespace OtpClipboard.Services;

/// <summary>Succeeded 表示查询成功或已达到请求状态；Error 保留原始异常供 UI 上报。</summary>
public sealed record StartupStatus(StartupTaskState? State, bool Succeeded, string Message, Exception? Error = null)
{
    public bool? IsEnabled => State switch
    {
        StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => true,
        StartupTaskState.Disabled or StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy => false,
        _ => null,
    };

    public bool CanChange => State is StartupTaskState.Enabled or StartupTaskState.Disabled;
}

/// <summary>
/// 在应用后台 STA 线程 await QueryAsync / SetEnabledAsync；仅用户操作时调用 SetEnabledAsync。
/// 查询不会开启自启动，系统状态不保存在偏好文件中。没有定时轮询。
/// </summary>
public static class StartupService
{
    public static string TaskId => Package.Current.Id.Name.EndsWith(".Development") ? "OtpClipboardDevelopmentStartup" : "OtpClipboardStartup";
    private static readonly SemaphoreSlim ChangeGate = new(1, 1);

    public static async Task<StartupStatus> QueryAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return Describe(task.State);
        }
        catch (Exception ex)
        {
            return new StartupStatus(null, false, $"查询开机自启动失败：{ex.Message}", ex);
        }
    }

    public static async Task<StartupStatus> SetEnabledAsync(bool enabled)
    {
        await ChangeGate.WaitAsync();
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            StartupTaskState state = task.State;
            if (enabled && state == StartupTaskState.Disabled)
            {
                state = await task.RequestEnableAsync();
            }
            else if (!enabled && state == StartupTaskState.Enabled)
            {
                // Disable 没有返回值，必须重新查询，不能假定成功。
                task.Disable();
                state = (await StartupTask.GetAsync(TaskId)).State;
            }

            var status = Describe(state);
            bool succeeded = status.IsEnabled == enabled;
            return status with
            {
                Succeeded = succeeded,
                Message = !succeeded && state == StartupTaskState.Disabled
                    ? "系统未允许启用开机自启动，当前仍为禁用。"
                    : status.Message,
            };
        }
        catch (Exception ex)
        {
            return new StartupStatus(null, false, $"修改开机自启动失败：{ex.Message}", ex);
        }
        finally
        {
            ChangeGate.Release();
        }
    }

    private static StartupStatus Describe(StartupTaskState state) => new(state, true, state switch
    {
        StartupTaskState.Disabled => "开机自启动已禁用。",
        StartupTaskState.Enabled => "开机自启动已启用，将在下次登录 Windows 时运行。",
        StartupTaskState.DisabledByUser => "开机自启动已被用户禁用；请在 Windows 设置的“应用 → 启动”或任务管理器中手动启用，应用无法代为开启。",
        StartupTaskState.DisabledByPolicy => "开机自启动被管理员或组策略禁用，或当前设备不支持；应用无法开启。",
        StartupTaskState.EnabledByPolicy => "开机自启动由管理员或组策略强制启用，应用无法关闭。",
        _ => "系统返回未知的开机自启动状态。",
    });
}

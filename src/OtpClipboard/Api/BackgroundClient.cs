using System.IO.Pipes;
namespace OtpClipboard.Api;

internal sealed class BackgroundClient
{
    public async Task ShowWindowAsync()
    {
        await SendRequestAsync(new PipeRequest(BridgeNames.Show));
    }

    public async Task<SnapshotResponse> GetSnapshotAsync() =>
        await SendAsync(BridgeNames.Snapshot);

    public async Task<SnapshotResponse> RequestAccessAsync() =>
        await SendAsync(BridgeNames.RequestAccess);

    public async Task<SnapshotResponse> ToggleMonitoringAsync() =>
        await SendAsync(BridgeNames.ToggleMonitoring);

    public async Task<SnapshotResponse> ScanAsync() =>
        await SendAsync(BridgeNames.Scan);

    public async Task<SnapshotResponse> UpdatePreferencesAsync(
        bool phoneLinkOnly, bool showPopup, bool minimizeToTray, bool autoCopy)
    {
        var request = new PipeRequest(BridgeNames.UpdatePreferences,
            PhoneLinkOnly: phoneLinkOnly, ShowPopup: showPopup, MinimizeToTray: minimizeToTray, AutoCopy: autoCopy);
        return await SendAsync(request);
    }

    public async Task<SnapshotResponse> SetStartupAsync(bool enabled) =>
        await SendAsync(new PipeRequest(BridgeNames.SetStartup, StartupEnabled: enabled));

    public async Task<SnapshotResponse> GetStartupAsync() =>
        await SendAsync(BridgeNames.GetStartup);

    public async Task<SnapshotResponse> RecopyAsync(string code, string time) =>
        await SendAsync(new PipeRequest(BridgeNames.Recopy, Code: code, Time: time));

    public async Task<SnapshotResponse> ClearRecordsAsync() =>
        await SendAsync(BridgeNames.ClearRecords);

    public async Task PreviewPopupAsync(string code) =>
        await SendRequestAsync(new PipeRequest(BridgeNames.PreviewPopup, Code: code));

    public async Task DismissPopupAsync() =>
        await SendRequestAsync(BridgeNames.DismissPopup);

    public async Task ExitAsync() => await SendRequestAsync(BridgeNames.Exit);

    public async Task<SnapshotResponse?> TryGetSnapshotAsync()
    {
        try { return await GetSnapshotAsync(); }
        catch { return null; }
    }

    public bool IsAvailable()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", BridgeNames.Pipe, PipeDirection.Out);
            pipe.Connect(250);
            return true;
        }
        catch { return false; }
    }

    private Task<SnapshotResponse> SendAsync(string command) =>
        SendAsync(new PipeRequest(command));

    private async Task<SnapshotResponse> SendAsync(PipeRequest request)
    {
        var response = await SendRequestAsync(request);
        return response.Snapshot ?? throw new InvalidDataException("后台没有返回状态。");
    }

    private async Task<PipeResponse> SendRequestAsync(string command) =>
        await SendRequestAsync(new PipeRequest(command));

    private async Task<PipeResponse> SendRequestAsync(PipeRequest request)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", BridgeNames.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500);
        await PipeCodec.WriteAsync(pipe, request);
        var response = await PipeCodec.ReadAsync<PipeResponse>(pipe);
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "后台拒绝了请求。");
        return response;
    }
}

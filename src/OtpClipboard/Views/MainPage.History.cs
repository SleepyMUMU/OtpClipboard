using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OtpClipboard.Models;

namespace OtpClipboard.Views;

public sealed partial class MainPage
{
    private EventKind? ActiveFilter => RecordFilter.SelectedIndex switch
    {
        1 => EventKind.Copied,
        2 => EventKind.NoMatch,
        3 => EventKind.Filtered,
        4 => EventKind.Failed,
        _ => null,
    };

    private void RecordFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        RebuildVisible();
    }

    private void RebuildVisible()
    {
        if (EventList is null) return;
        _visible.Clear();
        if (_controller is null) return;
        var filter = ActiveFilter;
        foreach (var item in _controller.Records)
            if (filter is null || item.Kind == filter) _visible.Add(item);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (RecordSummary is null || LatestCode is null || EmptyHistory is null) return;
        var records = _controller?.Records;
        int copied = records?.Count(r => r.Kind == EventKind.Copied) ?? 0;
        int noMatch = records?.Count(r => r.Kind == EventKind.NoMatch) ?? 0;
        int filtered = records?.Count(r => r.Kind == EventKind.Filtered) ?? 0;
        int failed = records?.Count(r => r.Kind == EventKind.Failed) ?? 0;
        UpdateLatestCode();
        EmptyHistory.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHistoryText.Text = records?.Count > 0
            ? "没有符合筛选条件的记录"
            : "还没有记录。后台收到新通知时会在这里显示处理结果。";
        string state = _controller?.IsMonitoring == true ? "监听中" : "未监听";
        RecordSummary.Text = records is null || records.Count == 0
            ? state
            : $"{state} · 共 {records.Count} 条：已复制 {copied}，未匹配 {noMatch}，已过滤 {filtered}，失败 {failed}";
    }

    private EventItem? LatestCopied() =>
        _controller?.Records.FirstOrDefault(item =>
            !string.IsNullOrEmpty(item.Code));

    private void UpdateLatestCode()
    {
        var latest = LatestCopied();
        LatestCopyButton.IsEnabled = latest is not null;
        LatestCode.Text = latest?.Code ?? "——";
        LatestCodeDetail.Text = latest is null
            ? "还没有识别到验证码。识别后会在这里显示。"
            : $"{latest.Time} · {latest.Title}";
    }
}

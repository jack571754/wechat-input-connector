namespace Connector.Core;

public sealed record HistoryEntry(string Id, DateTimeOffset? ReceivedAt, string Text, string Status, string Detail);

public static class InputHistory
{
    public static IReadOnlyList<HistoryEntry> Read(ReceiptSnapshot snapshot, BoundIdentity identity) => snapshot.Events
        .Where(x => x.ReasonCode is not ("status_query" or "history_query") &&
            (x.Event is { } e ? e.AccountId == identity.BotId && e.SenderId == identity.OwnerId
                : x.State == EventState.NeedsTranscript && x.Identity == identity))
        .Reverse().Select(x => new HistoryEntry(x.StorageKey, x.Event?.ReceivedAt ?? x.ObservedAt,
            x.Event?.Text ?? "未收到语音转写文字", Describe(x.State), x.State is not (EventState.Dispatched or EventState.Verified) && x.Failure is { } failure ? ReceiptFormatter.Failure(failure) : ""))
        .ToArray();

    public static string Describe(EventState state) => state switch
    {
        EventState.Dispatched => "已派发", EventState.Verified => "历史已确认", EventState.Queued => "待输入",
        EventState.Inserting => "派发中", EventState.Uncertain => "结果不明", EventState.NeedsTranscript => "缺少语音转写",
        EventState.Rejected => "已拒绝", EventState.Expired => "已过期", _ => "已跳过"
    };
}

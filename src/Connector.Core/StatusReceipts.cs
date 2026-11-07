namespace Connector.Core;

public sealed record ReplyContext(string AccountId, string OwnerId, string ContextToken,
    string SourceMessageId, DateTimeOffset ReceivedAt)
{
    public override string ToString() => "微信回复上下文（已隐藏）";
}

public enum InputFailure { NoInput, Paused, Expired, InvalidText, QueueFull, Uncertain }
public sealed record InputOutcome(EventState State, InputFailure Failure = InputFailure.NoInput, string? Application = null);
public enum ReceiptDelivery { Sent, Retryable, SessionExpired }
public sealed record PendingReceipt(string Id, string ClientId, ReplyContext Reply, string Text,
    DateTimeOffset CreatedAt, DateTimeOffset NextAttemptAt, int Attempts = 0, bool Query = false, bool Sending = false)
{
    public override string ToString() => "待发送状态回执（已隐藏上下文）";
}
public sealed record ReceiptSnapshot(string? Cursor, IReadOnlyList<InboxItem> Events, IReadOnlyList<PendingReceipt> Pending,
    bool AutoInput = true, bool AutoEnter = false, bool InputPaused = false, long Skipped = 0,
    ReplyContext? LastContext = null, DateTimeOffset? LastSendAt = null, bool Connected = true)
{
    public override string ToString() => "连接器持久化状态（已隐藏）";
}

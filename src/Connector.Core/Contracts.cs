namespace Connector.Core;

public sealed record TextEvent(string AccountId, string MessageId, string SenderId,
    string Text, DateTimeOffset? CreatedAt, DateTimeOffset ReceivedAt);
public sealed record InboxBatch(string? NextCursor, IReadOnlyList<InboxItem> Items);
public sealed record InboxItem(string StorageKey, TextEvent? Event, EventState State, string ReasonCode,
    ReplyContext? Reply = null, int InputAttempts = 0, string? LastReceiptId = null,
    InputFailure? Failure = null, DateTimeOffset? ObservedAt = null, BoundIdentity? Identity = null);
public sealed record ParseResult(InboxItem Item, bool Ignore = false);
public sealed record BoundIdentity(string BotId, string OwnerId);
public enum EventState { Queued, NeedsTranscript, Inserting, Dispatched, Verified, Uncertain, Rejected, Discarded, Expired }

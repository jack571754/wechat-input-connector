namespace Connector.Core;

public sealed record TextEvent(string AccountId, string MessageId, string SenderId,
    string Text, DateTimeOffset? CreatedAt, DateTimeOffset ReceivedAt);
public sealed record InboxBatch(string? NextCursor, IReadOnlyList<InboxItem> Items);
public sealed record InboxItem(string StorageKey, TextEvent? Event, EventState State, string ReasonCode);
public sealed record ParseResult(InboxItem Item);
public sealed record BoundIdentity(string BotId, string OwnerId);
public enum EventState { Queued, NeedsTranscript, Inserting, Dispatched, Verified, Uncertain, Rejected, Discarded, Expired }

namespace Connector.Core;

public sealed record ReplyContext(string AccountId, string OwnerId, string ContextToken,
    string SourceMessageId, DateTimeOffset ReceivedAt)
{
    public override string ToString() => "微信回复上下文（已隐藏）";
}

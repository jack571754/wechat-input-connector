using System.Text.Json;

namespace Connector.WeChat;

public sealed record ProtocolMetadata(string ChannelVersion, string AppId, string BotAgent, string SourceCommit);
public sealed record QrCode(string Value, string DisplayContent);
public sealed record QrStatus(string Status, string? BotToken = null, string? BotId = null,
    string? OwnerId = null, string? BaseUrl = null, string? RedirectHost = null);
public sealed record LoginProgress(string Status, string? QrContent = null);
public sealed record LoginResult(string BotToken, string BotId, string OwnerId, Uri BaseUrl)
{
    public override string ToString() => "微信凭据（已隐藏）";
}
public sealed record InboundMessage(JsonElement Raw);
public sealed record PollBatch(string? NextCursor, IReadOnlyList<InboundMessage> Messages,
    int? SuggestedTimeoutMs = null);
public class ILinkException(string message) : Exception(message);
public sealed class SessionExpiredException() : ILinkException("微信会话已失效，请暂停一小时或重新绑定。");

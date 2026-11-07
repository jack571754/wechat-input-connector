using Connector.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Connector.WeChat;

public sealed class MessageParser
{
    private readonly TimeProvider _clock;
    public MessageParser(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public ParseResult Parse(InboundMessage message, BoundIdentity identity)
    {
        var r = message.Raw;
        var key = r.ValueKind == JsonValueKind.Object ? MessageId(r) : null;
        var diagnosticKey = key ?? "rejected:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.GetRawText())));
        ParseResult Reject(string reason) => new(new(diagnosticKey, null, EventState.Rejected, reason));
        if (r.ValueKind != JsonValueKind.Object) return Reject("invalid_message");
        if (key is null) return Reject("missing_message_id");
        var from = ILinkClient.ReadString(r, "from_user_id");
        var to = ILinkClient.ReadString(r, "to_user_id");
        if (string.IsNullOrWhiteSpace(identity.OwnerId) || string.IsNullOrWhiteSpace(identity.BotId)
            || from != identity.OwnerId || to != identity.BotId) return Reject("unauthorized_identity");
        if (!string.IsNullOrEmpty(ILinkClient.ReadString(r, "group_id"))) return Reject("group_not_supported");
        if (ILinkClient.ReadInt(r, "message_type") != 1 || ILinkClient.ReadInt(r, "message_state") != 2)
            return Reject("not_completed_user_message");
        if (!r.TryGetProperty("item_list", out var items) || items.ValueKind != JsonValueKind.Array) return Reject("missing_items");
        List<string> texts = [];
        var missingTranscript = false;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var kind = ILinkClient.ReadInt(item, "type");
            var field = kind == 1 ? "text_item" : kind == 3 ? "voice_item" : null;
            if (field is null) continue;
            var text = item.TryGetProperty(field, out var content) && content.ValueKind == JsonValueKind.Object
                ? ILinkClient.ReadString(content, "text") : null;
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
            else if (kind == 3) missingTranscript = true;
        }
        var joined = string.Join('\n', texts);
        if (missingTranscript) return new(new(key, null, EventState.NeedsTranscript, "voice_transcript_missing"));
        if (joined.Length == 0) return Reject("no_supported_text");
        DateTimeOffset? created = null;
        if (r.TryGetProperty("create_time_ms", out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out var timestamp)
            && timestamp is >= -62135596800000L and <= 253402300799999L) created = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        var ev = new TextEvent(identity.BotId, key, from!, joined, created, _clock.GetUtcNow());
        var manualOnly = joined.Length > 10000 || joined.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');
        return new(new(key, ev, EventState.Queued, manualOnly ? "manual_only" : "ready"));
    }

    private static string? MessageId(JsonElement root)
    {
        if (!root.TryGetProperty("message_id", out var id)) return null;
        if (id.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString();
        if (id.ValueKind != JsonValueKind.Number) return null;
        var raw = id.GetRawText();
        return raw.Length > 0 && raw.All(char.IsAsciiDigit) ? raw : null;
    }
}

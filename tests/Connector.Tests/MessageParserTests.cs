using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Core;
using Connector.WeChat;
using Xunit;

namespace Connector.Tests;

public sealed class MessageParserTests
{
    private const string Fixture = """{"message_id":9007199254740993,"from_user_id":"owner","to_user_id":"bot","message_type":1,"message_state":2,"create_time_ms":1794010000000,"item_list":[{"type":1,"text_item":{"text":"你好"},"ref_msg":{"text_item":{"text":"不应拼入引用"}}}]}""";
    private static readonly BoundIdentity Identity = new("bot", "owner");

    [Fact]
    public void Parser_PreservesTextAndExactIdWithoutQuotedMessage()
    {
        var result = new MessageParser().Parse(Message(), Identity).Item;
        Assert.Equal(EventState.Queued, result.State);
        Assert.Equal("9007199254740993", result.StorageKey);
        Assert.Equal("你好", result.Event!.Text);
        Assert.Equal("owner", result.Event.SenderId);
    }

    [Theory]
    [InlineData("from_user_id", "\"stranger\"")]
    [InlineData("from_user_id", "null")]
    [InlineData("to_user_id", "\"another-bot\"")]
    [InlineData("group_id", "\"group\"")]
    [InlineData("message_type", "2")]
    [InlineData("message_state", "1")]
    [InlineData("message_state", "null")]
    [InlineData("message_id", "null")]
    public void Parser_RejectsOtherSenderGroupBotEchoAndMissingIdentity(string key, string jsonValue)
    {
        var result = new MessageParser().Parse(Message(key, JsonNode.Parse(jsonValue)), Identity).Item;
        Assert.Equal(EventState.Rejected, result.State);
        Assert.Null(result.Event);
    }

    [Fact]
    public void Parser_MissingIdUsesDiagnosticKeyWithoutDispatchableEvent()
    {
        var result = new MessageParser().Parse(Message("message_id", null), Identity).Item;
        Assert.StartsWith("rejected:", result.StorageKey);
        Assert.Null(result.Event);
    }

    [Fact]
    public void Parser_UsesVoiceTranscriptInOriginalItemOrder()
    {
        var items = JsonNode.Parse("""[{"type":1,"text_item":{"text":"第一段"}},{"type":3,"voice_item":{"text":"语音文字"}}]""");
        var result = new MessageParser().Parse(Message("item_list", items), Identity).Item;
        Assert.Equal("第一段\n语音文字", result.Event!.Text);
    }

    [Fact]
    public void Parser_VoiceWithoutTranscriptIsExplicitlyPending()
    {
        var result = new MessageParser().Parse(Message("item_list", JsonNode.Parse("""[{"type":3,"voice_item":{"playtime":8000}}]""")), Identity).Item;
        Assert.Equal(EventState.NeedsTranscript, result.State);
        Assert.Null(result.Event);
    }

    [Fact]
    public void Parser_UnsupportedMediaDoesNotCreateText()
    {
        var result = new MessageParser().Parse(Message("item_list", JsonNode.Parse("""[{"type":2,"image_item":{}}]""")), Identity).Item;
        Assert.Equal(EventState.Rejected, result.State);
        Assert.Null(result.Event);
    }

    private static InboundMessage Message(string? key = null, JsonNode? value = null)
    {
        var node = JsonNode.Parse(Fixture)!;
        if (key is not null) node[key] = value;
        return new(JsonSerializer.SerializeToElement(node));
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Connector.Core;
using Connector.WeChat;
using Xunit;

namespace Connector.Tests;

public sealed class StatusReplyProtocolTests
{
    private static readonly LoginResult Account = new("dummy-token", "bot", "owner", new("https://ilinkai.weixin.qq.com/"));
    private static readonly ReplyContext Context = new("bot", "owner", "private-context", "source-1", DateTimeOffset.UtcNow);
    private static ILinkClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), ILinkContractTests.Metadata, Account);
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ReplyUsesOriginalOwnerAndContextWithStableClientId()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("/ilink/bot/sendmessage", request.RequestUri!.AbsolutePath);
            Assert.Equal("dummy-token", request.Headers.Authorization!.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var msg = json.RootElement.GetProperty("msg");
            Assert.Equal("", msg.GetProperty("from_user_id").GetString());
            Assert.Equal("owner", msg.GetProperty("to_user_id").GetString());
            Assert.Equal("private-context", msg.GetProperty("context_token").GetString());
            Assert.Equal("receipt-fixed", msg.GetProperty("client_id").GetString());
            Assert.Equal(2, msg.GetProperty("message_type").GetInt32());
            Assert.Equal(2, msg.GetProperty("message_state").GetInt32());
            Assert.Equal("已暂停。", msg.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString());
            Assert.Equal(ILinkContractTests.Metadata.BotAgent, json.RootElement.GetProperty("base_info").GetProperty("bot_agent").GetString());
            return Json("{\"ret\":0}");
        });
        await Client(handler).SendTextAsync(Context, "receipt-fixed", "已暂停。", default);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("other-bot", "owner", "context")]
    [InlineData("bot", "stranger", "context")]
    [InlineData("bot", "owner", "")]
    public async Task WrongIdentityOrMissingContextNeverMakesRequest(string bot, string owner, string context)
    {
        using var handler = new Handler(_ => Task.FromResult(Json("{}")));
        await Assert.ThrowsAsync<ILinkException>(() => Client(handler).SendTextAsync(new(bot, owner, context, "id", DateTimeOffset.UtcNow), "id", "状态", default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ReplyRejectsOversizedTextBeforeSending()
    {
        using var handler = new Handler(_ => Task.FromResult(Json("{}")));
        await Assert.ThrowsAsync<ILinkException>(() => Client(handler).SendTextAsync(Context, "id", new string('字', 501), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SessionExpiredDuringReplyIsPropagated()
    {
        using var handler = new Handler(_ => Task.FromResult(Json("{\"errcode\":-14}")));
        await Assert.ThrowsAsync<SessionExpiredException>(() => Client(handler).SendTextAsync(Context, "id", "状态", default));
    }

    [Fact]
    public async Task ReplyFailureDoesNotExposeResponseOrContext()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("PRIVATE_SERVER_TOKEN") }));
        var error = await Assert.ThrowsAsync<ILinkException>(() => Client(handler).SendTextAsync(Context, "id", "状态", default));
        Assert.DoesNotContain("PRIVATE", error.Message);
        Assert.DoesNotContain(Context.ContextToken, error.Message);
        Assert.DoesNotContain(Context.ContextToken, Context.ToString());
    }

    [Theory]
    [InlineData(1, "{\"text\":\"输入\"}", EventState.Queued)]
    [InlineData(3, "{}", EventState.NeedsTranscript)]
    public void ParserPreservesAuthenticatedContextEvenWithoutTranscript(int type, string payload, EventState state)
    {
        var field = type == 1 ? "text_item" : "voice_item";
        using var json = JsonDocument.Parse($$"""{"message_id":"m1","from_user_id":"owner","to_user_id":"bot","message_type":1,"message_state":2,"context_token":"ctx","item_list":[{"type":{{type}},"{{field}}":{{payload}}}]}""");
        var item = new MessageParser().Parse(new(json.RootElement.Clone()), new("bot", "owner")).Item;
        Assert.Equal(state, item.State);
        Assert.Equal("ctx", item.Reply?.ContextToken);
        Assert.Equal("owner", item.Reply?.OwnerId);
    }

    [Fact]
    public void BotEchoIsIgnoredWithoutReplyContext()
    {
        using var json = JsonDocument.Parse("""{"message_id":"echo","from_user_id":"bot","to_user_id":"owner","message_type":2,"message_state":2,"context_token":"ctx"}""");
        var parsed = new MessageParser().Parse(new(json.RootElement.Clone()), new("bot", "owner"));
        Assert.True(parsed.Ignore);
        Assert.Null(parsed.Item.Reply);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return response(request); }
    }
}

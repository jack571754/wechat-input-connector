using System.Net;
using System.Text;
using System.Text.Json;
using Connector.WeChat;
using Xunit;

namespace Connector.Tests;

public sealed class ILinkContractTests
{
    internal static readonly ProtocolMetadata Metadata = new("2.4.10", "bot", "WeChatInputConnector/0.1.0", "test-baseline");

    [Fact]
    public async Task QrLogin_UsesPostAndLoginSpecificHeaders()
    {
        using var http = new HttpClient(new ResponseHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://ilinkai.weixin.qq.com/ilink/bot/get_bot_qrcode?bot_type=3", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.Equal("ilink_bot_token", request.Headers.GetValues("AuthorizationType").Single());
            Assert.Equal("bot", request.Headers.GetValues("iLink-App-Id").Single());
            Assert.Equal("132106", request.Headers.GetValues("iLink-App-ClientVersion").Single());
            Assert.NotEmpty(Convert.FromBase64String(request.Headers.GetValues("X-WECHAT-UIN").Single()));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Empty(body.RootElement.GetProperty("local_token_list").EnumerateArray());
            Assert.False(body.RootElement.TryGetProperty("base_info", out _));
            return Json("""{"qrcode":"qr-secret","qrcode_img_content":"https://weixin.qq.com/x/test"}""");
        }));
        var qr = await new ILinkClient(http, Metadata).GetQrCodeAsync(default);
        Assert.Equal("qr-secret", qr.Value);
        Assert.Equal("https://weixin.qq.com/x/test", qr.DisplayContent);
    }

    [Fact]
    public async Task QrStatus_UsesOnlyAppHeadersAndEncodesQuery()
    {
        using var http = new HttpClient(new ResponseHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("qrcode=a%2Bb%26c", request.RequestUri!.Query);
            Assert.Contains("verify_code=123456", request.RequestUri.Query);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("AuthorizationType"));
            Assert.False(request.Headers.Contains("X-WECHAT-UIN"));
            Assert.Equal("bot", request.Headers.GetValues("iLink-App-Id").Single());
            return Task.FromResult(Json("""{"status":"scaned"}"""));
        }));
        var status = await new ILinkClient(http, Metadata).GetQrStatusAsync("a+b&c", "123456", new("https://ilinkai.weixin.qq.com"), default);
        Assert.Equal("scaned", status.Status);
    }

    [Fact]
    public async Task GetUpdates_UsesBoundAccountAndPreservesNumericId()
    {
        using var http = new HttpClient(new ResponseHandler(async (request, ct) =>
        {
            Assert.Equal("Bearer dummy-token", request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("old-cursor", body.RootElement.GetProperty("get_updates_buf").GetString());
            Assert.Equal("2.4.10", body.RootElement.GetProperty("base_info").GetProperty("channel_version").GetString());
            Assert.Equal("WeChatInputConnector/0.1.0", body.RootElement.GetProperty("base_info").GetProperty("bot_agent").GetString());
            return Json("""{"ret":0,"msgs":[{"message_id":9007199254740993}],"get_updates_buf":"new-cursor","longpolling_timeout_ms":35000}""");
        }));
        var client = new ILinkClient(http, Metadata, new("dummy-token", "bot-1", "user-1", new("https://ilinkai.weixin.qq.com")));
        var batch = await client.GetUpdatesAsync("old-cursor", default);
        Assert.Equal("new-cursor", batch.NextCursor);
        Assert.Equal("9007199254740993", batch.Messages.Single().Raw.GetProperty("message_id").GetRawText());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://weixin.qq.com.evil.example")]
    [InlineData("http://ilinkai.weixin.qq.com")]
    [InlineData("https://ilinkai.weixin.qq.com:444")]
    [InlineData("https://user:pass@ilinkai.weixin.qq.com")]
    public async Task QrLogin_RejectsUntrustedHost(string baseUrl)
    {
        var calls = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        await Assert.ThrowsAsync<ILinkException>(() => new ILinkClient(http, Metadata).GetQrStatusAsync("secret", null, new(baseUrl), default));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("{\"ret\":-14}")]
    [InlineData("{\"ret\":0,\"errcode\":-14}")]
    public async Task GetUpdates_SessionExpiryIsNotSuccessfulEmptyPoll(string json)
    {
        using var http = new HttpClient(new ResponseHandler((_, _) => Task.FromResult(Json(json))));
        var client = new ILinkClient(http, Metadata, new("secret", "bot", "user", new("https://ilinkai.weixin.qq.com")));
        await Assert.ThrowsAsync<SessionExpiredException>(() => client.GetUpdatesAsync(null, default));
    }

    [Fact]
    public async Task HttpFailure_DoesNotExposeServerBodyOrCredentials()
    {
        using var http = new HttpClient(new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("SECRET_TOKEN_AND_TEXT") })));
        var error = await Assert.ThrowsAsync<ILinkException>(() => new ILinkClient(http, Metadata).GetQrCodeAsync(default));
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public async Task QrLogin_HandlesVerifyCodeAndRedirect()
    {
        var count = 0;
        using var http = new HttpClient(new ResponseHandler((request, _) =>
        {
            count++;
            var json = count switch
            {
                1 => """{"qrcode":"qr","qrcode_img_content":"https://weixin.qq.com/x/test"}""",
                2 => """{"status":"need_verifycode"}""",
                3 => """{"status":"scaned_but_redirect","redirect_host":"ilinkai.weixin.qq.com"}""",
                _ => """{"status":"confirmed","bot_token":"secret","ilink_bot_id":"bot","ilink_user_id":"owner","baseurl":"https://ilinkai.weixin.qq.com"}"""
            };
            if (count == 3) Assert.Contains("verify_code=987654", request.RequestUri!.Query);
            return Task.FromResult(Json(json));
        }));
        var statuses = new List<string>();
        var result = await new QrLogin(new(http, Metadata), _ => Task.FromResult("987654"), TimeSpan.Zero)
            .RunAsync(new DirectProgress(p => statuses.Add(p.Status)), default);
        Assert.Equal("owner", result.OwnerId);
        Assert.DoesNotContain("secret", result.ToString());
        Assert.Contains("need_verifycode", statuses);
        Assert.Equal(4, count);
    }

    [Theory]
    [InlineData("verify_code_blocked")]
    [InlineData("binded_redirect")]
    public async Task QrLogin_DoesNotInventCredentialsForBlockedOrAlreadyBound(string status)
    {
        var count = 0;
        using var http = new HttpClient(new ResponseHandler((_, _) => Task.FromResult(Json(++count == 1
            ? """{"qrcode":"qr","qrcode_img_content":"https://weixin.qq.com/x/test"}"""
            : JsonSerializer.Serialize(new { status })))));
        await Assert.ThrowsAsync<ILinkException>(() => new QrLogin(new(http, Metadata), pollingDelay: TimeSpan.Zero)
            .RunAsync(new DirectProgress(_ => { }), default));
    }

    [Fact]
    public async Task QrLogin_RefreshesExpiredCodeWithoutKeepingOldQrValue()
    {
        var qrCount = 0;
        using var http = new HttpClient(new ResponseHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                qrCount++;
                return Task.FromResult(Json(JsonSerializer.Serialize(new { qrcode = $"qr-{qrCount}", qrcode_img_content = $"https://weixin.qq.com/x/{qrCount}" })));
            }
            if (qrCount == 1) return Task.FromResult(Json("""{"status":"expired"}"""));
            Assert.Contains("qrcode=qr-2", request.RequestUri!.Query);
            return Task.FromResult(Json("""{"status":"confirmed","bot_token":"secret","ilink_bot_id":"bot","ilink_user_id":"owner","baseurl":"https://ilinkai.weixin.qq.com"}"""));
        }));
        var result = await new QrLogin(new(http, Metadata), pollingDelay: TimeSpan.Zero).RunAsync(new DirectProgress(_ => { }), default);
        Assert.Equal(2, qrCount);
        Assert.Equal("owner", result.OwnerId);
    }

    [Fact]
    public async Task GetUpdates_PropagatesCallerCancellation()
    {
        using var http = new HttpClient(new ResponseHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var client = new ILinkClient(http, Metadata, new("secret", "bot", "user", new("https://ilinkai.weixin.qq.com")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetUpdatesAsync(null, cts.Token));
    }

    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    internal sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    internal sealed class DirectProgress(Action<LoginProgress> report) : IProgress<LoginProgress> { public void Report(LoginProgress value) => report(value); }
}

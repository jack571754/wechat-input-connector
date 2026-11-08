using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Core;
using Connector.WeChat;
using Xunit;

namespace Connector.Tests;

public sealed class ImageUploadProtocolTests
{
    private static readonly LoginResult Account = new("dummy-token", "bot", "owner", new("https://ilinkai.weixin.qq.com/"));
    private static readonly ReplyContext Context = new("bot", "owner", "private-context", "source-1", DateTimeOffset.UtcNow);

    private static ILinkClient Client(HttpMessageHandler handler, LoginResult? account = null) =>
        new(new HttpClient(handler), ILinkContractTests.Metadata, account ?? Account);

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SendImageAsync_PerformsFullUploadFlowWithAesEncryptionAndCorrectPayload()
    {
        var rawBytes = Encoding.UTF8.GetBytes("FakeJpegImageBytesForTestingPurpose1234567890!");
        var expectedRawSize = rawBytes.Length;
        var expectedRawMd5 = Convert.ToHexStringLower(MD5.HashData(rawBytes));

        string? capturedFileKey = null;
        int? capturedFileSize = null;
        byte[]? capturedEncryptedBytes = null;
        string? capturedAesKeyHex = null;
        string? capturedAesKeyBase64 = null;
        int requestIndex = 0;

        using var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            var step = Interlocked.Increment(ref requestIndex);
            if (step == 1)
            {
                // Step 1: POST /ilink/bot/getuploadurl
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/ilink/bot/getuploadurl", request.RequestUri!.AbsolutePath);
                Assert.Equal("dummy-token", request.Headers.Authorization!.Parameter);

                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = json.RootElement;
                Assert.Equal(1, root.GetProperty("media_type").GetInt32());
                Assert.Equal("owner", root.GetProperty("to_user_id").GetString());
                Assert.Equal(expectedRawSize, root.GetProperty("rawsize").GetInt32());
                Assert.Equal(expectedRawMd5, root.GetProperty("rawfilemd5").GetString());

                var fileSize = root.GetProperty("filesize").GetInt32();
                Assert.True(fileSize >= expectedRawSize, "Ciphertext size with PKCS7 padding should be >= raw size.");
                capturedFileSize = fileSize;

                var fileKey = root.GetProperty("filekey").GetString();
                Assert.NotNull(fileKey);
                Assert.Equal(32, fileKey.Length);
                capturedFileKey = fileKey;

                Assert.Equal(ILinkContractTests.Metadata.BotAgent, root.GetProperty("base_info").GetProperty("bot_agent").GetString());
                Assert.True(root.GetProperty("no_need_thumb").GetBoolean());
                Assert.Equal(32, root.GetProperty("aeskey").GetString()!.Length);

                return Json("{\"ret\":0,\"upload_full_url\":\"https://novac2c.cdn.weixin.qq.com/test_upload\"}");
            }
            if (step == 2)
            {
                // Step 2: CDN Upload to upload_full_url
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("novac2c.cdn.weixin.qq.com", request.RequestUri!.Host);
                Assert.Equal("/test_upload", request.RequestUri.AbsolutePath);
                Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);

                var bytes = await request.Content.ReadAsByteArrayAsync(ct);
                Assert.Equal(capturedFileSize, bytes.Length);
                capturedEncryptedBytes = bytes;

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("x-encrypted-param", "test_query_param");
                return response;
            }
            if (step == 3)
            {
                // Step 3: POST /ilink/bot/sendmessage
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/ilink/bot/sendmessage", request.RequestUri!.AbsolutePath);
                Assert.Equal("dummy-token", request.Headers.Authorization!.Parameter);

                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var msg = json.RootElement.GetProperty("msg");
                Assert.Equal("", msg.GetProperty("from_user_id").GetString());
                Assert.Equal("owner", msg.GetProperty("to_user_id").GetString());
                Assert.Equal("private-context", msg.GetProperty("context_token").GetString());
                Assert.Equal("img-client-1", msg.GetProperty("client_id").GetString());
                Assert.Equal(2, msg.GetProperty("message_type").GetInt32());
                Assert.Equal(2, msg.GetProperty("message_state").GetInt32());

                var items = msg.GetProperty("item_list");
                Assert.Equal(1, items.GetArrayLength());
                var item = items[0];
                Assert.Equal(2, item.GetProperty("type").GetInt32());

                var imageItem = item.GetProperty("image_item");
                Assert.Equal(capturedFileKey, imageItem.GetProperty("filekey").GetString());

                var aeskeyHex = imageItem.GetProperty("aeskey").GetString();
                Assert.NotNull(aeskeyHex);
                Assert.Equal(32, aeskeyHex.Length);
                capturedAesKeyHex = aeskeyHex;

                var media = imageItem.GetProperty("media");
                Assert.Equal("test_query_param", media.GetProperty("encrypt_query_param").GetString());

                var aesKeyBase64 = media.GetProperty("aes_key").GetString();
                Assert.NotNull(aesKeyBase64);
                capturedAesKeyBase64 = aesKeyBase64;

                Assert.Equal(expectedRawSize, imageItem.GetProperty("mid_size").GetInt32());
                Assert.Equal(expectedRawSize, imageItem.GetProperty("hd_size").GetInt32());

                return Json("{\"ret\":0}");
            }

            throw new InvalidOperationException($"Unexpected request count: {step}");
        });

        await Client(handler).SendImageAsync(Context, "img-client-1", rawBytes, default);

        Assert.Equal(3, handler.Calls);
        Assert.NotNull(capturedEncryptedBytes);
        Assert.NotNull(capturedAesKeyHex);
        Assert.NotNull(capturedAesKeyBase64);

        // Verify AES key consistency: hex and Base64 represent the exact same 16 bytes
        var keyFromHex = Convert.FromHexString(capturedAesKeyHex);
        var keyFromBase64 = Convert.FromBase64String(capturedAesKeyBase64);
        Assert.Equal(16, keyFromHex.Length);
        Assert.Equal(keyFromHex, keyFromBase64);

        // Verify ciphertext can be decrypted using the AES key to obtain original plaintext
        using var aes = Aes.Create();
        aes.Key = keyFromHex;
        var decryptedBytes = aes.DecryptEcb(capturedEncryptedBytes, PaddingMode.PKCS7);
        Assert.Equal(rawBytes, decryptedBytes);
    }

    [Fact]
    public async Task SendImageAsync_RejectsEmptyOrNullOrOversizedBytes()
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(Json("{}")));
        var client = Client(handler);

        // Empty byte array
        await Assert.ThrowsAsync<ILinkException>(() => client.SendImageAsync(Context, "id", [], default));
        // Null byte array
        await Assert.ThrowsAsync<ILinkException>(() => client.SendImageAsync(Context, "id", null!, default));
        // Oversized (> 10MB)
        var oversized = new byte[10 * 1024 * 1024 + 1];
        await Assert.ThrowsAsync<ILinkException>(() => client.SendImageAsync(Context, "id", oversized, default));

        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("other-bot", "owner", "context", "id")]
    [InlineData("bot", "stranger", "context", "id")]
    [InlineData("bot", "owner", "", "id")]
    [InlineData("bot", "owner", "   ", "id")]
    [InlineData("bot", "owner", "context", "")]
    [InlineData("bot", "owner", "context", "   ")]
    public async Task SendImageAsync_RejectsInvalidContextOrClientId(string bot, string owner, string context, string clientId)
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(Json("{}")));
        var client = Client(handler);
        var replyContext = new ReplyContext(bot, owner, context, "source", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ILinkException>(() => client.SendImageAsync(replyContext, clientId, [1, 2, 3], default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SendImageAsync_RejectsOverlengthClientId()
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(Json("{}")));
        var client = Client(handler);
        var longClientId = new string('x', 129);

        await Assert.ThrowsAsync<ILinkException>(() => client.SendImageAsync(Context, longClientId, [1, 2, 3], default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SendImageAsync_ThrowsWhenNotLoggedIn()
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(Json("{}")));
        var client = new ILinkClient(new HttpClient(handler), ILinkContractTests.Metadata, null);

        await Assert.ThrowsAsync<ILinkException>(() => client.SendImageAsync(Context, "id", [1, 2, 3], default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SendImageAsync_ThrowsWhenGetUploadUrlFailsOrSessionExpired()
    {
        // Session expired (ret: -14)
        using var handler1 = new MockHttpMessageHandler((_, _) => Task.FromResult(Json("{\"errcode\":-14}")));
        await Assert.ThrowsAsync<SessionExpiredException>(() => Client(handler1).SendImageAsync(Context, "id", [1, 2, 3], default));

        // Business failure
        using var handler2 = new MockHttpMessageHandler((_, _) => Task.FromResult(Json("{\"ret\":10001}")));
        await Assert.ThrowsAsync<ILinkException>(() => Client(handler2).SendImageAsync(Context, "id", [1, 2, 3], default));

        // Missing upload_full_url
        using var handler3 = new MockHttpMessageHandler((_, _) => Task.FromResult(Json("{\"ret\":0}")));
        await Assert.ThrowsAsync<ILinkException>(() => Client(handler3).SendImageAsync(Context, "id", [1, 2, 3], default));
    }

    [Fact]
    public async Task SendImageAsync_ThrowsWhenCdnUploadFails()
    {
        int step = 0;
        using var handler = new MockHttpMessageHandler((request, _) =>
        {
            step++;
            if (step == 1)
                return Task.FromResult(Json("{\"ret\":0,\"upload_full_url\":\"https://novac2c.cdn.weixin.qq.com/test_upload\"}"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });

        await Assert.ThrowsAsync<ILinkException>(() => Client(handler).SendImageAsync(Context, "id", [1, 2, 3], default));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SendImageAsync_ThrowsWhenCdnMissingEncryptedParamHeader()
    {
        int step = 0;
        using var handler = new MockHttpMessageHandler((request, _) =>
        {
            step++;
            if (step == 1)
                return Task.FromResult(Json("{\"ret\":0,\"upload_full_url\":\"https://novac2c.cdn.weixin.qq.com/test_upload\"}"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); // no x-encrypted-param header
        });

        await Assert.ThrowsAsync<ILinkException>(() => Client(handler).SendImageAsync(Context, "id", [1, 2, 3], default));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SendImageAsync_RejectsDisallowedCdnHost()
    {
        using var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(Json("{\"ret\":0,\"upload_full_url\":\"https://malicious.example.com/upload\"}")));

        await Assert.ThrowsAsync<ILinkException>(() => Client(handler).SendImageAsync(Context, "id", [1, 2, 3], default));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return response(request, ct);
        }
    }
}

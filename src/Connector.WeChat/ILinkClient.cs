using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Connector.WeChat;

public sealed class ILinkClient
{
    public static readonly Uri LoginBaseUrl = new("https://ilinkai.weixin.qq.com/");
    private readonly HttpClient _http;
    private readonly ProtocolMetadata _metadata;
    private readonly LoginResult? _account;

    public ILinkClient(HttpClient http, ProtocolMetadata metadata, LoginResult? account = null)
    {
        _http = http;
        _metadata = metadata;
        _account = account;
    }

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static ProtocolMetadata LoadPinnedMetadata()
    {
        using var stream = typeof(ILinkClient).Assembly.GetManifestResourceStream("Connector.WeChat.protocol-baseline.json")
            ?? throw new InvalidOperationException("缺少协议基线文件。");
        return JsonSerializer.Deserialize<ProtocolMetadata>(stream) ?? throw new InvalidOperationException("协议基线无效。");
    }

    public static Uri ValidateApiBaseUrl(Uri uri)
    {
        // 只接受微信自有域名；HTTP 自动重定向已关闭，不将凭据转交给其他站点。
        var host = uri.IsAbsoluteUri ? uri.IdnHost : "";
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/"
            || !(host.Equals("weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase)))
            throw new ILinkException("服务器返回了未经允许的微信接口地址，已停止连接。");
        return uri;
    }

    public async Task<QrCode> GetQrCodeAsync(CancellationToken ct)
    {
        using var doc = await RequestAsync(HttpMethod.Post, LoginBaseUrl, "ilink/bot/get_bot_qrcode?bot_type=3",
            new { local_token_list = Array.Empty<string>() }, authenticated: false, TimeSpan.FromSeconds(30), ct);
        var root = doc.RootElement;
        var value = ReadString(root, "qrcode");
        var content = ReadString(root, "qrcode_img_content");
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(content)) throw new ILinkException("微信未返回有效二维码。");
        return new(value, content);
    }

    public async Task<QrStatus> GetQrStatusAsync(string qrcode, string? code, Uri baseUrl, CancellationToken ct)
    {
        var path = "ilink/bot/get_qrcode_status?qrcode=" + Uri.EscapeDataString(qrcode);
        if (!string.IsNullOrWhiteSpace(code)) path += "&verify_code=" + Uri.EscapeDataString(code);
        try
        {
            using var doc = await RequestAsync(HttpMethod.Get, baseUrl, path, null, false, TimeSpan.FromSeconds(40), ct);
            var r = doc.RootElement;
            return new(ReadString(r, "status") ?? "unknown", ReadString(r, "bot_token"), ReadString(r, "ilink_bot_id"),
                ReadString(r, "ilink_user_id"), ReadString(r, "baseurl"), ReadString(r, "redirect_host"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("wait"); }
    }

    public async Task<PollBatch> GetUpdatesAsync(string? cursor, CancellationToken ct)
    {
        if (_account is null) throw new ILinkException("请先扫码绑定微信。");
        try
        {
            using var doc = await RequestAsync(HttpMethod.Post, _account.BaseUrl, "ilink/bot/getupdates",
                new { get_updates_buf = cursor ?? "", base_info = BaseInfo() }, true, TimeSpan.FromSeconds(45), ct);
            var r = doc.RootElement;
            CheckBusinessResult(r);
            var messages = r.TryGetProperty("msgs", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(x => new InboundMessage(x.Clone())).ToArray() : [];
            return new(ReadString(r, "get_updates_buf"), messages, ReadInt(r, "longpolling_timeout_ms"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(cursor, []); }
    }

    public async Task NotifyLifecycleAsync(bool starting, CancellationToken ct)
    {
        if (_account is null) throw new ILinkException("请先扫码绑定微信。");
        using var doc = await RequestAsync(HttpMethod.Post, _account.BaseUrl,
            starting ? "ilink/bot/msg/notifystart" : "ilink/bot/msg/notifystop",
            new { base_info = BaseInfo() }, true, TimeSpan.FromSeconds(10), ct);
        CheckBusinessResult(doc.RootElement);
    }

    private object BaseInfo() => new { channel_version = _metadata.ChannelVersion, bot_agent = _metadata.BotAgent };

    private async Task<JsonDocument> RequestAsync(HttpMethod method, Uri baseUrl, string path, object? body,
        bool authenticated, TimeSpan timeout, CancellationToken ct)
    {
        ValidateApiBaseUrl(baseUrl);
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, path));
        request.Headers.Add("iLink-App-Id", _metadata.AppId);
        var parts = _metadata.ChannelVersion.Split('.');
        if (parts.Length != 3 || !parts.All(x => byte.TryParse(x, out _))) throw new ILinkException("协议版本元数据无效。");
        var version = (int.Parse(parts[0], CultureInfo.InvariantCulture) << 16)
            | (int.Parse(parts[1], CultureInfo.InvariantCulture) << 8) | int.Parse(parts[2], CultureInfo.InvariantCulture);
        request.Headers.Add("iLink-App-ClientVersion", version.ToString(CultureInfo.InvariantCulture));
        if (method == HttpMethod.Post)
        {
            request.Headers.Add("AuthorizationType", "ilink_bot_token");
            var number = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
            request.Headers.Add("X-WECHAT-UIN", Convert.ToBase64String(Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture))));
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        if (authenticated)
        {
            if (string.IsNullOrWhiteSpace(_account?.BotToken)) throw new ILinkException("缺少微信登录凭据。");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _account.BotToken);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ILinkException($"微信接口请求失败（HTTP {(int)response.StatusCode}）。");
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > 1024 * 1024) throw new ILinkException("微信响应超过安全大小限制。");
                buffer.Write(chunk, 0, read);
            }
            var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new ILinkException("微信返回了无效的数据结构。");
            }
            return document;
        }
        catch (HttpRequestException) { throw new ILinkException("微信网络请求失败，请检查网络连接。"); }
        catch (JsonException) { throw new ILinkException("微信返回的数据无法解析。"); }
    }

    private static void CheckBusinessResult(JsonElement root)
    {
        var ret = ReadInt(root, "ret");
        var error = ReadInt(root, "errcode");
        if (ret == -14 || error == -14) throw new SessionExpiredException();
        if ((ret.HasValue && ret != 0) || (error.HasValue && error != 0))
            throw new ILinkException($"微信接口返回错误码 {ret ?? error}，已保留本地状态。");
    }

    internal static string? ReadString(JsonElement root, string name) => root.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    internal static int? ReadInt(JsonElement root, string name) => root.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;
}

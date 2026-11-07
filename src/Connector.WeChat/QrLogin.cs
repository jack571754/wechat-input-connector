namespace Connector.WeChat;

public sealed class QrLogin
{
    private readonly ILinkClient _client;
    private readonly Func<CancellationToken, Task<string>>? _verifyCode;
    private readonly TimeSpan _delay;
    public QrLogin(ILinkClient client, Func<CancellationToken, Task<string>>? verifyCode = null,
        TimeSpan? pollingDelay = null)
    { _client = client; _verifyCode = verifyCode; _delay = pollingDelay ?? TimeSpan.FromSeconds(1); }

    public async Task<LoginResult> RunAsync(IProgress<LoginProgress> progress, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromMinutes(8));
        var token = lifetime.Token;
        var qr = await _client.GetQrCodeAsync(token).ConfigureAwait(false);
        progress.Report(new("qrcode", qr.DisplayContent));
        var baseUrl = ILinkClient.LoginBaseUrl;
        string? code = null;
        var refreshes = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var status = await _client.GetQrStatusAsync(qr.Value, code, baseUrl, token).ConfigureAwait(false);
            progress.Report(new(status.Status));
            switch (status.Status)
            {
                case "confirmed":
                    if (string.IsNullOrWhiteSpace(status.BotToken) || string.IsNullOrWhiteSpace(status.BotId)
                        || string.IsNullOrWhiteSpace(status.OwnerId) || !Uri.TryCreate(status.BaseUrl, UriKind.Absolute, out var confirmedBase))
                        throw new ILinkException("登录确认缺少身份或接口地址，已停止绑定。");
                    return new(status.BotToken, status.BotId, status.OwnerId, ILinkClient.ValidateApiBaseUrl(confirmedBase));
                case "need_verifycode":
                    if (_verifyCode is null) throw new ILinkException("需要输入手机微信显示的验证码。");
                    code = (await _verifyCode(token).ConfigureAwait(false)).Trim();
                    if (code.Length is < 4 or > 12 || !code.All(char.IsAsciiDigit)) throw new ILinkException("验证码应为手机显示的数字。");
                    continue;
                case "scaned_but_redirect":
                    if (string.IsNullOrWhiteSpace(status.RedirectHost)
                        || !Uri.TryCreate("https://" + status.RedirectHost, UriKind.Absolute, out var redirected))
                        throw new ILinkException("微信登录重定向地址无效。");
                    baseUrl = ILinkClient.ValidateApiBaseUrl(redirected);
                    break;
                case "expired":
                    if (++refreshes > 3) throw new ILinkException("二维码多次过期，请重新开始连接。");
                    qr = await _client.GetQrCodeAsync(token).ConfigureAwait(false);
                    code = null;
                    baseUrl = ILinkClient.LoginBaseUrl;
                    progress.Report(new("qrcode", qr.DisplayContent));
                    break;
                case "verify_code_blocked": throw new ILinkException("验证码错误次数过多，请稍后重新扫码。");
                case "binded_redirect": throw new ILinkException("此 Bot 已绑定；请使用已保存凭据，或在微信中解除旧连接后重试。");
                case "scaned": code = null; break;
                case "wait": break;
                default: throw new ILinkException("微信返回了未支持的登录状态，已停止轮询。");
            }
            await Task.Delay(_delay, token).ConfigureAwait(false);
        }
    }
}

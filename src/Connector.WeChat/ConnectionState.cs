using Connector.Core;

namespace Connector.WeChat;

// 与旧 P0 文件的字段兼容；输入事件只在顶层保存一份。
public sealed record ConnectionState(LoginResult Account, string? Cursor, List<InboxItem> Events,
    int VoiceTotal = 0, int VoiceWithTranscript = 0, ReceiptSnapshot? Runtime = null)
{
    public ReceiptSnapshot ToRuntime(bool autoInput, bool autoEnter) => (Runtime ?? new(null, [], [])) with
        { Cursor = Cursor, Events = Events, AutoInput = autoInput, AutoEnter = autoEnter };
    public ConnectionState WithRuntime(ReceiptSnapshot state) => this with
        { Cursor = state.Cursor, Events = state.Events.ToList(), Runtime = state with { Cursor = null, Events = [] } };
    public override string ToString() => "微信连接状态（已隐藏凭据及正文）";
}

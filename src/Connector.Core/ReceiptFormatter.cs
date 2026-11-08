using System.Globalization;

namespace Connector.Core;

public static class ReceiptFormatter
{
    internal const string UncertainText = "本条输入结果不明，已保留记录且不再重发；后续新消息继续尝试输入。";
    private static string Target(string? application) => application is "Codex" or "Antigravity" ? application : "当前输入框";
    public static string Input(InboxItem item, InputOutcome result) => result.State switch
    {
        EventState.Verified => result.Application == "屏幕截图" ? "📸 屏幕截图已回传至微信。" : $"已确认写入{Target(result.Application)}，共 {new StringInfo(item.Event?.Text ?? "").LengthInTextElements} 字。",
        EventState.Dispatched => $"已派发到{Target(result.Application)}。",
        EventState.Uncertain => UncertainText,
        EventState.NeedsTranscript => "本条语音未收到转写文字，未执行输入。",
        EventState.Rejected => "本条文字过长或含不支持的控制字符，未执行输入；已保留记录，补写将跳过本条。",
        _ => "未执行输入：" + Failure(result.Failure) + "。内容已暂存。"
    };

    public static string Failure(InputFailure failure) => failure switch
        {
            InputFailure.Paused => "自动输入已暂停",
            InputFailure.Expired => "消息不在自动输入时间窗内",
            InputFailure.InvalidText => "文字不符合自动输入条件",
            InputFailure.QueueFull => "回执队列已满",
            InputFailure.FocusChanged => "输入准备期间焦点发生变化",
            InputFailure.TargetUnavailable => "当前目标不可编辑或无法确认",
            InputFailure.MultilineUnsupported => "当前输入框无法安全插入换行或制表符",
            InputFailure.Timeout => "输入准备超时",
            InputFailure.StorageFailure => "本地状态无法保存",
            InputFailure.InboxFull => "待输入记录已达到容量上限",
            InputFailure.Uncertain => "本条结果不明，未重发",
            _ => "没有可用输入框"
    };

    public static string Status(ReceiptSnapshot state) => $"连接状态：{(state.Connected ? "已连接" : "会话失效")}\n"
        + "当前应用：未确认\n输入框：未确认\n"
        + $"自动输入：{(state.AutoInput && !state.InputPaused ? "开启" : "暂停")}\n"
        + $"自动回车：{(state.AutoEnter ? "开启" : "关闭")}\n"
        + $"待处理：{state.Events.Count(x => x.State is EventState.Queued or EventState.NeedsTranscript)} 条\n"
        + $"结果不明且未重发：{state.Events.Count(x => x.State == EventState.Uncertain)} 条\n"
        + $"待发回执：{state.Pending.Count} 条\n省略或未能送达回执：{state.Skipped} 条";

    public static string Switch(string application, bool activated, bool ready)
    {
        if (application is not ("Codex" or "Antigravity")) throw new ArgumentException("目标应用未经适配。", nameof(application));
        return !activated ? $"未能切换到 {application}；后续文字暂存。"
            : ready ? $"已切换到 {application}，输入框已就绪。"
            : $"已切换到 {application}，尚未找到输入框；后续文字暂存。";
    }
}

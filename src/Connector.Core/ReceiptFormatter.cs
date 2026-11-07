using System.Globalization;

namespace Connector.Core;

public static class ReceiptFormatter
{
    private static string Target(string? application) => application is "Codex" or "Antigravity" ? application : "当前输入框";
    public static string Input(InboxItem item, InputOutcome result) => result.State switch
    {
        EventState.Verified => $"已确认写入{Target(result.Application)}，共 {new StringInfo(item.Event?.Text ?? "").LengthInTextElements} 字。",
        EventState.Dispatched => $"已派发到{Target(result.Application)}，尚未确认写入结果。",
        EventState.Uncertain => "输入结果不明，自动输入已暂停；请检查电脑后再处理本条。",
        EventState.NeedsTranscript => "本条语音未收到转写文字，未执行输入。",
        EventState.Rejected => "本条文字过长或含不支持的控制字符，未执行输入；已保留记录，补写将跳过本条。",
        _ => "未执行输入：" + (result.Failure switch
        {
            InputFailure.Paused => "自动输入已暂停",
            InputFailure.Expired => "消息不在自动输入时间窗内",
            InputFailure.InvalidText => "文字不符合自动输入条件",
            InputFailure.QueueFull => "回执队列已满",
            _ => "没有可用输入框"
        }) + "。内容已暂存。"
    };

    public static string Status(ReceiptSnapshot state) => $"连接状态：{(state.Connected ? "已连接" : "会话失效")}\n"
        + "当前应用：未确认\n输入框：未确认\n"
        + $"自动输入：{(state.AutoInput && !state.InputPaused ? "开启" : "暂停")}\n"
        + $"自动回车：{(state.AutoEnter ? "开启" : "关闭")}\n"
        + $"待处理：{state.Events.Count(x => x.State is EventState.Queued or EventState.NeedsTranscript or EventState.Uncertain)} 条\n"
        + $"待发回执：{state.Pending.Count} 条\n省略或未能送达回执：{state.Skipped} 条";

    public static string Switch(string application, bool activated, bool ready)
    {
        if (application is not ("Codex" or "Antigravity")) throw new ArgumentException("目标应用未经适配。", nameof(application));
        return !activated ? $"未能切换到 {application}；后续文字暂存。"
            : ready ? $"已切换到 {application}，输入框已就绪。"
            : $"已切换到 {application}，尚未找到输入框；后续文字暂存。";
    }
}

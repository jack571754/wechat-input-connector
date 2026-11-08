using Connector.Core;
using Xunit;

namespace Connector.Tests;

public sealed class StatusReplyFormattingTests
{
    private static InboxItem CreateItem(string id = "m1", string text = "测试文本") =>
        new(id, new TextEvent("bot", id, "owner", text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            EventState.Queued, "key-" + id);

    [Fact]
    public void Input_WhenQueuedWithNoInputFailure_IncludesClipboardGuidance()
    {
        var item = CreateItem();
        var outcome = new InputOutcome(EventState.Queued, InputFailure.NoInput);

        var result = ReceiptFormatter.Input(item, outcome);

        Assert.Equal("未执行输入：没有可用输入框。内容已存入电脑剪贴板，解锁或点击输入框后按 Ctrl+V 即可粘贴。", result);
    }

    [Fact]
    public void Input_WhenQueuedWithTargetUnavailable_IncludesClipboardGuidance()
    {
        var item = CreateItem();
        var outcome = new InputOutcome(EventState.Queued, InputFailure.TargetUnavailable);

        var result = ReceiptFormatter.Input(item, outcome);

        Assert.Equal("未执行输入：当前目标不可编辑或无法确认。内容已存入电脑剪贴板，解锁或点击输入框后按 Ctrl+V 即可粘贴。", result);
    }

    [Fact]
    public void Input_WhenQueuedWithFocusChanged_IncludesClipboardGuidance()
    {
        var item = CreateItem();
        var outcome = new InputOutcome(EventState.Queued, InputFailure.FocusChanged);

        var result = ReceiptFormatter.Input(item, outcome);

        Assert.Equal("未执行输入：输入准备期间焦点发生变化。内容已存入电脑剪贴板，解锁或点击输入框后按 Ctrl+V 即可粘贴。", result);
    }
}

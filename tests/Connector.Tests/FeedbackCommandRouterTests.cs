using Connector.Core;
using Xunit;

namespace Connector.Tests;

public sealed class FeedbackCommandRouterTests
{
    [Theory]
    [InlineData("截图")]
    [InlineData("截屏")]
    [InlineData("看屏幕")]
    [InlineData("屏幕")]
    [InlineData("/截图")]
    [InlineData("/截屏")]
    [InlineData("/屏幕")]
    [InlineData("  截图  ")]
    [InlineData("\t/截屏\r\n")]
    [InlineData(" 屏幕 ")]
    [InlineData("看屏幕 ")]
    public void IsScreenshotCommand_ReturnsTrue_ForValidCommands(string command)
    {
        Assert.True(FeedbackCommandRouter.IsScreenshotCommand(command));
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("/状态")]
    [InlineData("截屏软件")]
    [InlineData("截图吗")]
    [InlineData("看屏幕看")]
    [InlineData("截图1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsScreenshotCommand_ReturnsFalse_ForNonScreenshotCommands(string? command)
    {
        Assert.False(FeedbackCommandRouter.IsScreenshotCommand(command));
    }

    [Fact]
    public void ReceiptFormatter_FormatsScreenshotVerifiedCorrectly()
    {
        var item = new InboxItem("id1", new TextEvent("bot", "id1", "owner", "截图", null, DateTimeOffset.UtcNow), EventState.Queued, "ready");
        var outcome = new InputOutcome(EventState.Verified, Application: "屏幕截图");
        var text = ReceiptFormatter.Input(item, outcome);
        Assert.Equal("📸 屏幕截图已回传至微信。", text);
    }

    [Fact]
    public async Task ReceiptCoordinator_GeneratesCorrectReceiptForScreenshot()
    {
        ReceiptSnapshot? saved = null;
        var coord = new ReceiptCoordinator(new(null, [], []), new("bot", "owner"), s => saved = s);
        var reply = new ReplyContext("bot", "owner", "ctx_token", "msg1", DateTimeOffset.UtcNow);
        var item = new InboxItem("msg1", new TextEvent("bot", "msg1", "owner", "看屏幕", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            EventState.Queued, "ready", reply);
        coord.CommitBatch("c1", [item]);

        await coord.ProcessAsync("msg1", (ev, ct) => Task.FromResult(new InputOutcome(EventState.Verified, Application: "屏幕截图")), false, default);

        var snap = coord.Snapshot;
        var verifiedItem = snap.Events.Single(x => x.StorageKey == "msg1");
        Assert.Equal(EventState.Verified, verifiedItem.State);
        Assert.Single(snap.Pending);
        Assert.Contains("📸 屏幕截图已回传至微信。", snap.Pending[0].Text);
    }
}

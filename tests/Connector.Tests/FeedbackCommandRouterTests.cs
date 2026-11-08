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
}

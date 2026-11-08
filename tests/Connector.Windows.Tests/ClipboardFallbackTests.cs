using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class ClipboardFallbackTests
{
    [Fact]
    public void CopyText_WithValidText_CopiesSuccessfullyAndCanBeReadBack()
    {
        const string text = "测试剪贴板保底";
        var success = ClipboardFallback.CopyText(text);

        Assert.True(success);
        var readBack = ClipboardFallback.GetText();
        Assert.Equal(text, readBack);
    }

    [Fact]
    public async Task CopyText_WorksOnMtaOrBackgroundThread()
    {
        const string text = "后台工作线程剪贴板保底测试";
        var success = await Task.Run(() => ClipboardFallback.CopyText(text));

        Assert.True(success);
        var readBack = ClipboardFallback.GetText();
        Assert.Equal(text, readBack);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CopyText_WithNullOrEmptyText_ReturnsFalse(string? text)
    {
        var success = ClipboardFallback.CopyText(text);

        Assert.False(success);
    }

    [Fact]
    public void CopyText_WithLongText_CopiesSuccessfully()
    {
        var longText = new string('长', 50000);
        var success = ClipboardFallback.CopyText(longText);

        Assert.True(success);
        var readBack = ClipboardFallback.GetText();
        Assert.Equal(longText, readBack);
    }

    [Fact]
    public void ExecuteDirect_WhenFocusRefused_CopiesTextToClipboardAndMarksCopied()
    {
        const string text = "焦点缺失时自动存入剪贴板";
        var result = FocusedInputProbe.ExecuteDirect(text, Environment.ProcessId);

        Assert.Equal("Queued", result.State);
        Assert.Equal("Copied", result.Clipboard);
        Assert.Equal(text, ClipboardFallback.GetText());
    }

    [Fact]
    public void ExecuteDirect_WhenTextUnsafe_LeavesClipboardUntouched()
    {
        var result = FocusedInputProbe.ExecuteDirect("", Environment.ProcessId);

        Assert.Equal("Queued", result.State);
        Assert.Equal("Untouched", result.Clipboard);
    }
}

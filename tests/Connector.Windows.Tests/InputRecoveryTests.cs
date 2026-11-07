using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class InputRecoveryTests
{
    [Fact]
    public void ReviewedInputBarrierIsRemovedWithoutTouchingOtherFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wic-recovery-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var record = Path.Combine(directory, "input-recovery.dpapi"); var other = Path.Combine(directory, "other.txt");
        try
        {
            File.WriteAllText(record, "test metadata"); File.WriteAllText(other, "preserve");
            InputRecovery.ClearReviewed(directory);
            Assert.False(File.Exists(record)); Assert.Equal("preserve", File.ReadAllText(other));
        }
        finally { File.Delete(record); File.Delete(other); Directory.Delete(directory); }
    }

    [Fact]
    public void LegacyClipboardRecoveryCannotBeSilentlyDiscardedByInputReview()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wic-recovery-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var record = Path.Combine(directory, "input-recovery.dpapi"); var clipboard = Path.Combine(directory, "clipboard-recovery.dpapi");
        try
        {
            File.WriteAllText(record, "input"); File.WriteAllText(clipboard, "clipboard");
            Assert.Throws<LegacyClipboardRecoveryException>(() => InputRecovery.ClearReviewed(directory));
            Assert.True(File.Exists(record)); Assert.True(File.Exists(clipboard));
        }
        finally { File.Delete(record); File.Delete(clipboard); Directory.Delete(directory); }
    }
}

using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class ProbePoliciesTests
{
    private static readonly TargetObservation Safe = new(42, 42, "Notepad", false, true, false, false, false, false, true, true);

    [Theory]
    [InlineData("你好🙂\r\n第二行\t缩进", true)]
    [InlineData("\u0000", false)]
    [InlineData("\u001b[31m", false)]
    [InlineData("", false)]
    public void OnlyBoundedTextWithoutControlCommandsCanBeInjected(string text, bool expected)
        => Assert.Equal(expected, ProbePolicies.IsSafeText(text));

    [Fact]
    public void OversizedTextIsNotAutomaticallyInjected() => Assert.False(ProbePolicies.IsSafeText(new string('字', 10001)));

    [Fact]
    public async Task MissingTargetCannotTouchClipboard()
    {
        var result = await NotepadProbe.InsertAsync("测试", 0, Path.Combine(AppContext.BaseDirectory, "not-used.dpapi"), default);
        Assert.Equal("Queued", result.State);
        Assert.Equal("Untouched", result.Clipboard);
    }

    [Fact]
    public void OwnedEditableNotepadIsEligible() => Assert.True(ProbePolicies.CanInsert(Safe));

    [Theory]
    [MemberData(nameof(UnsafeTargets))]
    public void UnsafeTargetNeverReceivesText(TargetObservation target) => Assert.False(ProbePolicies.CanInsert(target));

    public static IEnumerable<object[]> UnsafeTargets()
    {
        yield return [Safe with { ProcessId = 99 }];
        yield return [Safe with { AllowedProcessId = 0 }];
        yield return [Safe with { ProcessName = "WindowsTerminal" }];
        yield return [Safe with { IsPassword = true }];
        yield return [Safe with { IsEnabled = false }];
        yield return [Safe with { IsReadOnly = true }];
        yield return [Safe with { HasSelection = true }];
        yield return [Safe with { ModifiersDown = true }];
        yield return [Safe with { ImeComposing = true }];
        yield return [Safe with { OnInteractiveDesktop = false }];
        yield return [Safe with { HasTextPattern = false }];
    }

    [Fact]
    public void EmptyAndStandardTextClipboardCanBePreserved()
    {
        Assert.True(ProbePolicies.CanSnapshot([]));
        Assert.True(ProbePolicies.CanSnapshot([1, 7, 13, 16]));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(15u)]
    [InlineData(49321u)]
    public void UnsupportedClipboardFormatStopsBeforeMutation(uint format) => Assert.False(ProbePolicies.CanSnapshot([13, format]));

    [Theory]
    [InlineData(12u, 12u, true, true)]
    [InlineData(12u, 13u, true, false)]
    [InlineData(12u, 12u, false, false)]
    [InlineData(0u, 0u, true, false)]
    public void OnlyVerifiedAndStillOwnedClipboardCanBeRestored(uint owned, uint current, bool verified, bool expected)
        => Assert.Equal(expected, ProbePolicies.CanRestore(owned, current, verified));
}

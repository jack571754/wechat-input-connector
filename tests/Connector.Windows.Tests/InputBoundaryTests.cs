using System.Text.Json;
using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class InputBoundaryTests
{
    [Fact]
    public void NativeSuccessMapsToSuccessWithoutInventingFailure()
    {
        var result = new NativeProbeResult("Dispatched", "已派发", "Untouched");
        Assert.Equal(Connector.Core.EventState.Dispatched, result.ToOutcome().State);
        Assert.Equal(Connector.Core.InputFailure.NoInput, result.ToOutcome().Failure);
    }
    [Fact]
    public void MereFocusableElementIsNotAnEditableTarget()
    {
        Assert.False(ProbePolicies.IsEditableTarget(false, false, false));
        Assert.True(ProbePolicies.IsEditableTarget(true, false, false));
        Assert.True(ProbePolicies.IsEditableTarget(false, true, false));
        Assert.True(ProbePolicies.IsEditableTarget(false, false, true));
    }

    [Theory]
    [InlineData("第一行\n第二行")]
    [InlineData("第一行\r\n第二行")]
    [InlineData("a\tb")]
    public void GenericKeyboardCannotTurnTextControlsIntoActions(string text)
        => Assert.False(ProbePolicies.CanUseUnicode(text));

    [Fact]
    public void SingleLineUnicodeStillAllowsChineseAndEmoji()
        => Assert.True(ProbePolicies.CanUseUnicode("你好🙂"));

    [Fact]
    public void HelperRequestPreservesExplicitEnterAndNeverPrintsText()
    {
        var request = new NativeProbeRequest("PRIVATE_MESSAGE", 12, true);
        var restored = JsonSerializer.Deserialize<NativeProbeRequest>(JsonSerializer.Serialize(request))!;
        Assert.True(restored.AutoEnter);
        Assert.Equal("PRIVATE_MESSAGE", restored.Text);
        Assert.DoesNotContain("PRIVATE_MESSAGE", request.ToString());
    }
}

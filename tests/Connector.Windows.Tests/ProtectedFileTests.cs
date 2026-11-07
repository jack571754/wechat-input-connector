using System.Text;
using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class ProtectedFileTests
{
    public sealed record SecretData(string Token, string Text);

    [Fact]
    public void CredentialsRoundTripWithoutPlaintextAndAtomicReplacement()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "credentials.dpapi");
        var first = new SecretData("TOKEN_SHOULD_NEVER_BE_PLAINTEXT", "测试正文");
        ProtectedFile.Save(path, first);
        Assert.Equal(first, ProtectedFile.Load<SecretData>(path));
        Assert.DoesNotContain("TOKEN_SHOULD", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.DoesNotContain("测试正文", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        var second = new SecretData("REPLACED_TOKEN", "新正文");
        ProtectedFile.Save(path, second);
        Assert.Equal(second, ProtectedFile.Load<SecretData>(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}

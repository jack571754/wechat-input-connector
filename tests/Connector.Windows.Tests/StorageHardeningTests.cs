using System.Security.AccessControl;
using System.Security.Principal;
using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class StorageHardeningTests
{
    private static string DirectoryPath() => Path.Combine(AppContext.BaseDirectory, "TestResults", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PreviousGenerationSurvivesAndCorruptCurrentNeverSilentlyFallsBack()
    {
        var path = Path.Combine(DirectoryPath(), "state.dpapi");
        ProtectedFile.Save(path, new[] { "first" });
        ProtectedFile.Save(path, new[] { "second" });
        Assert.True(File.Exists(path + ".bak"));
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.ThrowsAny<Exception>(() => ProtectedFile.Load<string[]>(path));
        Assert.Equal(new[] { "first" }, ProtectedFile.Load<string[]>(path + ".bak"));
    }

    [Fact]
    public void PrivateDirectoryDoesNotInheritBroadWritePermissions()
    {
        var directory = DirectoryPath();
        DataDirectory.EnsurePrivate(directory);
        var acl = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.DoesNotContain(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            x => x.IdentityReference.Value == "S-1-5-11" && x.AccessControlType == AccessControlType.Allow);
    }

    [Fact]
    public void MigrationNeverOverwritesAnExistingIdentity()
    {
        var source = DirectoryPath(); var target = DirectoryPath();
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        ProtectedFile.Save(Path.Combine(source, "probe-state.dpapi"), "source");
        ProtectedFile.Save(Path.Combine(target, "probe-state.dpapi"), "target");
        Assert.Throws<IOException>(() => DataDirectory.Migrate(source, target));
        Assert.Equal("target", ProtectedFile.Load<string>(Path.Combine(target, "probe-state.dpapi")));
    }
}

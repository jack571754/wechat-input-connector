using System.Security.AccessControl;
using System.Security.Principal;
using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class StorageHardeningTests
{
    [Fact]
    public void HeldInstanceNeverRunsMigrationStartup()
    {
        var name = "Local\\wic-test-" + Guid.NewGuid();
        using var ready = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var owner = new Thread(() => ApplicationInstance.Run(name, () => { ready.Set(); release.Wait(TimeSpan.FromSeconds(10)); }));
        owner.Start(); Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        var called = false;
        try { Assert.False(ApplicationInstance.Run(name, () => called = true)); Assert.False(called); }
        finally { release.Set(); owner.Join(); }
    }

    [Fact]
    public void InterruptedMigrationCannotPublishIdentityAndCanRetry()
    {
        var source = DirectoryPath(); var target = DirectoryPath(); Directory.CreateDirectory(source);
        ProtectedFile.Save(Path.Combine(source, "probe-state.dpapi"), "identity");
        File.WriteAllText(Path.Combine(source, "settings.json"), "{\"AutoInput\":false}");
        using (var locked = new FileStream(Path.Combine(source, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => DataDirectory.Migrate(source, target));
        Assert.False(File.Exists(Path.Combine(target, "probe-state.dpapi")));
        Assert.Throws<IOException>(() => DataDirectory.EnsureReady(target));
        DataDirectory.Migrate(source, target); DataDirectory.EnsureReady(target);
        Assert.Equal("identity", ProtectedFile.Load<string>(Path.Combine(target, "probe-state.dpapi")));
        Assert.Contains("false", File.ReadAllText(Path.Combine(target, "settings.json")));
    }
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

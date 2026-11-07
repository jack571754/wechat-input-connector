using System.Diagnostics;
using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class OwnedHelperJobTests
{
    [Fact]
    public void ClosingOwnershipJobTerminatesOnlyItsHelper()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) info.ArgumentList.Add(arg);
        using var helper = Process.Start(info)!;
        try
        {
            using (var job = new OwnedHelperJob()) { job.Attach(helper); Assert.False(helper.HasExited); }
            Assert.True(helper.WaitForExit(5000));
        }
        finally { if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(); } }
    }
}

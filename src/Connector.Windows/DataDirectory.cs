using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Connector.Windows;

public static class DataDirectory
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatInputConnector");

    public static void EnsurePrivate(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("数据目录不允许使用链接。");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    public static void Migrate(string source, string target)
    {
        source = Path.GetFullPath(source); target = Path.GetFullPath(target);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
        var marker = Path.Combine(target, "migration-incomplete");
        if (File.Exists(Path.Combine(target, "probe-state.dpapi")) && !File.Exists(marker)) throw new IOException("目标目录已有微信身份，禁止覆盖迁移。");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("迁移源不允许使用链接。");
        EnsurePrivate(target);
        File.WriteAllText(marker, "1");
        var staging = Path.Combine(target, "migration.pending");
        Directory.CreateDirectory(staging);
        var names = new List<string>();
        foreach (var name in new[] { "settings.json", "clipboard-recovery.dpapi", "input-recovery.dpapi", "probe-state.dpapi" })
        {
            var path = Path.Combine(source, name);
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("迁移文件不允许使用链接。");
            File.Copy(path, Path.Combine(staging, name), true); names.Add(name);
        }
        if (!names.Contains("probe-state.dpapi")) throw new IOException("迁移源缺少微信身份文件。");
        foreach (var name in names) File.Move(Path.Combine(staging, name), Path.Combine(target, name), true);
        File.Delete(marker);
    }

    public static void EnsureReady(string directory)
    {
        if (File.Exists(Path.Combine(directory, "migration-incomplete")))
            throw new IOException("上次迁移未完成，请使用原迁移命令重试；未启动收发或输入。");
    }
}

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
        if (File.Exists(Path.Combine(target, "probe-state.dpapi"))) throw new IOException("目标目录已有微信身份，禁止覆盖迁移。");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("迁移源不允许使用链接。");
        EnsurePrivate(target);
        foreach (var name in new[] { "probe-state.dpapi", "settings.json", "clipboard-recovery.dpapi", "input-recovery.dpapi" })
        {
            var path = Path.Combine(source, name);
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("迁移文件不允许使用链接。");
            File.Copy(path, Path.Combine(target, name), false);
        }
    }
}

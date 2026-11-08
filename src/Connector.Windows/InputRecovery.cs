using System.IO;

namespace Connector.Windows;

public static class InputRecovery
{
    public static string? ArchiveUncertain(string recoveryPath)
    {
        if (!File.Exists(recoveryPath)) return null;
        var archived = recoveryPath + "." + Guid.NewGuid().ToString("N") + ".archived.dpapi";
        // 上条输入已由协调器记为结果不明；保留阶段记录，但不再阻塞下一条输入。
        File.Move(recoveryPath, archived);
        File.SetLastWriteTimeUtc(archived, DateTime.UtcNow);
        foreach (var old in new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(recoveryPath))!)
            .EnumerateFiles(Path.GetFileName(recoveryPath) + ".*.archived.dpapi").OrderByDescending(x => x.LastWriteTimeUtc).Skip(20))
            old.Delete();
        return archived;
    }

    public static void ClearReviewed(string directory)
    {
        if (File.Exists(Path.Combine(directory, "clipboard-recovery.dpapi")))
            throw new LegacyClipboardRecoveryException();
        // 仅在用户明确检查输入结果后移除本连接器的阶段元数据，不触碰剪贴板。
        File.Delete(Path.Combine(directory, "input-recovery.dpapi"));
    }
}

public sealed class LegacyClipboardRecoveryException() : IOException("存在旧版剪贴板恢复记录，已保持暂停。");

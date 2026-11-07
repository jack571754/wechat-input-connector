using System.IO;

namespace Connector.Windows;

public static class InputRecovery
{
    public static void ClearReviewed(string directory)
    {
        if (File.Exists(Path.Combine(directory, "clipboard-recovery.dpapi")))
            throw new LegacyClipboardRecoveryException();
        // 仅在用户明确检查输入结果后移除本连接器的阶段元数据，不触碰剪贴板。
        File.Delete(Path.Combine(directory, "input-recovery.dpapi"));
    }
}

public sealed class LegacyClipboardRecoveryException() : IOException("存在旧版剪贴板恢复记录，已保持暂停。");

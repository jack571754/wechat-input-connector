using System.Security.Cryptography;
using System.IO;
using System.Text.Json;

namespace Connector.Windows;

public static class ProtectedFile
{
    public static void Save<T>(string path, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static T Load<T>(string path)
    {
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("本地加密数据无效。"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

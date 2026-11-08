using System.Security.Cryptography;
using System.IO;
using System.Text.Json;

namespace Connector.Windows;

public static class ProtectedFile
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private sealed record Envelope<T>(int Format, T Payload);
    private static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static void Save<T>(string path, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(1, value), Options);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            if (encrypted.Length > MaximumBytes) throw new InvalidDataException("本地状态超过安全大小上限。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                // 只保留上一代密文；读取损坏时不自动回退旧游标或重放输入。
                _ = Load<JsonElement>(path);
                File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: false);
            }
            else File.Move(temporary, path);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static T Load<T>(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("本地状态超过安全大小上限。");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            using var document = JsonDocument.Parse(plain);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Format", out var format) && root.TryGetProperty("Payload", out var payload))
            {
                if (!format.TryGetInt32(out var version) || version != 1) throw new InvalidDataException("本地状态版本不受支持。");
                return payload.Deserialize<T>() ?? throw new InvalidDataException("本地加密数据无效。");
            }
            return root.Deserialize<T>() ?? throw new InvalidDataException("本地加密数据无效。");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

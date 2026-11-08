using System.Runtime.InteropServices;

namespace Connector.Windows;

public static class ClipboardFallback
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 20;

    public static bool CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var opened = TryOpenClipboard();
        if (!opened) return false;

        try
        {
            if (!NativeClipboard.EmptyClipboard()) return false;

            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var hGlobal = NativeClipboard.GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (hGlobal == 0) return false;

            var target = NativeClipboard.GlobalLock(hGlobal);
            if (target == 0)
            {
                NativeClipboard.GlobalFree(hGlobal);
                return false;
            }

            try
            {
                var chars = text.ToCharArray();
                Marshal.Copy(chars, 0, target, chars.Length);
                Marshal.WriteInt16(target + (chars.Length * sizeof(char)), 0);
            }
            finally
            {
                NativeClipboard.GlobalUnlock(hGlobal);
            }

            if (NativeClipboard.SetClipboardData(CF_UNICODETEXT, hGlobal) == 0)
            {
                NativeClipboard.GlobalFree(hGlobal);
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeClipboard.CloseClipboard();
        }
    }

    public static string? GetText()
    {
        var opened = TryOpenClipboard();
        if (!opened) return null;

        try
        {
            if (!NativeClipboard.IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            var hData = NativeClipboard.GetClipboardData(CF_UNICODETEXT);
            if (hData == 0) return null;

            var ptr = NativeClipboard.GlobalLock(hData);
            if (ptr == 0) return null;

            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                NativeClipboard.GlobalUnlock(hData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeClipboard.CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (var i = 0; i < MaxRetries; i++)
        {
            if (NativeClipboard.OpenClipboard(nint.Zero)) return true;
            if (i < MaxRetries - 1) Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    private static class NativeClipboard
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool OpenClipboard(nint hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetClipboardData(uint uFormat, nint hMem);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool IsClipboardFormatAvailable(uint uFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GlobalLock(nint hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalUnlock(nint hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GlobalFree(nint hMem);
    }
}

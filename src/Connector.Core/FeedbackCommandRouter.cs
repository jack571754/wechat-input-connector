namespace Connector.Core;

public static class FeedbackCommandRouter
{
    private static readonly HashSet<string> ScreenshotCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "截图",
        "截屏",
        "看屏幕",
        "屏幕",
        "/截图",
        "/截屏",
        "/屏幕"
    };

    public static bool IsScreenshotCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return ScreenshotCommands.Contains(text.Trim());
    }
}

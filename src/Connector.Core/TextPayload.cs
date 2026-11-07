namespace Connector.Core;

public static class TextPayload
{
    public static bool IsSafe(string text) => text.Length is > 0 and <= 10000
        && !text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t');
}

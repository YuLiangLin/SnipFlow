using SnipFlow.Services;

namespace SnipFlow.CodeCapture;

internal sealed record CodeImageOptions(
    string? HighlightingName, string FontName, double FontSize, int Width,
    bool WordWrap, bool LineNumbers, int StartingLine, bool Dark);

internal static class CodeImageLimits
{
    internal const int MaxCharacters = 100_000;
    internal const int MaxLines = 2_000;
    internal const int MaxLineLength = 4_096;
    internal const int MaxHeight = 16_384;
    internal const long MaxPixels = 40_000_000;

    // Validate before handing text to either the syntax engine or WPF's formatter.
    // Reject the entire input instead of changing or truncating the user's code.
    internal static void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxCharacters)
            throw new ArgumentException(I18n.F("程式碼超過 {0:N0} 字元，請分段製作。", MaxCharacters));
        int lines = 1, length = 0;
        foreach (char value in text)
        {
            if (value == '\r' || value == '\n')
            {
                if (value == '\n') lines++;
                length = 0;
            }
            else if (++length > MaxLineLength)
                throw new ArgumentException(I18n.F("單行超過 {0:N0} 字元，請先拆分長行。", MaxLineLength));
        }
        // AvalonEdit accepts lone CR as a line ending as well as LF and CRLF.
        for (int index = 0; index < text.Length; index++)
            if (text[index] == '\r' && (index + 1 == text.Length || text[index + 1] != '\n')) lines++;
        if (lines > MaxLines)
            throw new ArgumentException(I18n.F("程式碼超過 {0:N0} 行，請分段製作。", MaxLines));
    }
}

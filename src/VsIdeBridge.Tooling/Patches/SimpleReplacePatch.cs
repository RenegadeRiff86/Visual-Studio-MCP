using System;
using System.Text;

namespace VsIdeBridge.Tooling.Patches;

public static class SimpleReplacePatch
{
    public const string Header = "simple-replace";

    public static bool IsHeader(string? header) =>
        string.Equals(header, Header, StringComparison.Ordinal);

    public static string ReplaceInline(
        string text,
        string oldText,
        string newText,
        bool replaceAll,
        out int replacementCount)
    {
        replacementCount = 0;
        if (oldText.Length == 0)
        {
            return text;
        }

        int firstIndex = text.IndexOf(oldText, StringComparison.Ordinal);
        if (firstIndex < 0)
        {
            return text;
        }

        if (!replaceAll)
        {
            replacementCount = 1;
            return new StringBuilder(text.Length - oldText.Length + newText.Length)
                .Append(text, 0, firstIndex)
                .Append(newText)
                .Append(text, firstIndex + oldText.Length, text.Length - firstIndex - oldText.Length)
                .ToString();
        }

        StringBuilder builder = new(text.Length);
        int currentIndex = 0;
        int matchIndex = firstIndex;
        while (matchIndex >= 0)
        {
            builder.Append(text, currentIndex, matchIndex - currentIndex);
            builder.Append(newText);
            replacementCount++;
            currentIndex = matchIndex + oldText.Length;
            matchIndex = text.IndexOf(oldText, currentIndex, StringComparison.Ordinal);
        }

        builder.Append(text, currentIndex, text.Length - currentIndex);
        return builder.ToString();
    }
}

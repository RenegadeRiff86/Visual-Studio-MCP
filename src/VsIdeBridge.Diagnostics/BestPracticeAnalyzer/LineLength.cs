using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using static VsIdeBridge.Diagnostics.ErrorListConstants;

namespace VsIdeBridge.Diagnostics;

internal static partial class BestPracticeAnalyzer
{
    // BP1046: line too long

    public static IEnumerable<JObject> FindLongLines(string file, string content)
    {
        string[] lines = content.Split('\n');
        int findingCount = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length <= LineTooLongThreshold)
            {
                continue;
            }

            // Skip URL-only lines — they cannot be meaningfully wrapped.
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int over = line.Length - LineTooLongThreshold;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1046",
                message: $"Line is {line.Length} chars ({over} over the {LineTooLongThreshold}-char limit). Wrap or break the expression.",
                file: file,
                line: i + 1,
                symbol: "line-length",
                helpUri: BP1046HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }
}

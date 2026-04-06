using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using static VsIdeBridge.Diagnostics.BestPracticeAnalyzerHelpers;
using static VsIdeBridge.Diagnostics.ErrorListConstants;
using static VsIdeBridge.Diagnostics.ErrorListPatterns;

namespace VsIdeBridge.Diagnostics;

internal static partial class BestPracticeAnalyzer
{
    // ── BP1030: Mojibake / encoding corruption ────────────────────────────────

    public static IEnumerable<JObject> FindMojibake(string file, string content)
    {
        if (Path.GetFileName(file).Equals("BestPracticeAnalyzer.cs", StringComparison.OrdinalIgnoreCase)
#if NET5_0_OR_GREATER
            && content.Contains("MojibakeSignatures", StringComparison.Ordinal))
#else
            && content.IndexOf("MojibakeSignatures", StringComparison.Ordinal) >= 0)
#endif
        {
            yield break;
        }

#if NET5_0_OR_GREATER
        if (!MojibakeSignatures.Any(sig => content.Contains(sig, StringComparison.Ordinal)))
#else
        if (!MojibakeSignatures.Any(sig => content.IndexOf(sig, StringComparison.Ordinal) >= 0))
#endif
        {
            yield break;
        }

        int lineNumber = 1;
        string[] lines = content.Split(['\r', '\n'], StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
#if NET5_0_OR_GREATER
            if (MojibakeSignatures.Any(sig => lines[i].Contains(sig, StringComparison.Ordinal)))
#else
            if (MojibakeSignatures.Any(sig => lines[i].IndexOf(sig, StringComparison.Ordinal) >= 0))
#endif
            {
                lineNumber = i + 1;
                break;
            }
        }

        yield return DiagnosticRowFactory.CreateBestPracticeRow(
            code: "BP1030",
            message: "Mojibake (encoding corruption) detected in this file. " +
                     "UTF-8 characters appear to have been incorrectly decoded as Windows-1252. " +
                     "This typically occurs when an AI model or tool mishandles file encoding. " +
                     "Please fix the encoding (ensure UTF-8 with or without BOM) and verify the file.",
            file: file,
            line: lineNumber,
            symbol: "Mojibake",
            helpUri: BP1030HelpUri);
    }

    // PowerShell / VB / F# / Python late-platform rules ------------------------

    public static IEnumerable<JObject> FindWriteHostUsage(string file, string content)
    {
#if NET5_0_OR_GREATER
        MatchCollection matches = WriteHostPattern().Matches(content);
#else
        MatchCollection matches = Regex.Matches(content, @"(?im)^\s*Write-Host\b");
#endif
        int findingCount = 0;
        foreach (Match match in matches)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1031",
                message: "Write-Host writes directly to the host and is hard to test or redirect. Prefer Write-Output, Write-Information, or structured logging in automation scripts.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "Write-Host",
                helpUri: BP1031HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
        }
    }

    public static IEnumerable<JObject> FindMissingStrictMode(string file, string content)
    {
#if NET5_0_OR_GREATER
        if (StrictModePattern().IsMatch(content))
#else
        if (Regex.IsMatch(content, @"(?im)^\s*Set-StrictMode\s+-Version\s+Latest\b"))
#endif
        {
            yield break;
        }

        yield return DiagnosticRowFactory.CreateBestPracticeRow(
            code: "BP1032",
            message: "PowerShell script does not enable Set-StrictMode -Version Latest. Enable strict mode so typos and uninitialized variables fail fast.",
            file: file,
            line: 1,
            symbol: "Set-StrictMode",
            helpUri: BP1032HelpUri);
    }

    public static IEnumerable<JObject> FindMissingOptionStrict(string file, string content)
    {
        if (VbOptionStrictOnPattern().IsMatch(content))
        {
            yield break;
        }

        string message = VbOptionStrictOffPattern().IsMatch(content)
            ? "Visual Basic file sets Option Strict Off. Prefer Option Strict On so narrowing conversions and late binding are caught early."
            : "Visual Basic file does not enable Option Strict On. Prefer Option Strict On so narrowing conversions and late binding are caught early.";

        yield return DiagnosticRowFactory.CreateBestPracticeRow(
            code: "BP1036",
            message: message,
            file: file,
            line: 1,
            symbol: "Option Strict",
            helpUri: BP1036HelpUri);
    }

    public static IEnumerable<JObject> FindFSharpMutableState(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in FSharpMutablePattern().Matches(content))
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1039",
                message: "F# code uses 'mutable'. Prefer immutable values by default and encapsulate mutation tightly when performance requires it.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "mutable",
                helpUri: BP1039HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    public static IEnumerable<JObject> FindFSharpBlockComments(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in FSharpBlockCommentPattern().Matches(content))
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1040",
                message: "F# block comments '(* ... *)' are harder to scan than line comments. Prefer brief '//' comments for routine guidance.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "(* *)",
                helpUri: BP1040HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    public static IEnumerable<JObject> FindNoneEqualityComparison(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in PythonNoneComparisonPattern().Matches(content))
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1041",
                message: "Python compares to None with == or !=. Prefer 'is None' or 'is not None'.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "None",
                helpUri: BP1041HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

#if NET5_0_OR_GREATER
    [System.Text.RegularExpressions.GeneratedRegex(@"(?im)^\s*Write-Host\b")]
    private static partial Regex WriteHostPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?im)^\s*Set-StrictMode\s+-Version\s+Latest\b")]
    private static partial Regex StrictModePattern();
#endif

    public static IEnumerable<JObject> FindPowerShellAliases(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in PowerShellAliasPattern().Matches(content))
        {
            string aliasName = match.Groups["alias"].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1042",
                message: $"PowerShell alias '{aliasName}' is harder to read and review in automation. Prefer the full cmdlet name.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: aliasName,
                helpUri: BP1042HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

}

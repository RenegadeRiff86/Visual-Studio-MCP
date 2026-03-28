using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using static VsIdeBridge.Diagnostics.ErrorListConstants;
using static VsIdeBridge.Diagnostics.ErrorListPatterns;

namespace VsIdeBridge.Diagnostics;

internal static class BestPracticeAnalyzer
{
    // Keep these comments ASCII-only so the analyzer does not flag its own file
    // while still matching the escaped mojibake signatures below.
    // Number of extra lines beyond the match line to scan for an opening brace
    // when distinguishing real method bodies from call expressions in FindLongMethods.
    private const int MethodBraceLookAheadLines = 2;

    private static readonly string[] MojibakeSignatures =
    [
        "\u00C3\u0192",       // Misread UTF-8 prefix sequence.
        "\u00C2\u0080",       // Latin-1 control-character sequence.
        "\u00EF\u00BF\u00BD", // UTF-8 replacement-character sequence.
        "\u00E2\u0082\u00AC", // Misread euro-sign sequence.
        "\u00C3\u0080",       // Another common double-decoding prefix.
        "\u00E2\u0080",       // Smart quote or dash prefix sequence.
        "\u00C3\u0081",       // Another common double-decoding prefix.
        "\u00E2\u0084\u00A2", // Misread trademark-sign sequence.
    ];

    // ── Public entry point ────────────────────────────────────────────────────

    public static IEnumerable<JObject> AnalyzeFile(string file, string content)
    {
        CodeLanguage language = GetLanguage(file);

        // Cross-language rules
        IEnumerable<JObject> findings = FindRepeatedStringLiterals(file, content)
            .Concat(FindMagicNumbers(file, content))
            .Concat(FindFileTooLong(file, content))
            .Concat(FindLongMethods(file, content, language))
            .Concat(FindPoorNaming(file, content, language))
            .Concat(FindDeepNesting(file, content, language))
            .Concat(FindCommentedOutCode(file, content, language))
            .Concat(FindMixedIndentation(file, content))
            .Concat(FindMojibake(file, content));

        // Language-specific rules
        if (language == CodeLanguage.CSharp)
        {
            findings = findings
                .Concat(FindImplicitVarUsage(file, content))
                .Concat(FindBroadCatchException(file, content))
                .Concat(FindFrameworkTypeAliases(file, content))
                .Concat(FindLongMainThreadScopes(file, content))
                .Concat(FindSuspiciousRoundDown(file, content))
                .Concat(FindEmptyCatchBlocks(file, content))
                .Concat(FindAsyncVoid(file, content))
                .Concat(FindGodClass(file, content))
                .Concat(FindPropertyBagClass(file, content))
                .Concat(FindMissingUsing(file, content))
                .Concat(FindDateTimeInLoop(file, content))
                .Concat(FindDynamicObjectOveruse(file, content))
                .Concat(FindUnnecessaryComments(file, content))
                .Concat(FindNamespaceFolderStructureIssues(file, content));
        }
        else if (language == CodeLanguage.Cpp)
        {
            findings = findings
                .Concat(FindRawDelete(file, content))
                .Concat(FindCStyleCasts(file, content))
                .Concat(FindRawNew(file, content))
                .Concat(FindMacroOveruse(file, content))
                .Concat(FindDeepInheritance(file, content))
                .Concat(FindMissingConst(file, content));
            if (IsHeaderFile(file))
            {
                findings = findings.Concat(FindUsingNamespaceInHeader(file, content));
            }
        }
        else if (language == CodeLanguage.Python)
        {
            findings = findings
                .Concat(FindBareExcept(file, content))
                .Concat(FindMutableDefaultArgs(file, content))
                .Concat(FindImportStar(file, content))
                .Concat(FindBooleanComparison(file, content))
                .Concat(FindNoneEqualityComparison(file, content));
        }
        else if (language == CodeLanguage.VisualBasic)
        {
            findings = findings
                .Concat(FindMissingOptionStrict(file, content))
                .Concat(FindVbMultipleStatementsPerLine(file, content))
                .Concat(FindVbExplicitLineContinuation(file, content));
        }
        else if (language == CodeLanguage.FSharp)
        {
            findings = findings
                .Concat(FindFSharpMutableState(file, content))
                .Concat(FindFSharpBlockComments(file, content));
        }
        else if (language == CodeLanguage.PowerShell)
        {
            findings = findings
                .Concat(FindWriteHostUsage(file, content))
                .Concat(FindMissingStrictMode(file, content))
                .Concat(FindPowerShellAliases(file, content));
        }

        return findings;
    }

    // ── Language detection ────────────────────────────────────────────────────

    public static CodeLanguage GetLanguage(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => CodeLanguage.CSharp,
            ".vb" => CodeLanguage.VisualBasic,
            ".fs" or ".fsi" or ".fsx" => CodeLanguage.FSharp,
            ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx" => CodeLanguage.Cpp,
            ".py" => CodeLanguage.Python,
            ".ps1" or ".psm1" or ".psd1" => CodeLanguage.PowerShell,
            _ => CodeLanguage.Unknown,
        };
    }

    public static bool IsHeaderFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".h" or ".hh" or ".hpp" or ".hxx";
    }

    // ── BP1001: Repeated string literals ──────────────────────────────────────

    public static IEnumerable<JObject> FindRepeatedStringLiterals(string file, string content)
    {
        IEnumerable<IGrouping<string, Match>> occurrences = StringLiteralPattern.Matches(content)
            .Cast<Match>()
            .Where(m => !IsInsideStringLiteral(content, m.Index))
            .Where(m => !ConstStringDeclPattern.IsMatch(GetLineAt(content, m.Index)))
            .GroupBy(match => match.Groups[1].Value)
            .Where(group => group.Count() >= RepeatedStringThreshold)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(MaxBestPracticeFindingsPerFile);

        foreach (IGrouping<string, Match> repeated in occurrences)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1001",
                message: $"String literal '{repeated.Key}' is repeated {repeated.Count()} times. Extract a constant.",
                file: file,
                line: GetLineNumber(content, repeated.First().Index),
                symbol: repeated.Key,
                helpUri: BP1001HelpUri);
        }
    }

    // ── BP1002: Magic numbers ─────────────────────────────────────────────────

    public static IEnumerable<JObject> FindMagicNumbers(string file, string content)
    {
        IEnumerable<IGrouping<string, (Match Match, string Value)>> matches =
            NumberLiteralPattern.Matches(content)
            .Cast<Match>()
            .Select(match => (Match: match, Value: match.Groups["value"].Value))
            .Where(item => item.Value is not "0" and not "1" and not "-1")
        .Where(item => !IsInsideStringLiteral(content, item.Match.Index))
            .Where(item => !IsInsideLineComment(content, item.Match.Index))
            .GroupBy(item => item.Value)
            .Where(group => group.Count() >= RepeatedNumberThreshold)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(MaxBestPracticeFindingsPerFile);

        foreach (IGrouping<string, (Match Match, string Value)> repeated in matches)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1002",
                message: $"Numeric literal '{repeated.Key}' appears {repeated.Count()} times. Use a named constant when it carries domain meaning, or add a short comment when the value is only local arithmetic.",
                file: file,
                line: GetLineNumber(content, repeated.First().Match.Index),
                symbol: repeated.Key,
                helpUri: BP1002HelpUri);
        }
    }

    // ── BP1003: Suspicious round-down cast (C#) ───────────────────────────────

    public static IEnumerable<JObject> FindSuspiciousRoundDown(string file, string content)
    {
        string[] lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        int findingCount = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            Match roundDownMatch = SuspiciousRoundDownPattern.Match(lines[i]);
            if (roundDownMatch.Success)
            {
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1003",
                    message: $"(int)Math.{roundDownMatch.Groups["op"].Value}(...) casts a float floor/truncate result to int. This pattern usually means integer division (/) is what you want instead.",
                    file: file,
                    line: i + 1,
                    symbol: $"Math.{roundDownMatch.Groups["op"].Value}",
                    helpUri: BP1003HelpUri);
                findingCount++;
                if (findingCount >= MaxSuppressionFindingsPerFile)
                {
                    yield break;
                }
            }
        }
    }

    // ── BP1004: Empty catch block (C#) ───────────────────────────────────────

    public static IEnumerable<JObject> FindEmptyCatchBlocks(string file, string content)
    {
        MatchCollection matches = EmptyCatchBlockPattern.Matches(content);
        int findingCount = 0;
        foreach (Match match in matches)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1004",
                message: "Empty catch block swallows exceptions silently. Log or rethrow.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "catch",
                helpUri: BP1004HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1005: async void (C#) ───────────────────────────────────────────────

    public static IEnumerable<JObject> FindAsyncVoid(string file, string content)
    {
        MatchCollection matches = AsyncVoidPattern.Matches(content);
        int findingCount = 0;
        foreach (Match match in matches)
        {
            if (IsInsideStringLiteral(content, match.Index))
                continue;
            string methodName = match.Groups[1].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1005",
                message: $"'async void {methodName}' has unobservable exceptions. Use 'async Task' instead.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: methodName,
                helpUri: BP1005HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1006: Raw delete (C++) ──────────────────────────────────────────────

    public static IEnumerable<JObject> FindRawDelete(string file, string content)
    {
        MatchCollection matches = RawDeletePattern.Matches(content);
        int findingCount = 0;
        foreach (Match match in matches)
        {
            string op = match.Groups[1].Success ? "delete[]" : "delete";
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1006",
                message: $"Raw '{op}' detected. Prefer smart pointers (std::unique_ptr, std::shared_ptr).",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: op,
                helpUri: BP1006HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1007: using namespace in header (C++) ───────────────────────────────

    public static IEnumerable<JObject> FindUsingNamespaceInHeader(string file, string content)
    {
        MatchCollection matches = UsingNamespacePattern.Matches(content);
        int findingCount = 0;
        foreach (Match match in matches)
        {
            string ns = match.Groups[1].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1007",
                message: $"'using namespace {ns}' in header file pollutes the global namespace of every includer.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: ns,
                helpUri: BP1007HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1008: C-style cast (C++) ────────────────────────────────────────────

    public static IEnumerable<JObject> FindCStyleCasts(string file, string content)
    {
        MatchCollection matches = CStyleCastPattern.Matches(content);
        int findingCount = 0;
        foreach (Match match in matches)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1008",
                message: $"C-style cast '{match.Value.TrimEnd()}' detected. Prefer static_cast, reinterpret_cast, or const_cast.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: match.Value.Trim(),
                helpUri: BP1008HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1009: Bare except (Python) ──────────────────────────────────────────

    public static IEnumerable<JObject> FindBareExcept(string file, string content)
    {
        MatchCollection matches = BareExceptPattern.Matches(content);
        int findingCount = 0;
        foreach (Match match in matches)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1009",
                message: "Bare 'except:' catches all exceptions including SystemExit and KeyboardInterrupt. Catch specific exceptions.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "except",
                helpUri: BP1009HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1010: Mutable default argument (Python) ─────────────────────────────

    public static IEnumerable<JObject> FindMutableDefaultArgs(string file, string content)
    {
        MatchCollection matches = MutableDefaultArgPattern.Matches(content);
        int findingCount = 0;
        foreach (Match match in matches)
        {
            string funcName = match.Groups[1].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1010",
                message: $"Function '{funcName}' uses a mutable default argument. Use None and initialize inside the function.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: funcName,
                helpUri: BP1010HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1011: from X import * (Python) ──────────────────────────────────────

    public static IEnumerable<JObject> FindImportStar(string file, string content)
    {
        MatchCollection matches = ImportStarPattern.Matches(content);
        int findingCount = 0;
        foreach (Match match in matches)
        {
            string module = match.Groups[1].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1011",
                message: $"'from {module} import *' pollutes the namespace. Import specific names.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: module,
                helpUri: BP1011HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1012: File too long ─────────────────────────────────────────────────

    public static IEnumerable<JObject> FindFileTooLong(string file, string content)
    {
        int lineCount = content.Split('\n').Length;
        if (lineCount >= FileTooLongErrorThreshold)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1012",
                message: $"File is {lineCount} lines long (threshold: {FileTooLongErrorThreshold}). Extract functionality into a class library using create_project.",
                file: file,
                line: 1,
                symbol: Path.GetFileName(file),
                helpUri: BP1012HelpUri);
        }
        else if (lineCount >= FileTooLongWarningThreshold)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1012",
                message: $"File is {lineCount} lines long (threshold: {FileTooLongWarningThreshold}). Consider extracting into a class library using create_project.",
                file: file,
                line: 1,
                symbol: Path.GetFileName(file),
                helpUri: BP1012HelpUri);
        }
    }

    // ── BP1013: Method/function too long ──────────────────────────────────────

    public static IEnumerable<JObject> FindLongMethods(string file, string content, CodeLanguage language)
    {
        string[] lines = content.Split('\n');
        Regex? pattern = language switch
        {
            CodeLanguage.CSharp => CSharpMethodSignaturePattern,
            CodeLanguage.Python => PythonDefPattern,
            CodeLanguage.Cpp => CppFunctionPattern,
            _ => null,
        };
        if (pattern is null)
        {
            yield break;
        }

        int findingCount = 0;
        MatchCollection matches = pattern.Matches(content);
        foreach (Match match in matches)
        {
            string methodName = match.Groups[1].Value;
            int startLine = GetLineNumber(content, match.Index);
            int methodLength;
            if (language == CodeLanguage.Python)
            {
                methodLength = CountPythonFunctionLines(lines, startLine - 1);
            }
            else
            {
                // Guard: real block-body methods have their opening brace within 2 lines.
                // Calls and expression-bodied members don't -- skip them to avoid false positives.
                bool hasNearbyBrace = false;
                for (int i = startLine - 1; i < Math.Min(lines.Length, startLine + MethodBraceLookAheadLines); i++)
                {
                    if (lines[i].IndexOf('{') >= 0) { hasNearbyBrace = true; break; }
                }
                if (!hasNearbyBrace)
                    continue;
                methodLength = CountBracedBlockLines(lines, startLine - 1);
            }

            if (methodLength > MethodTooLongThreshold)
            {
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1013",
                    message: $"Method '{methodName}' is {methodLength} lines long (threshold: {MethodTooLongThreshold}). Break into smaller methods.",
                    file: file,
                    line: startLine,
                    symbol: methodName,
                    helpUri: BP1013HelpUri);
                findingCount++;
                if (findingCount >= MaxSuppressionFindingsPerFile)
                {
                    yield break;
                }
            }
        }
    }

    // ── BP1014: Poor/vague naming ─────────────────────────────────────────────

    public static IEnumerable<JObject> FindPoorNaming(string file, string content, CodeLanguage language)
    {
        int findingCount = 0;
        if (language == CodeLanguage.CSharp)
        {
            foreach (Match match in SingleLetterVarPattern.Matches(content))
            {
                string name = match.Groups["name"].Value;
                if (name is "i" or "j" or "k" or "x" or "y" or "z" or "e" or "s" or "_")
                {
                    continue;
                }
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1014",
                    message: $"Single-letter variable '{name}' is unclear. Use a descriptive name.",
                    file: file,
                    line: GetLineNumber(content, match.Index),
                    symbol: name,
                    helpUri: BP1014HelpUri);
                findingCount++;
                if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
            }

            foreach (Match match in PoorCSharpNamingPattern.Matches(content))
            {
                string name = match.Groups["name"].Value;
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1014",
                    message: $"Vague variable name '{name}'. Use a name that describes the value's purpose.",
                    file: file,
                    line: GetLineNumber(content, match.Index),
                    symbol: name,
                    helpUri: BP1014HelpUri);
                findingCount++;
                if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
            }
        }
        else if (language == CodeLanguage.Python)
        {
            foreach (Match match in PythonSingleLetterAssignPattern.Matches(content))
            {
                string name = match.Groups["name"].Value;
                if (name is "i" or "j" or "k" or "x" or "y" or "z" or "e" or "s" or "_")
                {
                    continue;
                }
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1014",
                    message: $"Single-letter variable '{name}' is unclear. Use a descriptive name.",
                    file: file,
                    line: GetLineNumber(content, match.Index),
                    symbol: name,
                    helpUri: BP1014HelpUri);
                findingCount++;
                if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
            }

            foreach (Match match in PythonPoorNamingPattern.Matches(content))
            {
                string name = match.Groups["name"].Value;
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1014",
                    message: $"Vague variable name '{name}'. Use a name that describes the value's purpose.",
                    file: file,
                    line: GetLineNumber(content, match.Index),
                    symbol: name,
                    helpUri: BP1014HelpUri);
                findingCount++;
                if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
            }
        }
    }

    // ── BP1033: implicit var usage (C#) ───────────────────────────────────────

    public static IEnumerable<JObject> FindImplicitVarUsage(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in ImplicitVarPattern.Matches(content))
        {
            if (IsInsideStringLiteral(content, match.Index) || IsInsideLineComment(content, match.Index))
            {
                continue;
            }

            string name = match.Groups["name"].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1033",
                message: $"Implicitly typed local '{name}' uses 'var'. Prefer the explicit type unless the concrete type would be excessively noisy.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: name,
                helpUri: BP1033HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    public static IEnumerable<JObject> FindBroadCatchException(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in BroadCatchPattern.Matches(content))
        {
            if (IsInsideStringLiteral(content, match.Index) || IsInsideLineComment(content, match.Index))
            {
                continue;
            }

            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1034",
                message: "Catching general Exception makes failures harder to reason about. Catch a narrower exception type or let the failure propagate.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "Exception",
                helpUri: BP1034HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    public static IEnumerable<JObject> FindFrameworkTypeAliases(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in FrameworkTypePattern.Matches(content))
        {
            if (IsInsideStringLiteral(content, match.Index) || IsInsideLineComment(content, match.Index))
            {
                continue;
            }

            string typeName = match.Groups["type"].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1035",
                message: $"Use the C# keyword form instead of 'System.{typeName}' for built-in types where possible.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: typeName,
                helpUri: BP1035HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    public static IEnumerable<JObject> FindLongMainThreadScopes(string file, string content)
    {
        string[] lines = content.Split('\n');
        int findingCount = 0;

        foreach (Match match in CSharpMethodSignaturePattern.Matches(content))
        {
            string methodName = match.Groups[1].Value;
            int startLine = GetLineNumber(content, match.Index);

            bool hasNearbyBrace = false;
            for (int i = startLine - 1; i < Math.Min(lines.Length, startLine + MethodBraceLookAheadLines); i++)
            {
                if (lines[i].IndexOf('{') >= 0)
                {
                    hasNearbyBrace = true;
                    break;
                }
            }

            if (!hasNearbyBrace)
            {
                continue;
            }

            int methodLength = CountBracedBlockLines(lines, startLine - 1);
            int methodEndExclusive = Math.Min(lines.Length, startLine - 1 + methodLength);
            int switchLineIndex = -1;
            int nonEmptyLinesBeforeSwitch = 0;

            for (int i = startLine - 1; i < methodEndExclusive; i++)
            {
                string trimmedLine = lines[i].Trim();
                if (trimmedLine.Length > 0)
                {
                    nonEmptyLinesBeforeSwitch++;
                }

                if (trimmedLine.IndexOf("SwitchToMainThreadAsync", StringComparison.Ordinal) >= 0)
                {
                    switchLineIndex = i;
                    break;
                }
            }

            if (switchLineIndex < 0 || nonEmptyLinesBeforeSwitch > MainThreadSwitchEarlyLineThreshold)
            {
                continue;
            }

            int nonEmptyLinesAfterSwitch = 0;
            for (int i = switchLineIndex + 1; i < methodEndExclusive; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    nonEmptyLinesAfterSwitch++;
                }
            }

            if (nonEmptyLinesAfterSwitch < MainThreadScopeWarningThreshold)
            {
                continue;
            }

            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1043",
                message: $"Method '{methodName}' switches to the Visual Studio UI thread early and then keeps {nonEmptyLinesAfterSwitch} non-empty lines in that scope. Narrow the main-thread region.",
                file: file,
                line: switchLineIndex + 1,
                symbol: methodName,
                helpUri: BP1043HelpUri);

            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1015: Excessive nesting depth ───────────────────────────────────────

    public static IEnumerable<JObject> FindDeepNesting(string file, string content, CodeLanguage language)
    {
        return language == CodeLanguage.Python
            ? FindPythonDeepNesting(file, content)
            : FindCSharpDeepNesting(file, content);
    }

    private static IEnumerable<JObject> FindPythonDeepNesting(string file, string content)
    {
        string[] lines = content.Split('\n');
        HashSet<int> reported = [];

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { continue; }

            int indentLevel = (line.Length - line.TrimStart().Length) / 4;
            if (indentLevel >= DeepNestingThreshold && reported.Add(i))
            {
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1015",
                    message: $"Code is nested {indentLevel} levels deep (threshold: {DeepNestingThreshold}). " +
                             "Consider extracting a method or using guard clauses to reduce nesting.",
                    file: file,
                    line: i + 1,
                    symbol: $"Nesting depth {indentLevel}",
                    helpUri: BP1015HelpUri);

                if (reported.Count >= MaxFindingsPerFile) { yield break; }
            }
        }
    }

    private static IEnumerable<JObject> FindCSharpDeepNesting(string file, string content)
    {
        HashSet<int> reported = [];
        int currentDepth = 0;
        string[] lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            currentDepth += line.Count(ch => ch == '{');
            currentDepth -= line.Count(ch => ch == '}');
            if (currentDepth < 0) { currentDepth = 0; }

            if (currentDepth >= CSharpDeepNestingThreshold && reported.Add(i))
            {
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1015",
                    message: $"Code is nested {currentDepth} levels deep (threshold: {CSharpDeepNestingThreshold}). " +
                             "Consider extracting a method, using early returns/guard clauses, or restructuring the logic.",
                    file: file,
                    line: i + 1,
                    symbol: $"Nesting depth {currentDepth}",
                    helpUri: BP1015HelpUri);

                if (reported.Count >= 5) { yield break; }
            }
        }
    }

    // ── BP1016: Commented-out code blocks ─────────────────────────────────────

    public static IEnumerable<JObject> FindCommentedOutCode(string file, string content, CodeLanguage language)
    {
        Regex? pattern = language switch
        {
            CodeLanguage.CSharp => CSharpCommentedCodePattern,
            CodeLanguage.Python => PythonCommentedCodePattern,
            CodeLanguage.Cpp => CppCommentedCodePattern,
            _ => null,
        };
        if (pattern is null) { yield break; }

        string[] lines = content.Split('\n');
        int findingCount = 0;
        int consecutiveCount = 0;
        int blockStart = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                if (consecutiveCount == 0) { blockStart = i; }
                consecutiveCount++;
            }
            else
            {
                if (consecutiveCount >= CommentedOutCodeThreshold)
                {
                    yield return DiagnosticRowFactory.CreateBestPracticeRow(
                        code: "BP1016",
                        message: $"{consecutiveCount} consecutive lines of commented-out code. Remove dead code; use version control to recover it.",
                        file: file,
                        line: blockStart + 1,
                        symbol: "commented-code",
                        helpUri: BP1016HelpUri);
                    findingCount++;
                    if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
                }
                consecutiveCount = 0;
            }
        }

        if (consecutiveCount >= CommentedOutCodeThreshold)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1016",
                message: $"{consecutiveCount} consecutive lines of commented-out code at end of file. Remove dead code; use version control to recover it.",
                file: file,
                line: blockStart + 1,
                symbol: "commented-code",
                helpUri: BP1016HelpUri);
        }
    }

    // ── BP1017: Mixed indentation ─────────────────────────────────────────────

    public static IEnumerable<JObject> FindMixedIndentation(string file, string content)
    {
        bool hasTabs = TabIndentedLinePattern.IsMatch(content);
        bool hasSpaces = SpaceIndentedLinePattern.IsMatch(content);
        if (hasTabs && hasSpaces)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1017",
                message: "File mixes tabs and spaces for indentation. Pick one and be consistent.",
                file: file,
                line: 1,
                symbol: "indentation",
                helpUri: BP1017HelpUri);
        }
    }

    // ── BP1018: God class (C#) ────────────────────────────────────────────────

    public static IEnumerable<JObject> FindGodClass(string file, string content)
    {
        MatchCollection classMatches = CSharpClassDeclPattern.Matches(content);
        int findingCount = 0;

        foreach (Match classMatch in classMatches)
        {
            string className = classMatch.Groups[1].Value;
            // Partial classes spread methods across files; per-file counts are unreliable. Skip them.
            if (classMatch.Value.IndexOf("partial", StringComparison.Ordinal) >= 0)
                continue;
            int classStartLine = GetLineNumber(content, classMatch.Index);
            string[] lines = content.Split('\n');
            string classBody = ExtractBracedBlock(lines, classStartLine - 1);

            int methodCount = CSharpMethodSignaturePattern.Matches(classBody).Count;
            int fieldCount = CSharpFieldDeclPattern.Matches(classBody).Count;

            if (methodCount >= GodClassMethodThreshold)
            {
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1018",
                    message: $"Class '{className}' has {methodCount} methods (threshold: {GodClassMethodThreshold}). Split responsibilities into smaller classes — use create_project to create a class library.",
                    file: file,
                    line: classStartLine,
                    symbol: className,
                    helpUri: BP1018HelpUri);
                findingCount++;
            }

            if (fieldCount >= GodClassFieldThreshold)
            {
                // Static classes hold only class-level state (constants, cached patterns, etc.).
                // High field counts in static classes are typical of catalog/registry designs -- skip.
                bool isStaticClass = classMatch.Value.IndexOf("static ", StringComparison.Ordinal) >= 0;
                if (!isStaticClass)
                {
                    yield return DiagnosticRowFactory.CreateBestPracticeRow(
                        code: "BP1018",
                        message: $"Class '{className}' has {fieldCount} fields (threshold: {GodClassFieldThreshold}). Consider splitting state into smaller classes — use create_project to create a class library.",
                        file: file,
                        line: classStartLine,
                        symbol: className,
                        helpUri: BP1018HelpUri);
                    findingCount++;
                }
            }

            if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
        }
    }

    // ── BP1027: Property bag class (C#) ───────────────────────────────────────

    public static IEnumerable<JObject> FindPropertyBagClass(string file, string content)
    {
        MatchCollection classMatches = CSharpClassDeclPattern.Matches(content);
        int findingCount = 0;
        string[] lines = content.Split('\n');

        foreach (Match classMatch in classMatches)
        {
            string className = classMatch.Groups[1].Value;
            // Partial classes spread their members across multiple files; a file
            // containing only properties is normal and not a property-bag smell.
            if (classMatch.Value.IndexOf("partial", StringComparison.Ordinal) >= 0)
                continue;
            int classStartLine = GetLineNumber(content, classMatch.Index);
            string classBody = ExtractBracedBlock(lines, classStartLine - 1);
            int propertyCount = CSharpAutoPropertyPattern.Matches(classBody).Count;
            int behaviorCount = CSharpMethodSignaturePattern.Matches(classBody).Count;

            if (propertyCount < PropertyBagPropertyThreshold || behaviorCount > PropertyBagBehaviorThreshold)
            {
                continue;
            }

            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1027",
                message: $"Class '{className}' has {propertyCount} auto-properties and only {behaviorCount} behavioral methods. Move shared state behind a focused service or model instead of growing an accessor-only class.",
                file: file,
                line: classStartLine,
                symbol: className,
                helpUri: BP1027HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ── BP1019: Missing using for IDisposable (C#) ────────────────────────────

    public static IEnumerable<JObject> FindMissingUsing(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in NewDisposablePattern.Matches(content))
        {
            string line = GetLineAt(content, match.Index);
            if (line.TrimStart().StartsWith("using ", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("using(", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("await using", StringComparison.Ordinal))
            {
                continue;
            }

            string varName = match.Groups[1].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1019",
                message: $"'{varName}' is IDisposable but not wrapped in a 'using' statement. Resources may leak.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: varName,
                helpUri: BP1019HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
        }
    }

    // ── BP1020: DateTime.Now/UtcNow in loops (C#) ─────────────────────────────

    public static IEnumerable<JObject> FindDateTimeInLoop(string file, string content)
    {
        string[] lines = content.Split('\n');
        int findingCount = 0;
        bool inLoop = false;
        int loopBraceDepth = 0;
        int braceDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (Regex.IsMatch(line, @"\b(?:for|foreach|while)\s*\("))
            {
                inLoop = true;
                loopBraceDepth = braceDepth;
            }

            foreach (char ch in line)
            {
                if (ch == '{') { braceDepth++; }
                else if (ch == '}') { braceDepth--; }
            }

            if (inLoop && braceDepth <= loopBraceDepth)
            {
                inLoop = false;
            }

            if (inLoop)
            {
                Match dtMatch = DateTimeNowSimplePattern.Match(line);
                if (dtMatch.Success)
                {
                    yield return DiagnosticRowFactory.CreateBestPracticeRow(
                        code: "BP1020",
                        message: $"DateTime.{dtMatch.Groups["prop"].Value} called inside a loop. Capture it once before the loop.",
                        file: file,
                        line: i + 1,
                        symbol: $"DateTime.{dtMatch.Groups["prop"].Value}",
                        helpUri: BP1020HelpUri);
                    findingCount++;
                    if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
                }
            }
        }
    }

    // ── BP1021: Overuse of dynamic/object (C#) ────────────────────────────────

    public static IEnumerable<JObject> FindDynamicObjectOveruse(string file, string content)
    {
        MatchCollection matches = DynamicObjectParamPattern.Matches(content);
        if (matches.Count >= DynamicObjectThreshold)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1021",
                message: $"'dynamic' or 'object' used as parameter type {matches.Count} times. Use specific types or generics for type safety.",
                file: file,
                line: GetLineNumber(content, matches[0].Index),
                symbol: "dynamic/object",
                helpUri: BP1021HelpUri);
        }
    }

    // ── BP1022: Raw new without smart pointer (C++) ───────────────────────────

    public static IEnumerable<JObject> FindRawNew(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in RawNewPattern.Matches(content))
        {
            string line = GetLineAt(content, match.Index);
            if (line.Contains("make_unique") || line.Contains("make_shared") || line.Contains("reset("))
            {
                continue;
            }

            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1022",
                message: "Raw 'new' detected. Prefer std::make_unique or std::make_shared for automatic memory management.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "new",
                helpUri: BP1022HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
        }
    }

    // ── BP1023: Heavy macro usage (C++) ───────────────────────────────────────

    public static IEnumerable<JObject> FindMacroOveruse(string file, string content)
    {
        MatchCollection matches = PreprocessorDefinePattern.Matches(content);
        if (matches.Count >= MacroOveruseThreshold)
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1023",
                message: $"File has {matches.Count} #define macros (threshold: {MacroOveruseThreshold}). Prefer constexpr, inline functions, or templates.",
                file: file,
                line: GetLineNumber(content, matches[0].Index),
                symbol: "#define",
                helpUri: BP1023HelpUri);
        }
    }

    // ── BP1024: Deep inheritance (C++) ────────────────────────────────────────

    public static IEnumerable<JObject> FindDeepInheritance(string file, string content)
    {
        Regex classPattern = new(
            @"^[ \t]*(?:class|struct)\s+(\w+)\s*:\s*(.+?)(?:\{|$)",
            RegexOptions.Compiled | RegexOptions.Multiline);
        int findingCount = 0;
        foreach (Match match in classPattern.Matches(content))
        {
            string className = match.Groups[1].Value;
            string[] bases = match.Groups[2].Value.Split(',');
            if (bases.Length >= DeepNestingThreshold)
            {
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1024",
                    message: $"Class '{className}' inherits from {bases.Length} bases. Deep/wide inheritance is hard to maintain; prefer composition.",
                    file: file,
                    line: GetLineNumber(content, match.Index),
                    symbol: className,
                    helpUri: BP1024HelpUri);
                findingCount++;
                if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
            }
        }
    }

    // ── BP1025: Missing const correctness (C++) ───────────────────────────────

    public static IEnumerable<JObject> FindMissingConst(string file, string content)
    {
        Regex passByValuePattern = new(
            @"\b(?:std::(?:string|vector|map|unordered_map|set|list|deque|array)|string|vector|map)\s+(\w+)\s*[,)]",
            RegexOptions.Compiled);
        int findingCount = 0;
        foreach (Match match in passByValuePattern.Matches(content))
        {
            string paramName = match.Groups[1].Value;
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1025",
                message: $"Parameter '{paramName}' is passed by value. Use 'const &' to avoid unnecessary copies.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: paramName,
                helpUri: BP1025HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
        }
    }

    // ── BP1026: Python == True/False ──────────────────────────────────────────

    public static IEnumerable<JObject> FindBooleanComparison(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in PythonBoolComparePattern.Matches(content))
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1026",
                message: $"'{match.Value.Trim()}' is redundant. Use truthiness directly: 'if x:' not 'if x == True:', 'if not x:' not 'if x == False:'.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: match.Value.Trim(),
                helpUri: BP1026HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile) { yield break; }
        }
    }

    // ── BP1028: Unnecessary or redundant comments (C#) ───────────────────────

    public static IEnumerable<JObject> FindUnnecessaryComments(string file, string content)
    {
        MatchCollection commentMatches = CSharpCommentPattern.Matches(content);
        foreach (Match match in commentMatches)
        {
            // Strip the comment delimiter to get the raw comment text.
            string raw = match.Value.Trim();
            string commentText = raw.StartsWith("//", StringComparison.Ordinal)
                ? raw.Substring(2).Trim()
                : raw.TrimStart('/').TrimStart('*').TrimEnd('*').TrimEnd('/').Trim();

            if (string.IsNullOrWhiteSpace(commentText))
            {
                continue;
            }

            if (IsTrulyUsefulComment(commentText))
            {
                continue;
            }

            if (IsUnnecessaryComment(commentText))
            {
                yield return DiagnosticRowFactory.CreateBestPracticeRow(
                    code: "BP1028",
                    message: $"Unnecessary comment detected: \"{Truncate(commentText, 72)}\". " +
                             "This comment restates what the code already clearly expresses or adds no meaningful value. " +
                             "Remove it to reduce visual noise.",
                    file: file,
                    line: GetLineNumber(content, match.Index),
                    symbol: "Redundant comment",
                    helpUri: BP1028HelpUri);
            }
        }
    }

    // ── BP1029: Namespace/folder structure issues (C#) ────────────────────────

    public static IEnumerable<JObject> FindNamespaceFolderStructureIssues(string file, string content)
    {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file);
        if (fileNameWithoutExtension.Contains("."))
        {
            string suggestedPath = fileNameWithoutExtension.Replace('.', '\\') + ".cs";
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1029",
                message: $"File '{Path.GetFileName(file)}' uses dotted naming that hides the owning folder structure. Move it into folders such as '{suggestedPath}' instead of keeping partial groups in the filename.",
                file: file,
                line: 1,
                symbol: fileNameWithoutExtension,
                helpUri: BP1029HelpUri);
            yield break;
        }

        Match namespaceMatch = CSharpNamespacePattern.Match(content);
        if (!namespaceMatch.Success)
        {
            yield break;
        }

        string declaredNamespace = namespaceMatch.Groups["name"].Value;
        string directoryPath = Path.GetDirectoryName(file) ?? string.Empty;
        string[] relativeSegments = GetRelativeDirectorySegments(directoryPath);
        if (relativeSegments.Length == 0)
        {
            yield break;
        }

        string[] partialTypeNames = GetDeclaredPartialTypeNames(content);
        string[] structuralSegments = TrimTypeGroupSegments(relativeSegments, partialTypeNames);
        string[] namespaceSegments = declaredNamespace.Split('.');
        if (NamespaceMatchesFolderStructure(structuralSegments, namespaceSegments))
        {
            yield break;
        }

        string actualFolder = string.Join("\\", structuralSegments);
        yield return DiagnosticRowFactory.CreateBestPracticeRow(
            code: "BP1029",
            message: $"Namespace '{declaredNamespace}' does not match the folder structure '{actualFolder}'. Align namespace segments with folders, and keep extra partial organization under an owning-type folder instead of dotted filenames.",
            file: file,
            line: GetLineNumber(content, namespaceMatch.Index),
            symbol: declaredNamespace,
            helpUri: BP1029HelpUri);
    }

    // ── BP1030: Mojibake / encoding corruption ────────────────────────────────

    public static IEnumerable<JObject> FindMojibake(string file, string content)
    {
        if (Path.GetFileName(file).Equals("BestPracticeAnalyzer.cs", StringComparison.OrdinalIgnoreCase)
            && content.IndexOf("MojibakeSignatures", StringComparison.Ordinal) >= 0)
        {
            yield break;
        }

        if (!MojibakeSignatures.Any(sig => content.IndexOf(sig, StringComparison.Ordinal) >= 0))
        {
            yield break;
        }

        int lineNumber = 1;
        string[] lines = content.Split(['\r', '\n'], StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            if (MojibakeSignatures.Any(sig => lines[i].IndexOf(sig, StringComparison.Ordinal) >= 0))
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

    // PowerShell rules ---------------------------------------------------------

    public static IEnumerable<JObject> FindWriteHostUsage(string file, string content)
    {
        MatchCollection matches = Regex.Matches(content, @"(?im)^\s*Write-Host\b");
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
        if (Regex.IsMatch(content, @"(?im)^\s*Set-StrictMode\s+-Version\s+Latest\b"))
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
        if (VbOptionStrictOnPattern.IsMatch(content))
        {
            yield break;
        }

        string message = VbOptionStrictOffPattern.IsMatch(content)
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

    public static IEnumerable<JObject> FindVbMultipleStatementsPerLine(string file, string content)
    {
        string[] lines = content.Split('\n');
        int findingCount = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int commentIndex = line.IndexOf('\'');
            string code = commentIndex >= 0 ? line.Substring(0, commentIndex) : line;
            if (code.IndexOf(':') < 0)
            {
                continue;
            }

            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1037",
                message: "Visual Basic line contains multiple statements separated by ':'. Prefer one statement per line for readability.",
                file: file,
                line: i + 1,
                symbol: ":",
                helpUri: BP1037HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    public static IEnumerable<JObject> FindVbExplicitLineContinuation(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in VbLineContinuationPattern.Matches(content))
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: "BP1038",
                message: "Visual Basic file uses explicit line continuation '_'. Prefer implicit line continuation where the language already supports it.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "_",
                helpUri: BP1038HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    public static IEnumerable<JObject> FindFSharpMutableState(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in FSharpMutablePattern.Matches(content))
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
        foreach (Match match in FSharpBlockCommentPattern.Matches(content))
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
        foreach (Match match in PythonNoneComparisonPattern.Matches(content))
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

    public static IEnumerable<JObject> FindPowerShellAliases(string file, string content)
    {
        int findingCount = 0;
        foreach (Match match in PowerShellAliasPattern.Matches(content))
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

    // ── Block structure helpers ───────────────────────────────────────────────

    private static int CountBracedBlockLines(string[] lines, int startIndex)
    {
        int depth = 0;
        bool foundOpen = false;
        for (int i = startIndex; i < lines.Length; i++)
        {
            (depth, foundOpen) = ScanLineForBraces(lines[i], depth, foundOpen);
            if (foundOpen && depth <= 0)
            {
                return i - startIndex + 1;
            }
        }
        return lines.Length - startIndex;
    }

    private static int CountPythonFunctionLines(string[] lines, int startIndex)
    {
        if (startIndex >= lines.Length)
        {
            return 0;
        }

        string defLine = lines[startIndex];
        int baseIndent = defLine.Length - defLine.TrimStart().Length;
        int count = 1;
        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                count++;
                continue;
            }
            int indent = line.Length - line.TrimStart().Length;
            if (indent <= baseIndent)
            {
                break;
            }
            count++;
        }
        return count;
    }

    private static string ExtractBracedBlock(string[] lines, int startIndex)
    {
        int depth = 0;
        bool foundOpen = false;
        List<string> blockLines = [];
        for (int i = startIndex; i < lines.Length; i++)
        {
            blockLines.Add(lines[i]);
            (depth, foundOpen) = ScanLineForBraces(lines[i], depth, foundOpen);
            if (foundOpen && depth <= 0)
            {
                break;
            }
        }
        return string.Join("\n", blockLines);
    }

    // ── Namespace/folder helpers ──────────────────────────────────────────────

    private static (int Depth, bool FoundOpen) ScanLineForBraces(string line, int depth, bool foundOpen)
    {
        foreach (char ch in line)
        {
            if (ch == '{') { depth++; foundOpen = true; }
            else if (ch == '}') { depth--; }
        }
        return (depth, foundOpen);
    }

    private static string[] GetRelativeDirectorySegments(string directoryPath)
    {
        string[] pathSegments = directoryPath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        int srcIndex = Array.FindLastIndex(pathSegments, static segment =>
            string.Equals(segment, "src", StringComparison.OrdinalIgnoreCase));
        if (srcIndex < 0 || srcIndex >= pathSegments.Length - 1)
        {
            return [];
        }

        int relativeLength = pathSegments.Length - srcIndex - 1;
        string[] relativeSegments = new string[relativeLength];
        Array.Copy(pathSegments, srcIndex + 1, relativeSegments, 0, relativeLength);
        return relativeSegments;
    }

    private static string[] GetDeclaredPartialTypeNames(string content)
    {
        List<string> partialTypeNames = [];
        foreach (Match match in PartialTypeDeclarationPattern.Matches(content))
        {
            string typeName = match.Groups["name"].Value.TrimStart('@');
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                partialTypeNames.Add(typeName);
            }
        }
        return [.. partialTypeNames.Distinct(StringComparer.Ordinal)];
    }

    private static string[] TrimTypeGroupSegments(string[] directorySegments, string[] partialTypeNames)
    {
        if (partialTypeNames.Length == 0)
        {
            return directorySegments;
        }

        for (int index = directorySegments.Length - 1; index >= 0; index--)
        {
            if (partialTypeNames.Contains(directorySegments[index], StringComparer.Ordinal))
            {
                string[] trimmedSegments = new string[index];
                Array.Copy(directorySegments, 0, trimmedSegments, 0, index);
                return trimmedSegments;
            }
        }

        return directorySegments;
    }

    private static bool NamespaceMatchesFolderStructure(string[] directorySegments, string[] namespaceSegments)
    {
        if (directorySegments.Length == 0)
        {
            return true;
        }

        // Directory segments may contain dots (e.g. "VsIdeBridge.Discovery" as a project-root folder).
        // Expand them so they compare correctly against namespace segments split by '.'.
        string[] expandedDir = directorySegments
            .SelectMany(static seg => seg.Split('.'))
            .ToArray();

        if (expandedDir.Length > namespaceSegments.Length)
        {
            return false;
        }

        int namespaceOffset = namespaceSegments.Length - expandedDir.Length;
        for (int index = 0; index < expandedDir.Length; index++)
        {
            if (!string.Equals(expandedDir[index], namespaceSegments[index + namespaceOffset], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // ── String literal guard ──────────────────────────────────────────────────

    private static bool IsInsideStringLiteral(string content, int index)
    {
        int lineStart = content.LastIndexOf('\n', index > 0 ? index - 1 : 0) + 1;
        int quoteCount = 0;
        for (int i = lineStart; i < index; i++)
        {
            if (content[i] == '"' && (i == lineStart || content[i - 1] != '\\'))
                quoteCount++;
        }
        return quoteCount % 2 == 1;
    }

    private static bool IsInsideLineComment(string content, int index)
    {
        int lineStart = content.LastIndexOf('\n', index > 0 ? index - 1 : 0) + 1;
        bool inString = false;
        for (int i = lineStart; i < index - 1; i++)
        {
            if (content[i] == '"' && (i == lineStart || content[i - 1] != '\\'))
                inString = !inString;
            if (!inString && content[i] == '/' && content[i + 1] == '/')
                return true;
        }
        return false;
    }

    // ── Comment quality helpers ───────────────────────────────────────────────

    private static bool IsTrulyUsefulComment(string comment)
    {
        string lower = comment.ToLowerInvariant();
        // XML doc comments are always intentional documentation.
        if (comment.TrimStart().StartsWith("<", StringComparison.Ordinal))
            return true;
        // Section-header style comments (e.g. "First pass:", "Step 2:") are structural markers.
        if (System.Text.RegularExpressions.Regex.IsMatch(comment.Trim(),
            @"^(First|Second|Third|Fourth|Pass \d+|Step \d+|Phase \d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        return comment.StartsWith("TODO", StringComparison.OrdinalIgnoreCase) ||
               comment.StartsWith("FIXME", StringComparison.OrdinalIgnoreCase) ||
               comment.StartsWith("HACK", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("why ") || lower.Contains("because") ||
               lower.Contains("reason:") || lower.Contains("note:") ||
               lower.Contains("intentional") || lower.Contains("non-obvious") ||
               lower.Contains("handles") || lower.Contains("avoids") ||
               comment.Length <= 15;
    }

    private static bool IsUnnecessaryComment(string comment)
    {
        string lower = comment.ToLowerInvariant().TrimEnd('.', '!', '?', ':');
        string[] redundantPhrases =
        [
            "this method", "this function", "this class", "this variable",
            "this line", "increments", "decrements", "sets the", "gets the",
            "initializes the", "checks if",
            "loops through", "iterates over", "adds one to", "subtracts one",
            "as an ai", "large language model",
            "this ensures that", "this approach ensures", "it is recommended",
            "best practice", "following best practices", "in summary",
            "please note that", "note that this", "important to note",
            "the above code", "the following code", "this code does the following",
            "the purpose of this", "simply", "basically",
        ];
        return redundantPhrases.Any(phrase => lower.Contains(phrase));
    }

    // ── Line and position helpers ─────────────────────────────────────────────

    private static int GetLineNumber(string content, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n') { line++; }
        }
        return line;
    }

    private static string GetLineAt(string content, int index)
    {
        int start = index > 0 ? content.LastIndexOf('\n', index - 1) + 1 : 0;
        int end = content.IndexOf('\n', index);
        return end < 0 ? content.Substring(start) : content.Substring(start, end - start);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) { return text; }
        return text.Substring(0, maxLength - 3) + "...";
    }
}

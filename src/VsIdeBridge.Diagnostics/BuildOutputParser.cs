using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Diagnostics;

internal static class BuildOutputParser
{
    private static readonly char[] s_newlineChars = ['\r', '\n'];

    public static IReadOnlyList<JObject> ParseBuildOutput(string buildOutputText)
    {
        List<JObject> diagnostics = new();

        string[] lines = buildOutputText.Split(s_newlineChars, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            JObject? diagnostic = TryParseMsBuildDiagnostic(line) ?? TryParseStructuredOutput(line);
            if (diagnostic != null)
            {
                diagnostics.Add(diagnostic);
            }
        }

        return diagnostics;
    }

    private static JObject? TryParseMsBuildDiagnostic(string line)
    {
        Match match = ErrorListPatterns.MsBuildDiagnosticPattern().Match(line);
        if (!match.Success) return null;

        string file = match.Groups["file"].Value;
        int lineNum = int.TryParse(match.Groups["line"].Value, out int ln) ? ln : 1;
        int column = int.TryParse(match.Groups["column"].Value, out int col) ? col : 1;
        string severity = match.Groups["severity"].Value;
        string code = match.Groups["code"].Value;
        string message = match.Groups["message"].Value;
        string project = match.Groups["project"].Value;

        return new JObject
        {
            [ErrorListConstants.SeverityKey] = severity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.ProjectKey] = project,
            [ErrorListConstants.FileKey] = file,
            [ErrorListConstants.LineKey] = lineNum,
            [ErrorListConstants.ColumnKey] = column,
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.ToolKey] = "MSBuild",
            [ErrorListConstants.SourceKey] = "Build",
        };
    }

    private static JObject? TryParseStructuredOutput(string line)
    {
        Match match = ErrorListPatterns.StructuredOutputPattern().Match(line);
        if (!match.Success) return null;

        string project = match.Groups["project"].Value;
        string file = match.Groups["file"].Value;
        int lineNum = int.TryParse(match.Groups["line"].Value, out int ln) ? ln : 1;
        int column = int.TryParse(match.Groups["column"].Value, out int col) ? col : 1;
        string severity = match.Groups["severity"].Value;
        string code = match.Groups["code"].Value;
        string message = match.Groups["message"].Value;

        return new JObject
        {
            [ErrorListConstants.SeverityKey] = severity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.ProjectKey] = project,
            [ErrorListConstants.FileKey] = file,
            [ErrorListConstants.LineKey] = lineNum,
            [ErrorListConstants.ColumnKey] = column,
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.ToolKey] = "MSBuild",
            [ErrorListConstants.SourceKey] = "Build",
        };
    }
}

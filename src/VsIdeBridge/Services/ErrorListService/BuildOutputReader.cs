using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using VsIdeBridge.Infrastructure;
using static VsIdeBridge.Diagnostics.ErrorListConstants;
using static VsIdeBridge.Diagnostics.ErrorListPatterns;

namespace VsIdeBridge.Services;

internal sealed partial class ErrorListService
{
    private static IReadOnlyList<JObject> ReadBuildOutputRows(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        OutputWindowPane? pane = TryGetBuildOutputPane(dte);
        if (pane is null)
        {
            return [];
        }

        string text = TryReadBuildOutputText(dte, pane);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<JObject> rows = new List<JObject>();
        using StringReader reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParseBuildOutputLine(line, out JObject row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static string TryReadBuildOutputText(DTE2 dte, OutputWindowPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        ActivateBuildOutputPane(dte, pane);

        for (int attempt = 0; attempt < BuildOutputReadAttemptCount; attempt++)
        {
            try
            {
                return pane.TextDocument is TextDocument textDocument
                    ? ReadTextDocument(textDocument)
                    : string.Empty;
            }
            catch (COMException)
            {
                if (attempt == 0)
                {
                    ActivateBuildOutputPane(dte, pane);
                    continue;
                }

                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static void EnsureErrorListWindow(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryGetErrorListWindow(dte)?.Object is ErrorList)
        {
            return;
        }

        try
        {
            dte.ExecuteCommand("View.ErrorList", string.Empty);
        }
        catch (COMException ex)
        {
            LogNonCriticalException(ex);
        }
    }

    private static void ActivateBuildOutputPane(DTE2 dte, OutputWindowPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            dte.ExecuteCommand("View.Output", string.Empty);
        }
        catch (COMException ex)
        {
            LogNonCriticalException(ex);
        }

        try
        {
            pane.Activate();
        }
        catch (COMException ex)
        {
            LogNonCriticalException(ex);
        }
    }

    private static OutputWindowPane? TryGetBuildOutputPane(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
        {
            string paneName = pane.Name;
            foreach (string candidateName in BuildOutputPaneNames)
            {
                if (string.Equals(paneName, candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    return pane;
                }
            }
        }

        return null;
    }

    private static string ReadTextDocument(TextDocument textDocument)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        EditPoint start = textDocument.StartPoint.CreateEditPoint();
        return start.GetText(textDocument.EndPoint);
    }

    private static bool TryParseBuildOutputLine(string line, out JObject row)
    {
        row = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        Match match = StructuredOutputPattern.Match(line);
        if (!match.Success)
        {
            match = MsBuildDiagnosticPattern.Match(line);
        }

        if (!match.Success)
        {
            return false;
        }

        string severity = NormalizeParsedSeverity(match.Groups["severity"].Value);
        string description = match.Groups["message"].Value.Trim();
        string project = match.Groups["project"].Value.Trim();
        string file = NormalizeFilePath(match.Groups["file"].Value.Trim());
        int lineNumber = ParseOptionalInt(match.Groups["line"].Value);
        int columnNumber = ParseOptionalInt(match.Groups["column"].Value);
        NormalizeBuildOutputLocation(ref file, ref lineNumber, ref columnNumber);
        string code = NormalizeCode(match.Groups["code"].Value, description);

        row = new JObject
        {
            [SeverityKey] = severity,
            ["code"] = code,
            ["codeFamily"] = InferCodeFamily(code),
            ["tool"] = InferTool(code, description),
            ["message"] = description,
            ["project"] = project,
            ["file"] = file,
            ["line"] = lineNumber,
            ["column"] = columnNumber,
            ["symbols"] = new JArray(ExtractSymbols(description)),
            ["source"] = "build-output",
        };
        return true;
    }

    private static string NormalizeParsedSeverity(string severity)
    {
        return severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning";
    }

    private static string NormalizeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return PathNormalization.NormalizeFilePath(value);
        }
        catch (ArgumentException)
        {
            // Build output can include non-path tokens in the file column; keep the raw value
            // so diagnostics still flow without failing the entire error-list request.
            return value;
        }
        catch (NotSupportedException)
        {
            // Build output can include non-path tokens in the file column; keep the raw value
            // so diagnostics still flow without failing the entire error-list request.
            return value;
        }
        catch (PathTooLongException)
        {
            // Build output can include non-path tokens in the file column; keep the raw value
            // so diagnostics still flow without failing the entire error-list request.
            return value;
        }
    }

    private static void NormalizeBuildOutputLocation(ref string file, ref int lineNumber, ref int columnNumber)
    {
        if (lineNumber > 0 && columnNumber > 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(file) || !file.EndsWith(")", StringComparison.Ordinal))
        {
            return;
        }

        int openParenIndex = file.LastIndexOf('(');
        if (openParenIndex <= 0 || openParenIndex >= file.Length - 1)
        {
            return;
        }

        string[] coordinates = file.Substring(openParenIndex + 1, file.Length - openParenIndex - CoordinateSuffixTrimLength).Split(',');
        if (coordinates.Length < 1 || coordinates.Length > MaximumBuildOutputCoordinateCount)
        {
            return;
        }

        if (!int.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLine))
        {
            return;
        }

        int parsedColumn = 1;
        if (coordinates.Length > 1 && !int.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedColumn))
        {
            return;
        }

        string normalizedFile = NormalizeFilePath(file.Substring(0, openParenIndex));
        if (string.IsNullOrWhiteSpace(normalizedFile))
        {
            return;
        }

        file = normalizedFile;
        lineNumber = parsedLine;
        columnNumber = parsedColumn;
    }

    private static int ParseOptionalInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
    }

    private static string NormalizeCode(string explicitCode, string description)
    {
        return !string.IsNullOrWhiteSpace(explicitCode)
            ? explicitCode
            : InferCode(description);
    }

    private static string ExtractExplicitCode(string description)
    {
        Match match = ExplicitCodePattern.Match(description);
        return match.Success ? NormalizeCode(match.Value) : string.Empty;
    }

    private static string NormalizeCode(string code)
    {
        if (code.StartsWith("LINK", StringComparison.OrdinalIgnoreCase) &&
            code.Length > LinkerCodePrefixLength &&
            int.TryParse(code.Substring(LinkerCodePrefixLength), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return "LNK" + code.Substring(LinkerCodePrefixLength);
        }

        if (code.StartsWith("lnt-", StringComparison.OrdinalIgnoreCase))
        {
            return code.ToLowerInvariant();
        }

        return code.ToUpperInvariant();
    }
}

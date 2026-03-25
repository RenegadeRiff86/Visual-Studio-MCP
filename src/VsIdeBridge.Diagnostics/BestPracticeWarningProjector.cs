using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Diagnostics;

internal static class BestPracticeWarningProjector
{
    public static JArray CreateResponseWarnings(IReadOnlyList<JObject> rows)
    {
        return CreateResponseWarnings(rows, null);
    }

    public static JArray CreateResponseWarnings(IReadOnlyList<JObject> rows, string? projectUniqueName)
    {
        JArray warnings = new JArray();

        foreach (JObject row in rows)
        {
            warnings.Add(CreateResponseWarning(row, projectUniqueName));
        }

        return warnings;
    }

    private static JObject CreateResponseWarning(JObject row, string? projectUniqueName)
    {
        string code = GetString(row, ErrorListConstants.CodeKey);
        string message = GetString(row, ErrorListConstants.MessageKey);
        string symbol = GetPrimarySymbol(row);
        string helpUri = ResolveHelpUri(code, GetString(row, ErrorListConstants.HelpUriKey));
        string authority = ResolveAuthority(code, row, helpUri);
        string project = string.IsNullOrWhiteSpace(projectUniqueName)
            ? GetString(row, ErrorListConstants.ProjectKey)
            : projectUniqueName ?? string.Empty;

        JObject warning = new JObject
        {
            [ErrorListConstants.SeverityKey] = row[ErrorListConstants.SeverityKey] ?? ErrorListConstants.WarningSeverity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.LineKey] = row[ErrorListConstants.LineKey],
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.GuidanceKey] = CreateGuidance(code, message),
            [ErrorListConstants.SuggestedActionKey] = GetSuggestedAction(code),
            [ErrorListConstants.SourceKey] = ErrorListConstants.BestPracticeCategory,
            [ErrorListConstants.AuthorityKey] = authority,
        };

        if (!string.IsNullOrWhiteSpace(project))
        {
            warning[ErrorListConstants.ProjectKey] = project;
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            warning[ErrorListConstants.SymbolKey] = symbol;
        }

        if (!string.IsNullOrWhiteSpace(helpUri))
        {
            warning[ErrorListConstants.HelpUriKey] = helpUri;
        }

        return warning;
    }

    private static string GetPrimarySymbol(JObject row)
    {
        JToken? symbolsToken = row[ErrorListConstants.SymbolsKey];
        if (symbolsToken is JArray symbols && symbols.Count > 0)
        {
            return symbols[0]?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string GetString(JObject row, string key)
    {
        return row[key]?.ToString() ?? string.Empty;
    }

    private static string CreateGuidance(string code, string message)
    {
        string suggestedAction = GetSuggestedAction(code);
        if (string.IsNullOrWhiteSpace(suggestedAction))
        {
            return message;
        }

        return string.Concat(message, " Next step: ", suggestedAction);
    }

    private static string GetSuggestedAction(string code)
    {
        return code switch
        {
            "BP1001" => "Extract the repeated string into a named constant or shared readonly value.",
            "BP1002" => "Use a named constant for domain values, or add a short comment when the number is only local arithmetic or indexing math.",
            "BP1003" => "Use integer division or an explicit rounding helper so the intent is obvious.",
            "BP1004" => "Handle the exception explicitly by logging it, translating it, or rethrowing it.",
            "BP1005" => "Return Task instead of async void unless this is a real event handler.",
            "BP1006" => "Replace manual new/delete ownership with RAII or a smart pointer.",
            "BP1007" => "Remove the global using namespace from the header and qualify names instead.",
            "BP1008" => "Replace the C-style cast with a named cast that shows the conversion intent.",
            "BP1009" => "Log the exception details with the logger so failures stay observable.",
            "BP1010" => "Add a docstring and an explicit return contract so the function is easier to use.",
            "BP1011" => "Move imports to the top of the file and group them consistently.",
            "BP1012" => "Split the file into smaller focused types or helpers before adding more code.",
            "BP1013" => "Extract smaller methods so each block has one clear job.",
            "BP1014" => "Rename the symbol so its purpose is obvious without reading surrounding code.",
            "BP1015" => "Flatten the control flow with guard clauses, extracted helpers, or simpler branching.",
            "BP1016" => "Delete the dead code and rely on version control instead of commented-out blocks.",
            "BP1017" => "Normalize indentation to one style and reformat the file.",
            "BP1018" => "Split responsibilities into smaller classes before extending this type further.",
            "BP1019" => "Wrap the disposable resource in using or await using so cleanup is guaranteed.",
            "BP1020" => "Capture the time value once before the loop and reuse it inside the loop body.",
            "BP1021" => "Replace dynamic or object parameters with specific types or generics.",
            "BP1022" => "Use std::make_unique or std::make_shared instead of raw new.",
            "BP1023" => "Replace macros with constexpr values, inline functions, or templates.",
            "BP1024" => "Prefer composition over deep or wide inheritance.",
            "BP1025" => "Pass large values by const reference when you do not need a copy.",
            "BP1026" => "Use truthiness directly instead of comparing to True or False.",
            "BP1027" => "Move the shared state behind a focused service or model instead of growing an accessor-only class.",
            "BP1028" => "Delete comments that only restate obvious code and keep the ones that add real intent.",
            "BP1029" => "Align folders with namespaces and use owning-type folders instead of dotted partial filenames.",
            _ => "Fix the pattern before repeating it in more edits.",
        };
    }

    private static string ResolveHelpUri(string code, string existingHelpUri)
    {
        if (!string.IsNullOrWhiteSpace(existingHelpUri))
        {
            return existingHelpUri;
        }

        return BestPracticeRuleCatalog.GetHelpUri(code);
    }

    private static string ResolveAuthority(string code, JObject row, string helpUri)
    {
        string authority = GetString(row, ErrorListConstants.AuthorityKey);
        if (!string.IsNullOrWhiteSpace(authority))
        {
            return authority;
        }

        if (!string.IsNullOrWhiteSpace(helpUri))
        {
            return BestPracticeRuleCatalog.GetAuthority(code);
        }

        return ErrorListConstants.ProjectLocalAuthority;
    }
}

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
        string guidance = ResolveGuidance(code, row, message);
        string suggestedAction = ResolveSuggestedAction(code, row);
        string llmFixPrompt = ResolveLlmFixPrompt(code, row, guidance, suggestedAction);
        string project = string.IsNullOrWhiteSpace(projectUniqueName)
            ? GetString(row, ErrorListConstants.ProjectKey)
            : projectUniqueName ?? string.Empty;

        JObject warning = new JObject
        {
            [ErrorListConstants.SeverityKey] = row[ErrorListConstants.SeverityKey] ?? ErrorListConstants.WarningSeverity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.LineKey] = row[ErrorListConstants.LineKey],
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.GuidanceKey] = guidance,
            [ErrorListConstants.SuggestedActionKey] = suggestedAction,
            [ErrorListConstants.LlmFixPromptKey] = llmFixPrompt,
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

    private static string ResolveGuidance(string code, JObject row, string message)
    {
        string guidance = GetString(row, ErrorListConstants.GuidanceKey);
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            return guidance;
        }

        guidance = BestPracticeRuleCatalog.GetGuidance(code);
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            return guidance;
        }

        string suggestedAction = ResolveSuggestedAction(code, row);
        if (string.IsNullOrWhiteSpace(suggestedAction))
        {
            return message;
        }

        return string.Concat(message, " Next step: ", suggestedAction);
    }

    private static string ResolveSuggestedAction(string code, JObject row)
    {
        string suggestedAction = GetString(row, ErrorListConstants.SuggestedActionKey);
        if (!string.IsNullOrWhiteSpace(suggestedAction))
        {
            return suggestedAction;
        }

        suggestedAction = BestPracticeRuleCatalog.GetSuggestedAction(code);
        return string.IsNullOrWhiteSpace(suggestedAction)
            ? "Fix the pattern before repeating it in more edits."
            : suggestedAction;
    }

    private static string ResolveLlmFixPrompt(string code, JObject row, string guidance, string suggestedAction)
    {
        string llmFixPrompt = GetString(row, ErrorListConstants.LlmFixPromptKey);
        if (!string.IsNullOrWhiteSpace(llmFixPrompt))
        {
            return llmFixPrompt;
        }

        llmFixPrompt = BestPracticeRuleCatalog.GetLlmFixPrompt(code);
        if (!string.IsNullOrWhiteSpace(llmFixPrompt))
        {
            return llmFixPrompt;
        }

        return string.Concat(
            "Fix this best-practice issue without changing behavior. Context: ",
            guidance,
            " Preferred repair: ",
            suggestedAction);
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

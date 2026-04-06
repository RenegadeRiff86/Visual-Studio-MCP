using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Diagnostics;

internal static class DiagnosticRowFactory
{
    public static JObject CreateBestPracticeRow(string code, string message, string file, int line, string symbol, string helpUri = "")
    {
        string resolvedHelpUri = string.IsNullOrWhiteSpace(helpUri)
            ? BestPracticeRuleCatalog.GetHelpUri(code)
            : helpUri;
        string guidance = BestPracticeRuleCatalog.GetGuidance(code);
        string suggestedAction = BestPracticeRuleCatalog.GetSuggestedAction(code);
        string llmFixPrompt = BestPracticeRuleCatalog.GetLlmFixPrompt(code);

        JObject row = new()
        {
            [ErrorListConstants.SeverityKey] = ErrorListConstants.WarningSeverity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.ToolKey] = ErrorListConstants.BestPracticeCategory,
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.ProjectKey] = string.Empty,
            [ErrorListConstants.FileKey] = file,
            [ErrorListConstants.LineKey] = line,
        };

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            row[ErrorListConstants.SymbolKey] = symbol;
            row[ErrorListConstants.SymbolsKey] = new JArray(symbol);
        }

        if (!string.IsNullOrWhiteSpace(resolvedHelpUri))
        {
            row[ErrorListConstants.HelpUriKey] = resolvedHelpUri;
            row[ErrorListConstants.AuthorityKey] = BestPracticeRuleCatalog.GetAuthority(code);
        }

        if (!string.IsNullOrWhiteSpace(guidance))
        {
            row[ErrorListConstants.GuidanceKey] = guidance;
        }

        if (!string.IsNullOrWhiteSpace(suggestedAction))
        {
            row[ErrorListConstants.SuggestedActionKey] = suggestedAction;
        }

        if (!string.IsNullOrWhiteSpace(llmFixPrompt))
        {
            row[ErrorListConstants.LlmFixPromptKey] = llmFixPrompt;
        }

        return row;
    }
}

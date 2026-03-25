using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Diagnostics;

internal static class DiagnosticRowFactory
{
    public static JObject CreateBestPracticeRow(string code, string message, string file, int line, string symbol, string helpUri = "")
    {
        return new JObject
        {
            [ErrorListConstants.SeverityKey] = ErrorListConstants.WarningSeverity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.ToolKey] = ErrorListConstants.BestPracticeCategory,
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.ProjectKey] = string.Empty,
            [ErrorListConstants.FileKey] = file,
            [ErrorListConstants.LineKey] = line,
        };
    }
}

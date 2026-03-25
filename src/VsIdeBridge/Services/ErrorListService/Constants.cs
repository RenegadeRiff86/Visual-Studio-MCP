using Microsoft.VisualStudio.Shell.TableManager;

namespace VsIdeBridge.Services;

internal sealed partial class ErrorListService
{
    private static readonly string[] BestPracticeTableColumns =
    [
        StandardTableKeyNames.ErrorSeverity,
        StandardTableKeyNames.ErrorCode,
        StandardTableKeyNames.ErrorCodeToolTip,
        StandardTableKeyNames.Text,
        StandardTableKeyNames.DocumentName,
        StandardTableKeyNames.Path,
        StandardTableKeyNames.Line,
        StandardTableKeyNames.Column,
        StandardTableKeyNames.ProjectName,
        StandardTableKeyNames.BuildTool,
        StandardTableKeyNames.ErrorSource,
        StandardTableKeyNames.HelpKeyword,
        StandardTableKeyNames.HelpLink,
        StandardTableKeyNames.FullText,
    ];
}

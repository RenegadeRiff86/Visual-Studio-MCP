// Analysis methods have been extracted to VsIdeBridge.Diagnostics.BestPracticeAnalyzer.
// ErrorListService.cs now calls BestPracticeAnalyzer.AnalyzeFile(file, content) directly.

namespace VsIdeBridge.Services;

internal sealed partial class ErrorListService
{
}

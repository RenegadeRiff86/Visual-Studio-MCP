using System.IO;
using System.Text.Json.Nodes;

namespace VsIdeBridge.ServiceDomain;

/// <summary>
/// A single parsed build diagnostic (error or warning) from MSBuild or dotnet CLI output.
/// </summary>
public readonly record struct BuildDiagnostic(
    string FilePath,
    int LineNumber,
    int ColumnNumber,
    string Code,
    string Message,
    string Project,
    string ProjectPath)
{
    /// <summary>Just the file name component of FilePath, or empty string if no path.</summary>
    public string FileName => string.IsNullOrWhiteSpace(FilePath)
        ? string.Empty
        : Path.GetFileName(FilePath);

    /// <summary>Serializes the diagnostic to a JSON object for tool output.</summary>
    public JsonObject ToJson() => new()
    {
        ["file"] = FileName,
        ["path"] = FilePath,
        ["line"] = LineNumber,
        ["column"] = ColumnNumber,
        ["code"] = Code,
        ["message"] = Message,
        ["project"] = Project,
        ["projectPath"] = ProjectPath,
    };
}

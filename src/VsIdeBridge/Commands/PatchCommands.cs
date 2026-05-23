using Microsoft.VisualStudio.Shell;
using System.Text;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class PatchCommands
{
    private const string InvalidArguments = "invalid_arguments";

    internal sealed class IdeApplyEditorPatchCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0221)
    {
        protected override string CanonicalName => "Tools.IdeApplyUnifiedDiff";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string? patchText = null;
            string? patchTextBase64 = args.GetString("patch-text-base64");
            if (!string.IsNullOrWhiteSpace(patchTextBase64))
            {
                patchText = DecodePatchTextBase64(patchTextBase64!);
                patchText = context.Handles.RewritePatch(patchText);
                // Note: ValidatePatchText intentionally omitted. FromPatchText fires
                // LooksLikeSingleFileSimpleReplacementPatch which rejects the context-free
                // patches produced by FromSimpleReplace. The MCP layer already validates;
                // PatchService.ApplyEditorPatchAsync re-parses for structural correctness.
            }

            string? patchFile = args.GetString("patch-file");
            string? baseDirectory = args.GetString("base-directory");
            bool openChangedFiles = args.GetBoolean("open-changed-files", defaultValue: true);

            Newtonsoft.Json.Linq.JObject commandData = await context.Runtime.PatchService.ApplyEditorPatchAsync(
                context.Dte,
                context.Runtime.DocumentService,
                patchFile,
                patchText,
                baseDirectory,
                openChangedFiles,
                args.GetBoolean("save-changed-files", false),
                args.GetBoolean("best-practice-warnings", false),
                args.GetBoolean("replace-all", false)).ConfigureAwait(true);

            commandData["diagnosticsRefreshQueued"] = true;

            return new CommandExecutionResult($"Applied editor patch to {commandData["count"]} file(s).", commandData);
        }
    }

    private static string DecodePatchTextBase64(string patchTextBase64)
    {
        try
        {
            return Encoding.UTF8.GetString(System.Convert.FromBase64String(patchTextBase64));
        }
        catch (System.FormatException ex)
        {
            throw new CommandErrorException(
                InvalidArguments,
                "The patch-text-base64 value is not valid base64. Base64-encode the patch text as UTF-8 before passing it — e.g., Convert.ToBase64String(Encoding.UTF8.GetBytes(patchText)).",
                new { exception = ex.Message });
        }
    }

    internal sealed class IdeWriteFileCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x024D)
    {
        protected override string CanonicalName => "Tools.IdeWriteFile";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
            => ExecuteWriteFileAsync(context, args);
    }

    internal static async Task<CommandExecutionResult> ExecuteWriteFileAsync(IdeCommandContext context, CommandArguments args)
    {
        string file = args.GetString("file")
            ?? throw new CommandErrorException(
                InvalidArguments,
                "Missing required file parameter. " +
                "Call tool_help with write_file to see all required parameters and an example call.");

        string contentBase64 = args.GetString("content-base64")
            ?? throw new CommandErrorException(
                InvalidArguments,
                "Missing required content-base64 parameter. " +
                "Pass the full file content as a base64-encoded UTF-8 string. " +
                "Call tool_help with write_file to see all required parameters and an example.");

        string content;
        try
        {
            content = Encoding.UTF8.GetString(System.Convert.FromBase64String(contentBase64));
        }
        catch (System.FormatException ex)
        {
            throw new CommandErrorException(
                InvalidArguments,
                "The content-base64 value is not valid base64. " +
                "Base64-encode the full file content as UTF-8 before passing it — " +
                "e.g., Convert.ToBase64String(Encoding.UTF8.GetBytes(content)).",
                new { exception = ex.Message });
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string resolvedPath = context.Runtime.PatchService.ResolveFilePath(context.Dte, file);

        // Guard: refuse to overwrite a large file with a suspiciously small snippet.
        // This catches the common mistake of passing only a code block instead of the full
        // file — write_file replaces the entire file, so a partial write destroys content.
        if (!args.GetBoolean("allow-truncation", false) && System.IO.File.Exists(resolvedPath))
        {
            int existingLineCount = System.IO.File.ReadAllLines(resolvedPath).Length;
            int newLineCount = content.Split('\n').Length;
            const int TruncationGuardMinLines = 50;
            if (existingLineCount >= TruncationGuardMinLines && newLineCount < existingLineCount / 2)
            {
                throw new CommandErrorException(
                    InvalidArguments,
                    $"Refusing to write {newLineCount} lines to {System.IO.Path.GetFileName(resolvedPath)}, " +
                    $"which currently has {existingLineCount} lines. " +
                    "write_file replaces the ENTIRE file — this looks like a partial snippet, not a complete replacement. " +
                    "To change only part of the file, use apply_diff instead. " +
                    "To replace the entire file with shorter content, pass allow_truncation: true.",
                    new { existingLineCount, newLineCount });
            }
        }

        Newtonsoft.Json.Linq.JObject writeResult = await context.Runtime.DocumentService.WriteDocumentTextAsync(
            context.Dte,
            resolvedPath,
            content,
            line: 1,
            column: 1,
            saveChanges: true,
            includeBestPracticeWarnings: args.GetBoolean("best-practice-warnings", false)).ConfigureAwait(true);

        Newtonsoft.Json.Linq.JObject commandData = new()
        {
            ["path"] = resolvedPath,
            ["byteCount"] = Encoding.UTF8.GetByteCount(content),
            ["lineCount"] = content.Split('\n').Length,
            ["editorBacked"] = writeResult["editorBacked"] ?? false,
            ["saved"] = writeResult["saved"] ?? true,
        };

        commandData["diagnosticsRefreshQueued"] = true;

        return new CommandExecutionResult($"Wrote {commandData["lineCount"]} lines to {System.IO.Path.GetFileName(resolvedPath)}.", commandData);
    }
}

using System;
using System.Collections.Generic;
#if !NETFRAMEWORK
using System.Diagnostics.CodeAnalysis;
#endif
using System.Linq;

namespace VsIdeBridge.Shared;

public sealed class BridgeCommandMetadata(string canonicalName, string pipeName, string description, string example)
{
    public string CanonicalName { get; } = canonicalName;

    public string PipeName { get; } = pipeName;

    public string Description { get; } = description;

    public string Example { get; } = example;
}

public static partial class BridgeCommandCatalog
{
    private static readonly BridgeCommandMetadata[] Commands = Build();
    private static readonly Dictionary<string, BridgeCommandMetadata> ByPipeName = Commands
        .ToDictionary(item => item.PipeName, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BridgeCommandMetadata> ByCanonicalName = Commands
        .ToDictionary(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase);

    static BridgeCommandCatalog()
    {
        string[] errors = [..Validate()];
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                $"Bridge command metadata is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }
    }

    public static IReadOnlyList<BridgeCommandMetadata> All => Commands;

    public static bool TryGetByPipeName(string? pipeName,
#if !NETFRAMEWORK
        [NotNullWhen(true)]
#endif
        out BridgeCommandMetadata? metadata)
    {
        if (pipeName != null && !string.IsNullOrWhiteSpace(pipeName) && ByPipeName.TryGetValue(pipeName, out var found))
        {
            metadata = found;
            return true;
        }

        metadata = null;
        return false;
    }

    public static bool TryGetByCanonicalName(string? canonicalName,
#if !NETFRAMEWORK
        [NotNullWhen(true)]
#endif
        out BridgeCommandMetadata? metadata)
    {
        if (canonicalName != null && !string.IsNullOrWhiteSpace(canonicalName) && ByCanonicalName.TryGetValue(canonicalName, out var found))
        {
            metadata = found;
            return true;
        }

        metadata = null;
        return false;
    }

    public static IEnumerable<string> Validate()
    {
        List<string> errors = [];
        string[] duplicatePipe = [..Commands
            .GroupBy(item => item.PipeName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)];
        foreach (var duplicatePipeName in duplicatePipe)
        {
            errors.Add($"duplicate pipe command name: {duplicatePipeName}");
        }

        string[] duplicateCanonical = [..Commands
            .GroupBy(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)];
        foreach (var duplicateCanonicalName in duplicateCanonical)
        {
            errors.Add($"duplicate canonical command name: {duplicateCanonicalName}");
        }

        foreach (var command in Commands)
        {
            if (string.IsNullOrWhiteSpace(command.CanonicalName))
            {
                errors.Add("missing canonical command name");
            }

            if (string.IsNullOrWhiteSpace(command.PipeName))
            {
                errors.Add($"missing pipe command name for canonical '{command.CanonicalName}'");
            }

            if (string.IsNullOrWhiteSpace(command.Description))
            {
                errors.Add($"missing description for '{command.PipeName}'");
            }

            if (string.IsNullOrWhiteSpace(command.Example))
            {
                errors.Add($"missing example for '{command.PipeName}'");
            }
        }

        return errors;
    }

    private static BridgeCommandMetadata[] Build()
    {
        return
        [
            Create("Tools.IdeHelp", "help"),
            Create("Tools.IdeSmokeTest", "smoke-test"),
            Create("Tools.IdeGetState", "state"),
            Create("Tools.IdeGetUiSettings", "ui-settings"),
            Create("Tools.IdeWaitForReady", "ready"),
            Create("Tools.IdeOpenSolution", "open-solution"),
            Create("Tools.IdeCreateSolution", "create-solution"),
            Create("Tools.IdeCloseIde", "close-ide"),
            Create("Tools.IdeBatchCommands", "batch"),
            Create("Tools.IdeFindText", "find-text"),
            Create("Tools.IdeFindTextBatch", "find-text-batch"),
            Create("Tools.IdeFindFiles", "find-files"),
            Create("Tools.IdeOpenDocument", "open-document"),
            Create("Tools.IdeListDocuments", "list-documents"),
            Create("Tools.IdeListOpenTabs", "list-tabs"),
            Create("Tools.IdeActivateDocument", "activate-document"),
            Create("Tools.IdeCloseDocument", "close-document"),
            Create("Tools.IdeSaveDocument", "save-document"),
            Create("Tools.IdeCloseFile", "close-file"),
            Create("Tools.IdeCloseAllExceptCurrent", "close-others"),
            Create("Tools.IdeActivateWindow", "activate-window"),
            Create("Tools.IdeListWindows", "list-windows"),
            Create("Tools.IdeExecuteVsCommand", "execute-command"),
            Create("Tools.IdeFindAllReferences", "find-references"),
            Create("Tools.IdeCountReferences", "count-references"),
            Create("Tools.IdeShowCallHierarchy", "call-hierarchy"),
            Create("Tools.IdeGetDocumentSlice", "document-slice"),
            Create("Tools.IdeGetSmartContextForQuery", "smart-context"),
            Create("Tools.IdeApplyUnifiedDiff", "apply-diff"),
            Create("Tools.IdeSetBreakpoint", "set-breakpoint"),
            Create("Tools.IdeListBreakpoints", "list-breakpoints"),
            Create("Tools.IdeRemoveBreakpoint", "remove-breakpoint"),
            Create("Tools.IdeClearAllBreakpoints", "clear-breakpoints"),
            Create("Tools.IdeDebugGetState", "debug-state"),
            Create("Tools.IdeDebugStart", "debug-start"),
            Create("Tools.IdeDebugStop", "debug-stop"),
            Create("Tools.IdeDebugBreak", "debug-break"),
            Create("Tools.IdeDebugContinue", "debug-continue"),
            Create("Tools.IdeDebugStepOver", "debug-step-over"),
            Create("Tools.IdeDebugStepInto", "debug-step-into"),
            Create("Tools.IdeDebugStepOut", "debug-step-out"),
            Create("Tools.IdeDebugThreads", "debug-threads"),
            Create("Tools.IdeDebugStack", "debug-stack"),
            Create("Tools.IdeDebugLocals", "debug-locals"),
            Create("Tools.IdeDebugModules", "debug-modules"),
            Create("Tools.IdeDebugWatch", "debug-watch"),
            Create("Tools.IdeDebugExceptions", "debug-exceptions"),
            Create("Tools.IdeDiagnosticsSnapshot", "diagnostics-snapshot"),
            Create("Tools.IdeBuildConfigurations", "build-configurations"),
            Create("Tools.IdeSetBuildConfiguration", "set-build-configuration"),
            Create("Tools.IdeBuildSolution", "build"),
            Create("Tools.IdeRebuildSolution", "rebuild"),
            Create("Tools.IdeGetErrorList", "errors"),
            Create("Tools.IdeGetWarnings", "warnings"),
            Create("Tools.IdeGetMessages", "messages"),
            Create("Tools.IdeBuildAndCaptureErrors", "build-errors"),
            Create("Tools.IdeGoToDefinition", "goto-definition"),
            Create("Tools.IdeGoToImplementation", "goto-implementation"),
            Create("Tools.IdeGetFileOutline", "file-outline"),
            Create("Tools.IdeGetFileSymbols", "file-symbols"),
            Create("Tools.IdeSearchSymbols", "search-symbols"),
            Create("Tools.IdeGetQuickInfo", "quick-info"),
            Create("Tools.IdeGetDocumentSlices", "document-slices"),
            Create("Tools.IdeEnableBreakpoint", "enable-breakpoint"),
            Create("Tools.IdeDisableBreakpoint", "disable-breakpoint"),
            Create("Tools.IdeEnableAllBreakpoints", "enable-all-breakpoints"),
            Create("Tools.IdeDisableAllBreakpoints", "disable-all-breakpoints"),
            Create("Tools.IdeListProjects", "list-projects"),
            Create("Tools.IdeQueryProjectItems", "query-project-items"),
            Create("Tools.IdeQueryProjectProperties", "query-project-properties"),
            Create("Tools.IdeQueryProjectConfigurations", "query-project-configurations"),
            Create("Tools.IdeQueryProjectReferences", "query-project-references"),
            Create("Tools.IdeQueryProjectOutputs", "query-project-outputs"),
            Create("Tools.IdeAddProject", "add-project"),
            Create("Tools.IdeRemoveProject", "remove-project"),
            Create("Tools.IdeSetStartupProject", "set-startup-project"),
            Create("Tools.IdeRenameProject", "rename-project"),
            Create("Tools.IdeAddFileToProject", "add-file-to-project"),
            Create("Tools.IdeRemoveFileFromProject", "remove-file-from-project"),
            Create("Tools.IdeSearchSolutions", "search-solutions"),
            Create("Tools.IdeSetPythonProjectEnv", "set-python-project-env"),
        ];
    }

    private static BridgeCommandMetadata Create(string canonicalName, string pipeName)
    {
        var (description, example) = GetCommandDetail(pipeName);
        return new BridgeCommandMetadata(canonicalName, pipeName, description, example);
    }

    private const string ExampleFile = @"C:\repo\src\foo.cpp";

    private static string ExampleCommand(string commandName, string? argsJson = null)
        => string.IsNullOrWhiteSpace(argsJson) ? commandName : commandName + " " + argsJson;

    private static (string Description, string Example) GetCommandDetail(string commandName)
    {
        if (TryGetWorkspaceCommandDetail(commandName, out (string Description, string Example) detail) ||
            TryGetNavigationCommandDetail(commandName, out detail) ||
            TryGetDebugCommandDetail(commandName, out detail) ||
            TryGetProjectCommandDetail(commandName, out detail))
        {
            return detail;
        }

        return ($"Run bridge command '{commandName}'.", commandName);
    }
}

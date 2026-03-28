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

public static class BridgeCommandCatalog
{
    private static readonly BridgeCommandMetadata[] Commands = Build();
    private static readonly Dictionary<string, BridgeCommandMetadata> ByPipeName = Commands
        .ToDictionary(item => item.PipeName, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BridgeCommandMetadata> ByCanonicalName = Commands
        .ToDictionary(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase);

    static BridgeCommandCatalog()
    {
        var errors = Validate().ToArray();
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
        var errors = new List<string>();
        var duplicatePipe = Commands
            .GroupBy(item => item.PipeName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        foreach (var duplicatePipeName in duplicatePipe)
        {
            errors.Add($"duplicate pipe command name: {duplicatePipeName}");
        }

        var duplicateCanonical = Commands
            .GroupBy(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
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
        return commandName switch
        {
            "help" => ("Return bridge command catalog metadata and usage examples.", commandName),
            "smoke-test" => ("Capture smoke-test IDE state to verify bridge command execution.", commandName),
            "state" => ("Capture IDE state including solution, active document, and bridge identity.", ExampleCommand("state", @"{""out"":""C:\\temp\\ide-state.json""}")),
            "ui-settings" => ("Read current IDE Bridge UI/security settings without modifying them.", "ui-settings"),
            "ready" => ("Wait for Visual Studio and IntelliSense to be ready for semantic commands.", ExampleCommand("ready", @"{""timeout_ms"":120000}")),
            "open-solution" => ("Open a specific existing .sln or .slnx file in the current Visual Studio instance without opening a new window. Use this when you already know the exact solution path.", ExampleCommand("open-solution", @"{""solution"":""C:\\repo\\PinballBot\\PinballBot.sln""}")),
            "create-solution" => ("Create and open a new solution in the current Visual Studio instance.", ExampleCommand("create-solution", @"{""directory"":""C:\\repo\\Scratch"",""name"":""ScratchApp""}")),
            "close-ide" => ("Close the current Visual Studio instance through DTE Quit.", commandName),
            "batch" => ("Run multiple commands in one request.", ExampleCommand("batch", @"{""steps"": [{""id"": ""state"", ""command"": ""state""}]}")),
            "find-text" => ("Find text across the solution, project, or current document, with optional subtree filtering.", ExampleCommand("find-text", @"{""query"":""OnInit"",""path"":""src\\libslic3r""}")),
            "find-text-batch" => ("Find text for multiple queries in one bridge round-trip, internally chunked when needed.", ExampleCommand("find-text-batch", @"{""queries"": [""OnInit"", ""RunAsync"", ""BridgeHealth""], ""path"": ""src\\VsIdeBridge"", ""max_queries_per_chunk"": 5}")),
            "find-files" => ("Search Solution Explorer-style files by name or path fragment and return ranked matches.", ExampleCommand("find-files", @"{""query"":""CMakeLists.txt""}")),
            "open-document" => ("Open a document by absolute path, solution-relative path, or solution item name.", ExampleCommand("open-document", @"{""file"":""src\\CMakeLists.txt"",""line"":1,""column"":1}")),
            "list-documents" => ("List open documents.", commandName),
            "list-tabs" => ("List open editor tabs and identify the active tab.", commandName),
            "activate-document" => ("Activate an open document tab by query.", ExampleCommand("activate-document", @"{""query"":""Program.cs""}")),
            "close-document" => ("Close one or more open tabs by query.", ExampleCommand("close-document", @"{""query"":"".json"",""all"":true}")),
            "save-document" => ("Save one document by path or save all open documents.", ExampleCommand("save-document", @"{""file"":""C:\\repo\\src\\foo.cpp""}")),
            "close-file" => ("Close one open file tab by path or query.", ExampleCommand("close-file", @"{""file"":""C:\\repo\\src\\foo.cpp""}")),
            "close-others" => ("Close all tabs except the active tab.", commandName),
            "activate-window" => ("Activate a Visual Studio tool window by caption or kind.", ExampleCommand("activate-window", @"{""window"":""Error List""}")),
            "list-windows" => ("List Visual Studio tool windows, optionally filtered by query.", ExampleCommand("list-windows", @"{""query"":""Error""}")),
            "execute-command" => ("Execute an arbitrary Visual Studio command with optional arguments.", ExampleCommand("execute-command", @"{""name"":""Edit.FormatDocument""}")),
            "find-references" => ("Run Find All References for the symbol at a file, line, and column.", ExampleCommand("find-references", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42,""column"":13}")),
            "count-references" => ("Run Find All References and return exact count when Visual Studio exposes one, or explicit unknown otherwise.", ExampleCommand("count-references", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42,""column"":13}")),
            "call-hierarchy" => ("Open Call Hierarchy for the symbol at a file, line, and column.", ExampleCommand("call-hierarchy", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42,""column"":13}")),
            "document-slice" => ("Fetch one code slice from a file.", ExampleCommand("document-slice", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":120,""context_before"":8,""context_after"":20}")),
            "smart-context" => ("Collect focused code context for a natural-language query.", ExampleCommand("smart-context", @"{""query"":""where is GUI_App::OnInit used"",""max_contexts"":3}")),
            "apply-diff" => ("Apply unified diff text or editor patch text through the live editor so changes are visible in Visual Studio. Changed files open by default.", ExampleCommand("apply-diff", @"{""patch_file"":""C:\\temp\\change.diff""}")),
            "set-breakpoint" => ("Set a breakpoint at file/line with optional condition, hit count, and tracepoint behavior.", ExampleCommand("set-breakpoint", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42}")),
            "list-breakpoints" => ("List current breakpoints.", commandName),
            "remove-breakpoint" => ("Remove breakpoints by file/line, id, or all.", ExampleCommand("remove-breakpoint", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42}")),
            "clear-breakpoints" => ("Clear all breakpoints.", commandName),
            "debug-state" => ("Get debugger mode and active stack frame info.", commandName),
            "debug-start" => ("Start debugging the current startup project.", commandName),
            "debug-stop" => ("Stop the debugger.", commandName),
            "debug-break" => ("Break execution in the debugger.", commandName),
            "debug-continue" => ("Continue execution in the debugger.", commandName),
            "debug-step-over" => ("Step over the current line in the debugger.", commandName),
            "debug-step-into" => ("Step into the current call in the debugger.", commandName),
            "debug-step-out" => ("Step out of the current function in the debugger.", commandName),
            "debug-threads" => ("List debugger threads for the active debug session.", commandName),
            "debug-stack" => ("Capture stack frames for the current or selected debugger thread.", ExampleCommand("debug-stack", @"{""thread_id"":1,""max_frames"":50}")),
            "debug-locals" => ("Capture local variables for the active stack frame.", ExampleCommand("debug-locals", @"{""max"":200}")),
            "debug-modules" => ("Capture debugger module snapshot (best effort by debugger engine).", commandName),
            "debug-watch" => ("Evaluate one debugger watch expression in break mode.", ExampleCommand("debug-watch", @"{""expression"":""count""}")),
            "debug-exceptions" => ("Capture debugger exception group/settings snapshot (best effort).", commandName),
            "diagnostics-snapshot" => ("Aggregate IDE state, debugger state, build state, and current errors/warnings.", ExampleCommand("diagnostics-snapshot", @"{""wait_for_intellisense"":true}")),
            "build-configurations" => ("List available solution build configurations and platforms.", commandName),
            "set-build-configuration" => ("Activate one build configuration/platform pair.", ExampleCommand("set-build-configuration", @"{""configuration"":""Debug"",""platform"":""x64""}")),
            "build" => ("Build the solution or a specific project. Provide project to build one project; omit it to build the whole solution.", ExampleCommand("build", @"{""project"":""VsIdeBridgeInstaller"",""configuration"":""Release""}")),
            "rebuild" => ("Rebuild the active solution inside Visual Studio. This performs a clean step before building and is heavier than build.", ExampleCommand("rebuild", @"{""configuration"":""Release""}")),
            "errors" => ("Capture Error List rows with optional severity and text filters.", ExampleCommand("errors", @"{""severity"":""error"",""max"":50}")),
            "warnings" => ("Capture warning rows with optional code/path/project filters.", ExampleCommand("warnings", @"{""group_by"":""code""}")),
            "build-errors" => ("Build then capture Error List rows in one call. By default this refuses to build when diagnostics already exist and fails if any errors, warnings, or messages remain after the build.", ExampleCommand("build-errors", @"{""max"":200,""require_clean_diagnostics"":true}")),
            "goto-definition" => ("Navigate to the definition of the symbol at a file, line, and column.", ExampleCommand("goto-definition", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42,""column"":13}")),
            "goto-implementation" => ("Navigate to one implementation of the symbol at a file, line, and column.", ExampleCommand("goto-implementation", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42,""column"":13}")),
            "file-outline" => ("List a file outline from the code model.", ExampleCommand("file-outline", @"{""file"":""C:\\repo\\src\\foo.cpp""}")),
            "file-symbols" => ("List symbols in one file with optional kind filtering.", ExampleCommand("file-symbols", @"{""file"":""C:\\repo\\src\\foo.cpp"",""kind"":""function""}")),
            "search-symbols" => ("Search symbol definitions by name across solution scope.", ExampleCommand("search-symbols", @"{""query"":""RunAsync"",""kind"":""function"",""path"":""src\\VsIdeBridge""}")),
            "quick-info" => ("Resolve symbol information at file, line, and column with surrounding context.", ExampleCommand("quick-info", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42,""column"":13}")),
            "document-slices" => ("Fetch multiple code slices from ranges_file or inline ranges JSON.", ExampleCommand("document-slices", @"{""ranges"": [{""file"": ""C:\\repo\\src\\foo.cpp"", ""line"": 42, ""context_before"": 8, ""context_after"": 20}]}")),
            "enable-breakpoint" => ("Enable a breakpoint by id or file/line.", ExampleCommand("enable-breakpoint", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42}")),
            "disable-breakpoint" => ("Disable a breakpoint by id or file/line.", ExampleCommand("disable-breakpoint", @"{""file"":""C:\\repo\\src\\foo.cpp"",""line"":42}")),
            "enable-all-breakpoints" => ("Enable all breakpoints.", commandName),
            "disable-all-breakpoints" => ("Disable all breakpoints.", commandName),
            "list-projects" => ("List all projects in the open solution.", commandName),
            "query-project-items" => ("List items in a project with file paths, kinds, and item types.", ExampleCommand("query-project-items", @"{""project"":""VsIdeBridge"",""path"":""src\\VsIdeBridge"",""max"":200}")),
            "query-project-properties" => ("Read MSBuild-style project properties from one project, including normalized TargetFramework values when available.", ExampleCommand("query-project-properties", @"{""project"":""VsIdeBridge"",""names"": [""TargetFramework"", ""RootNamespace"", ""AssemblyName""]}")),
            "query-project-configurations" => ("List project configurations and platforms for one project.", ExampleCommand("query-project-configurations", @"{""project"":""VsIdeBridge""}")),
            "query-project-references" => ("List project references for one project. By default this returns resolved references with framework assemblies omitted; set declared_only=true for project-file declarations or include_framework=true for the full closure.", ExampleCommand("query-project-references", @"{""project"":""VsIdeBridge.Tests"",""declared_only"":true}")),
            "query-project-outputs" => ("Resolve the primary output artifact and output directory for one project using the active or requested build shape.", ExampleCommand("query-project-outputs", @"{""project"":""VsIdeBridge"",""configuration"":""Release"",""target_framework"":""net8.0""}")),
            "add-project" => ("Add an existing or new project to the solution.", ExampleCommand("add-project", @"{""path"":""C:\\repo\\MyLib\\MyLib.csproj""}")),
            "remove-project" => ("Remove a project from the solution by name or path.", ExampleCommand("remove-project", @"{""project"":""MyLib""}")),
            "set-startup-project" => ("Set the solution startup project by name or path.", ExampleCommand("set-startup-project", @"{""project"":""MyApp""}")),
            "rename-project" => ("Rename a project within the solution. This changes the project name shown by Visual Studio, but does not rename folders or the project file on disk.", ExampleCommand("rename-project", @"{""project"":""MyLib"",""new-name"":""MyLibrary""}")),
            "add-file-to-project" => ("Add an existing file to a project.", ExampleCommand("add-file-to-project", @"{""project"":""MyLib"",""file"":""C:\\repo\\MyLib\\Foo.cs""}")),
            "remove-file-from-project" => ("Remove a file from a project.", ExampleCommand("remove-file-from-project", @"{""project"":""MyLib"",""file"":""C:\\repo\\MyLib\\Foo.cs""}")),
            "search-solutions" => ("Search for solution files (.sln/.slnx) on disk under a given root directory. Defaults to %USERPROFILE%\\source\\repos.", ExampleCommand("search-solutions", @"{""query"":""MyApp"",""path"":""%USERPROFILE%\\source\\repos"",""max_depth"":4}")),
            "set-python-project-env" => ("Set the active Python interpreter for the open .pyproj project or open-folder workspace in Visual Studio (affects IntelliSense and debugging).", ExampleCommand("set-python-project-env", @"{""path"":""%USERPROFILE%\\miniconda3\\envs\\superslicer\\python.exe""}")),
            _ => ($"Run bridge command '{commandName}'.", commandName),
        };
    }
}

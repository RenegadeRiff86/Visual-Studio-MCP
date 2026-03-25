using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static IEnumerable<ToolEntry> SemanticTools()
    {
        yield return BridgeTool(ToolDefinitionCatalog.FileOutline(
            ObjectSchema(Req(FileArg, FileDesc))),
            "file-outline",
            a => Build((FileArg, OptionalString(a, FileArg))));

        yield return BridgeTool(ToolDefinitionCatalog.SymbolInfo(
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc))),
            "quick-info",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))));

        yield return BridgeTool(ToolDefinitionCatalog.FindReferences(
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc))),
            "find-references",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))));

        yield return BridgeTool("count_references",
            "Run Find All References and return the exact count.",
            ObjectSchema(
                Req(FileArg, FileDesc),
                ReqInt(Line, LineDesc),
                ReqInt(Column, ColumnDesc),
                OptBool("activate_window", "Activate references window while counting (default true)."),
                OptInt("timeout_ms", "Optional window wait timeout in milliseconds.")),
            "count-references",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column)),
                BoolArg("activate-window", a, "activate_window", true, true),
                ("timeout-ms", OptionalText(a, "timeout_ms"))),
            Search);

        yield return BridgeTool("call_hierarchy",
            "Open Call Hierarchy for the symbol at a file/line/column.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc)),
            "call-hierarchy",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))),
            Search);

        yield return BridgeTool("goto_definition",
            "Navigate to the definition of the symbol at a file/line/column.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc)),
            "goto-definition",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))),
            Search);

        yield return BridgeTool("goto_implementation",
            "Navigate to an implementation of the symbol at a file/line/column.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc)),
            "goto-implementation",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))),
            Search);

        yield return BridgeTool(ToolDefinitionCatalog.PeekDefinition(
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc))),
            "peek-definition",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))));
    }
}

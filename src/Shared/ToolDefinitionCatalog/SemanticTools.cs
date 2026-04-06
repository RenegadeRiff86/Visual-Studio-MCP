using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    public static ToolDefinition SearchSymbols(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "search_symbols",
            "search",
            "Find class/method/field definitions — use instead of Grep for symbol names.",
            "Find class, method, field, property, or interface definitions by name across the solution. Use this instead of Grep when you know the name of what you are looking for — returns navigation-ready locations with kind and file. Use find_text for arbitrary string patterns.",
            parameterSchema,
            bridgeCommand: "search-symbols",
            aliases: ["find_symbol", "find_symbols", "symbol_search", "find_class", "find_method", "find_definition"],
            tags: ["code", NavigationTag, "symbols", "definition", "grep"]);

    public static ToolDefinition FileOutline(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "file_outline",
            "search",
            "List file symbols.",
            "Get the symbol outline of a file. Use this after find_files or before read_file when you want the shape of a file without scanning the whole body.",
            parameterSchema,
            bridgeCommand: "file-outline",
            aliases: ["document_outline", "outline_file", "list_file_symbols"],
            tags: ["code", NavigationTag, "outline", "symbols", "file"]);

    public static ToolDefinition SymbolInfo(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "symbol_info",
            "search",
            "Get symbol type/signature at a location (hover info).",
            "Return the type, signature, and documentation for the symbol at file/line/column — equivalent to hovering in the editor. Use when you want to know WHAT a symbol is (its type, overloads, doc comment). Use peek_definition when you want to read the actual source of the definition.",
            parameterSchema,
            bridgeCommand: "quick-info",
            aliases: ["quick_info", "get_symbol_info", "symbol_details", "hover_info"],
            tags: ["code", NavigationTag, "symbol", "info"]);

    public static ToolDefinition PeekDefinition(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "peek_definition",
            "search",
            "Read definition source without navigating.",
            "Return the full source code of the definition of the symbol at file/line/column without navigating the editor. Use when you want to READ the implementation. Use symbol_info when you only need the type/signature. Prefer this before broader read_file calls when following symbol flow.",
            parameterSchema,
            bridgeCommand: "peek-definition",
            aliases: ["get_definition", "read_definition", "definition_peek"],
            tags: ["code", NavigationTag, "definition", "peek"]);

    public static ToolDefinition FindReferences(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_references",
            "search",
            "Find references to a symbol.",
            "Run Find All References for the symbol at file/line/column. This is more expensive than direct read/search tools.",
            parameterSchema,
            bridgeCommand: "find-references",
            aliases: ["references", "find_symbol_references", "search_references"],
            tags: ["code", NavigationTag, "references", "symbol"]);
}

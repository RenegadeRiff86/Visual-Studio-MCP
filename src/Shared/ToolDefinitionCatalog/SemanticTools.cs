using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    public static ToolDefinition SearchSymbols(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "search_symbols",
            "search",
            "Locate a class/method/field definition — use this FIRST when you know a symbol name, not find_text.",
            "Use this as the first step whenever you know the name of a type, method, property, or field you need to find. Returns " +
            "the exact file and line of every definition without scanning text — faster and more precise than find_text or Grep for " +
            "named symbols. Only fall back to find_text for non-symbol string patterns. Each match includes a \"handle\" field — pass " +
            "it directly as the file argument to read_file or apply_diff instead of copying the full path.",
            parameterSchema,
            bridgeCommand: "search-symbols",
            aliases: ["find_symbol", "find_symbols", "symbol_search", "find_class", "find_method", "find_definition"],
            tags: ["code", NavigationTag, "symbols", "definition", "grep"]);

    public static ToolDefinition FileOutline(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "file_outline",
            "search",
            "Map a file's members before reading — call this BEFORE read_file on any unfamiliar file.",
            "ALWAYS call this before reading a file you have not already inspected in this session. Returns every class, method, " +
            "property, and field with their line numbers so you can target read_file with a precise range instead of scanning the " +
            "whole file. The file argument accepts a handle (h:N or f:N) from find_text, search_symbols, find_files, or glob — " +
            "pass the handle directly instead of copying the full path. " +
            "Use search_symbols when you need to locate a symbol across the solution rather than inspecting one specific file.",
            parameterSchema,
            bridgeCommand: "file-outline",
            aliases: ["document_outline", "outline_file", "list_file_symbols"],
            tags: ["code", NavigationTag, "outline", "symbols", "file"]);

    public static ToolDefinition SymbolInfo(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "symbol_info",
            "search",
            "Get symbol type/signature at a location (hover info).",
            "Return the type, signature, and documentation for the symbol at file/line/column — equivalent to hovering in the editor. " +
            "Use when you want to know WHAT a symbol is (its type, overloads, doc comment). Use peek_definition when you want to read " +
            "the actual source of the definition. " +
            "Pass a handle (h:N) from search_symbols or find_text as the file argument, with line and column from that result.",
            parameterSchema,
            bridgeCommand: "quick-info",
            aliases: ["quick_info", "get_symbol_info", "symbol_details", "hover_info"],
            tags: ["code", NavigationTag, "symbol", "info"]);

    public static ToolDefinition PeekDefinition(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "peek_definition",
            "search",
            "Read definition source without navigating.",
            "Return the full source code of the definition of the symbol at file/line/column without navigating the editor. Use when " +
            "you want to READ the implementation. Use symbol_info when you only need the type/signature. Prefer this before broader " +
            "read_file calls when following symbol flow. " +
            "Pass a handle (h:N) from search_symbols or find_text as the file argument, with line and column from that result.",
            parameterSchema,
            bridgeCommand: "peek-definition",
            aliases: ["get_definition", "read_definition", "definition_peek"],
            tags: ["code", NavigationTag, "definition", "peek"]);

    public static ToolDefinition FindReferences(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_references",
            "search",
            "Find references to a symbol.",
            "Run Find All References for the symbol at file/line/column. " +
            "Pass a handle (h:N) from search_symbols or find_text as the file argument, with the line and column from that same result. " +
            "This is more expensive than direct read/search tools.",
            parameterSchema,
            bridgeCommand: "find-references",
            aliases: ["references", "find_symbol_references", "search_references"],
            tags: ["code", NavigationTag, "references", "symbol"]);
}

using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    private const string PrefixPropertyName = "prefix";
    private const string KindPropertyName = "kind";
    private const string ProducedByPropertyName = "producedBy";

    public JsonObject BuildToolHelp(string? toolName = null, string? category = null)
    {
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            if (TryGet(toolName, out ToolDefinition? tool))
            {
                return new JsonObject
                {
                    ["Summary"] = $"Help for tool '{tool.Name}'.",
                    ["invocationHint"] = tool.Name == "call_tool"
                        ? "call_tool is directly callable and cannot dispatch to itself."
                        : $"In lazy mode, invoke this catalog tool with call_tool: {{ \"name\": \"call_tool\", \"arguments\": {{ \"name\": \"{tool.Name}\", \"arguments\": {{ ... }} }} }}.",
                    ["handleGuide"] = BuildHandleGuide(),
                    ["invocation"] = tool.BuildInvocationEntry(),
                    ["tool"] = tool.BuildToolObject(),
                };
            }

            return new JsonObject
            {
                ["error"] = $"Tool '{toolName}' not found.",
                ["suggestion"] = "Use list_tools, list_tool_categories, or recommend_tools to discover available tools.",
            };
        }

        if (!string.IsNullOrWhiteSpace(category))
            return BuildToolsByCategory(category);

        return BuildCategoryList();
    }

    private static JsonObject BuildHandleGuide()
    {
        return new JsonObject
        {
            ["summary"] = "Bridge result rows may include handle IDs. Treat those handles as the canonical file reference for this session.",
            ["policy"] = "If a result row has a handle, pass that handle directly as any file/path value for follow-up bridge calls. Do not copy the row's full path unless no handle exists.",
            ["format"] = "{prefix}:{n}; n is a per-kind sequence.",
            ["applyDiffPolicy"] = "For single-file replacements, call apply_diff with file + old_content + new_content. Reserve the diff argument for multi-file or structural patches.",
            ["callToolExamples"] = new JsonArray
            {
                "call_tool({\"name\":\"read_file\",\"arguments\":{\"file\":\"h:2\",\"start_line\":260,\"end_line\":360}})",
                "call_tool({\"name\":\"apply_diff\",\"arguments\":{\"file\":\"h:2\",\"old_content\":\"exact old text\",\"new_content\":\"replacement\"}})",
                "call_tool({\"name\":\"apply_diff\",\"arguments\":{\"diff\":\"*** Begin Patch\\n*** Update File: h:2\\n*** Move to: src/NewName.cs\\n*** End Patch\"}}) for structural patches only",
            },
            ["runtime"] = "IdeBridgeRuntime.HandleService owns HandleRegistry, producer registration, patch rewriting, and TryResolve. PathResolver resolves handles transparently before normal path resolution.",
            ["producerApis"] = new JsonArray
            {
                "RegisterDiagnosticRows(HandleKind, JArray rows)",
                "RegisterSearchHits(JArray matches)",
            },
            ["consumerApis"] = new JsonArray
            {
                "RewritePatch(string patchText)",
                "TryResolve(string id, out HandleEntry)",
                "HandleService.IsHandle(string? value)",
            },
            ["prefixes"] = new JsonArray
            {
                BuildHandlePrefix("e", "Error", "errors"),
                BuildHandlePrefix("w", "Warning", "warnings"),
                BuildHandlePrefix("m", "Message", "messages, diagnostics_snapshot"),
                BuildHandlePrefix("f", "FileMatch", "find_files, glob"),
                BuildHandlePrefix("h", "SearchHit", "find_text, search_symbols"),
            },
        };
    }

    private static JsonObject BuildHandlePrefix(string prefix, string kind, string producedBy)
    {
        return new JsonObject
        {
            [PrefixPropertyName] = prefix,
            [KindPropertyName] = kind,
            [ProducedByPropertyName] = producedBy,
        };
    }
}

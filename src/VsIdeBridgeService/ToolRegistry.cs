using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal sealed class ToolExecutionRegistry
{
    private readonly ToolEntry[] _all;
    private readonly Dictionary<string, ToolEntry> _byLookupName;
    private readonly ToolRegistry _definitions;

    public ToolExecutionRegistry(IEnumerable<ToolEntry> entries)
    {
        _all = [.. entries];
        _byLookupName = BuildLookup(_all);
        _definitions = new ToolRegistry(_all.Select(entry => entry.Definition));
    }

    public IReadOnlyList<ToolEntry> All => _all;

    public ToolRegistry Definitions => _definitions;

    public bool TryGet(string name, [NotNullWhen(true)] out ToolEntry? entry)
        => _byLookupName.TryGetValue(name, out entry);

    public bool TryGetDefinition(string name, [NotNullWhen(true)] out ToolDefinition? definition)
        => _definitions.TryGet(name, out definition);

    public JsonArray BuildToolsList()
        => BuildToolsList(McpToolSurface.Full);

    public JsonArray BuildToolsList(McpToolSurface surface)
    {
        JsonArray result = [];
        foreach (ToolEntry entry in GetMcpVisibleEntries(surface))
            result.Add(entry.Definition.BuildToolObject());

        return result;
    }

    private IEnumerable<ToolEntry> GetMcpVisibleEntries(McpToolSurface surface)
    {
        if (surface.IsFull)
        {
            return _all;
        }

        return surface.VisibleToolNames!
            .Select(name => _byLookupName.TryGetValue(name, out ToolEntry? entry) ? entry : null)
            .OfType<ToolEntry>()
            .DistinctBy(static entry => entry.Name);
    }

    public async Task<JsonNode> DispatchAsync(JsonNode? id, string name, JsonObject? args, BridgeConnection bridge)
    {
        if (!_byLookupName.TryGetValue(name, out ToolEntry? entry))
        {
            // Return a soft error so the model sees helpful guidance instead of a hard JSON-RPC failure.
            // This dramatically reduces hallucination spirals when the model guesses wrong tool names.
            McpServerLog.Write($"tool unknown name={name}");
            return BuildUnknownToolResult(name);
        }

        McpServerLog.Write($"tool dispatch name={name}");
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ValidateArguments(id, entry, args);
            JsonNode result = await entry.Handler(id, args, bridge).ConfigureAwait(false);
            McpServerLog.Write($"tool complete name={name} ms={sw.ElapsedMilliseconds}");
            return result;
        }
        catch (McpRequestException ex)
        {
            // Convert known-tool failures into a content-level isError result so the model can
            // self-correct using the embedded help instead of spiraling after a JSON-RPC error.
            McpServerLog.Write($"tool failure tool={name} ms={sw.ElapsedMilliseconds} code={ex.Code} message={ex.Message}");
            return BuildToolFailureResult(ex, entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            McpServerLog.Write($"tool unhandled failure tool={name} ms={sw.ElapsedMilliseconds} exception={ex.GetType().Name} message={ex.Message}");
            return BuildToolFailureResult(
                new McpRequestException(
                    id,
                    McpErrorCodes.BridgeError,
                    $"Tool '{entry.Name}' failed before it could return a structured error: {ex.Message}"),
                entry);
        }
    }

    private static void ValidateArguments(JsonNode? id, ToolEntry entry, JsonObject? args)
    {
        JsonObject schema = entry.Definition.ParameterSchema;
        if (!SchemaTypeIs(schema, "object"))
            return;

        JsonObject actual = args ?? [];
        if (!TryValidateObject(actual, schema, out string? error))
            throw new McpRequestException(
                id,
                McpErrorCodes.InvalidParams,
                error ?? $"Arguments for tool '{entry.Name}' failed schema validation.");
    }

    private static bool TryValidateObject(JsonObject actual, JsonObject schema, out string? error)
        => TryValidateObject(actual, schema, path: null, out error);

    private static bool TryValidateObject(JsonObject actual, JsonObject schema, string? path, out string? error)
    {
        JsonObject? properties = schema["properties"] as JsonObject;
        HashSet<string> allowed = properties is null
            ? []
            : properties.Select(static property => property.Key).ToHashSet(StringComparer.Ordinal);

        if (SchemaBoolean(schema, "additionalProperties") == false)
        {
            foreach (KeyValuePair<string, JsonNode?> property in actual)
            {
                if (!allowed.Contains(property.Key))
                {
                    error = $"Unexpected argument '{FormatPath(path, property.Key)}'.";
                    return false;
                }
            }
        }

        foreach (string required in RequiredProperties(schema))
        {
            if (!actual.ContainsKey(required) || actual[required] is null)
            {
                error = $"Missing required argument '{FormatPath(path, required)}'.";
                return false;
            }
        }

        if (properties is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> property in actual)
            {
                if (!properties.TryGetPropertyValue(property.Key, out JsonNode? propertySchemaNode) ||
                    propertySchemaNode is not JsonObject propertySchema)
                {
                    continue;
                }

                string propertyPath = FormatPath(path, property.Key);
                if (!TryValidateValue(property.Value, propertySchema, propertyPath, out error))
                    return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryValidateValue(JsonNode? value, JsonObject schema, string path, out string? error)
    {
        string? type = SchemaType(schema);
        if (type is null)
        {
            error = null;
            return true;
        }

        if (value is null)
        {
            error = $"Argument '{path}' must be {DescribeType(type)}.";
            return false;
        }

        switch (type)
        {
            case "string":
                return TryValidateScalar<string>(value, path, type, out error);
            case "integer":
                return TryValidateInteger(value, path, out error);
            case "number":
                return TryValidateNumber(value, path, out error);
            case "boolean":
                return TryValidateScalar<bool>(value, path, type, out error);
            case "array":
                return TryValidateArray(value, schema, path, out error);
            case "object":
                if (value is JsonObject child)
                    return TryValidateObject(child, schema, path, out error);

                error = $"Argument '{path}' must be {DescribeType(type)}.";
                return false;
            default:
                error = null;
                return true;
        }
    }

    private static bool TryValidateScalar<T>(JsonNode value, string path, string type, out string? error)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<T>(out _))
        {
            error = null;
            return true;
        }

        error = $"Argument '{path}' must be {DescribeType(type)}.";
        return false;
    }

    private static bool TryValidateInteger(JsonNode value, string path, out string? error)
    {
        if (value is JsonValue jsonValue &&
            (jsonValue.TryGetValue<int>(out _) || jsonValue.TryGetValue<long>(out _)))
        {
            error = null;
            return true;
        }

        error = $"Argument '{path}' must be an integer.";
        return false;
    }

    private static bool TryValidateNumber(JsonNode value, string path, out string? error)
    {
        if (value is JsonValue jsonValue &&
            (jsonValue.TryGetValue<double>(out _) || jsonValue.TryGetValue<decimal>(out _)))
        {
            error = null;
            return true;
        }

        error = $"Argument '{path}' must be a number.";
        return false;
    }

    private static bool TryValidateArray(JsonNode value, JsonObject schema, string path, out string? error)
    {
        if (value is not JsonArray array)
        {
            error = $"Argument '{path}' must be an array.";
            return false;
        }

        if (schema["items"] is JsonObject itemSchema)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if (!TryValidateValue(array[index], itemSchema, $"{path}[{index}]", out error))
                    return false;
            }
        }

        error = null;
        return true;
    }

    private static IEnumerable<string> RequiredProperties(JsonObject schema)
    {
        if (schema["required"] is not JsonArray required)
            yield break;

        foreach (JsonNode? value in required)
        {
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }

    private static bool SchemaTypeIs(JsonObject schema, string expected)
        => string.Equals(SchemaType(schema), expected, StringComparison.OrdinalIgnoreCase);

    private static string? SchemaType(JsonObject schema)
        => schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out string? type)
            ? type
            : null;

    private static bool? SchemaBoolean(JsonObject schema, string propertyName)
        => schema[propertyName] is JsonValue value && value.TryGetValue<bool>(out bool boolValue)
            ? boolValue
            : null;

    private static string FormatPath(string? parent, string child)
        => string.IsNullOrEmpty(parent) ? child : $"{parent}.{child}";

    private static string DescribeType(string type)
        => type switch
        {
            "integer" => "an integer",
            "array" => "an array",
            "object" => "an object",
            _ when StartsWithVowel(type) => $"an {type}",
            _ => $"a {type}",
        };

    private static bool StartsWithVowel(string value)
        => value.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(value[0]));

    private static JsonObject BuildToolFailureResult(McpRequestException exception, ToolEntry entry)
    {
        JsonObject help = BuildInlineToolHelp(entry);
        string text =
            $"{exception.Message}\n\n" +
            $"Tool '{entry.Name}' failed with MCP error code {exception.Code}.\n\n" +
            $"Tool help for '{entry.Name}':\n{help.ToJsonString()}\n\n" +
            "Retry using the invocation wrapper shown above. In lazy mode, do not call catalog tool names as top-level MCP tools unless they appear in the protocol tools/list response.";
        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = true,
        };
    }

    private static JsonObject BuildInlineToolHelp(ToolEntry entry)
        => new()
        {
            ["Summary"] = $"Help for tool '{entry.Name}'.",
            ["invocation"] = entry.Definition.BuildInvocationEntry(),
            ["tool"] = entry.Definition.BuildToolObject(),
        };

    private JsonObject BuildUnknownToolResult(string attemptedName)
    {
        string? closest = FindClosestToolName(attemptedName);
        string suggestion = closest != null
            ? $"You called '{attemptedName}' but the closest matching tool is '{closest}'.\n\n"
            : "";

        string text =
            $"{suggestion}Unknown tool '{attemptedName}'.\n\n" +
            "To find the correct tool name:\n\n" +
            "1. Describe what you need to recommend_tools — returns the best matching tools for your task.\n" +
            "   Example: { \"name\": \"recommend_tools\", \"arguments\": { \"task\": \"find all callers of a method\" } }\n\n" +
            "2. Or browse a focused category with list_tools_by_category:\n" +
            "   Example: { \"name\": \"list_tools_by_category\", \"arguments\": { \"category\": \"search\" } }\n" +
            "   Categories: core, search, documents, diagnostics, debug, git, project, python, system, developer_tools\n\n" +
            "3. Or call list_tool_categories to see all category names and counts.\n\n" +
            "4. As a last resort, call list_tools (no parameters) to see every available tool name.\n" +
            "   Example: { \"name\": \"list_tools\", \"arguments\": {} }\n\n" +
            "Once you have the correct name, call tool_help to get its full schema:\n" +
            "   Example: { \"name\": \"tool_help\", \"arguments\": { \"name\": \"find_files\" } }";

        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = true,
        };
    }

    private string? FindClosestToolName(string attempted)
    {
        if (string.IsNullOrWhiteSpace(attempted))
            return null;

        // Common obvious mistakes first (fast path)
        if (attempted.Equals("shell", StringComparison.OrdinalIgnoreCase))
            return "shell_exec";

        // Simple similarity: prefer names that contain the guess or start with it
        List<string> candidates =
        [
            .._byLookupName.Keys
                .Where(k => k.Contains(attempted, StringComparison.OrdinalIgnoreCase) ||
                            attempted.Contains(k, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => Math.Abs(k.Length - attempted.Length))
                .ThenBy(k => k)
                .Take(3),
        ];

        if (candidates.Count > 0)
            return candidates[0];

        // Fallback: pick the first tool that shares any characters (very loose)
        return _byLookupName.Keys
            .OrderBy(k => LevenshteinDistance(k.ToLowerInvariant(), attempted.ToLowerInvariant()))
            .FirstOrDefault();
    }

    // Minimal Levenshtein distance implementation (no external dependencies)
    private static int LevenshteinDistance(string s, string target)
    {
        if (string.IsNullOrEmpty(s)) return target?.Length ?? 0;
        if (string.IsNullOrEmpty(target)) return s.Length;

        int[,] d = new int[s.Length + 1, target.Length + 1];
        for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= target.Length; j++) d[0, j] = j;

        for (int i = 1; i <= s.Length; i++)
            for (int j = 1; j <= target.Length; j++)
            {
                int cost = s[i - 1] == target[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        return d[s.Length, target.Length];
    }

    private static Dictionary<string, ToolEntry> BuildLookup(IEnumerable<ToolEntry> entries)
    {
        ToolEntry[] materialized = entries as ToolEntry[] ?? [.. entries];
        Dictionary<string, ToolEntry> lookup = new(StringComparer.Ordinal);

        // Pass 1: canonical names + explicit aliases must be unique. A
        // collision here is a real authoring bug in the registrars and
        // should fail loudly so it gets fixed before shipping.
        foreach (ToolEntry entry in materialized)
        {
            AddLookupEntry(lookup, entry.Name, entry);
            foreach (string alias in entry.Definition.Aliases)
                AddLookupEntry(lookup, alias, entry);
        }

        // Pass 2: BridgeCommand is a wire-level VS pipe address, not a
        // unique MCP dispatch key. Convenience wrapper tools may target the
        // same bridge command as a lower-level tool. Add the shortcut only
        // when it does not conflict with another tool's name, alias, or
        // earlier bridge-command shortcut. Silently dropping collisions keeps
        // the tool catalog usable instead of crashing the entire MCP server
        // and starving every client of every tool.
        foreach (ToolEntry entry in materialized)
        {
            string? bridgeCommand = entry.Definition.BridgeCommand;
            if (string.IsNullOrWhiteSpace(bridgeCommand))
                continue;
            if (lookup.ContainsKey(bridgeCommand))
                continue;
            lookup[bridgeCommand] = entry;
        }

        return lookup;
    }

    private static void AddLookupEntry(
        Dictionary<string, ToolEntry> lookup,
        string key,
        ToolEntry entry)
    {
        if (lookup.TryGetValue(key, out ToolEntry? existing))
        {
            if (ReferenceEquals(existing, entry))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Duplicate MCP tool lookup key '{key}' for '{existing.Name}' and '{entry.Name}'.");
        }

        lookup[key] = entry;
    }
}
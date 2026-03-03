using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static partial class CliApp
{
    private static readonly JsonSerializerOptions McpJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<int> RunMcpServerAsync(CliOptions options)
    {
        var selector = BridgeInstanceSelector.FromOptions(options);
        var discovery = await PipeDiscovery.SelectAsync(selector, options.GetFlag("verbose")).ConfigureAwait(false);
        await McpServer.RunAsync(discovery, options).ConfigureAwait(false);
        return 0;
    }

    private static sealed class McpServer
    {
        public static async Task RunAsync(PipeDiscovery discovery, CliOptions options)
        {
            while (true)
            {
                var request = await ReadMessageAsync(Console.OpenStandardInput()).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var response = await HandleRequestAsync(request, discovery, options).ConfigureAwait(false);
                if (response is not null)
                {
                    await WriteMessageAsync(Console.OpenStandardOutput(), response).ConfigureAwait(false);
                }
            }
        }

        private static async Task<JsonObject?> HandleRequestAsync(JsonObject request, PipeDiscovery discovery, CliOptions options)
        {
            var id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? string.Empty;
            var @params = request["params"] as JsonObject;

            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                        ["resources"] = new JsonObject(),
                        ["prompts"] = new JsonObject(),
                    },
                    ["serverInfo"] = new JsonObject { ["name"] = "vs-ide-bridge-mcp", ["version"] = "0.1.0" },
                },
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(@params, discovery, options).ConfigureAwait(false),
                "resources/list" => ListResources(),
                "resources/read" => await ReadResourceAsync(@params, discovery, options).ConfigureAwait(false),
                "prompts/list" => ListPrompts(),
                "prompts/get" => GetPrompt(@params),
                "notifications/initialized" => null!,
                _ => throw new CliException($"Unsupported MCP method: {method}"),
            };

            if (method == "notifications/initialized")
            {
                return null;
            }

            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private static JsonArray ListTools() => new()
        {
            Tool("state", "Capture current Visual Studio bridge state."),
            Tool("errors", "Get current errors."),
            Tool("warnings", "Get current warnings."),
            Tool("list_tabs", "List open editor tabs."),
            Tool("open_file", "Open a file path and optional line/column."),
            Tool("search_symbols", "Search solution symbols by query."),
            Tool("quick_info", "Get quick info at file/line/column."),
            Tool("apply_diff", "Apply unified diff through Visual Studio editor buffer."),
        };

        private static JsonObject Tool(string name, string description) => new() { ["name"] = name, ["description"] = description };

        private static async Task<JsonNode> CallToolAsync(JsonObject? p, PipeDiscovery discovery, CliOptions options)
        {
            var toolName = p?["name"]?.GetValue<string>() ?? throw new CliException("tools/call missing name.");
            var args = p?["arguments"] as JsonObject;
            var (command, commandArgs) = toolName switch
            {
                "state" => ("state", string.Empty),
                "errors" => ("errors", "--quick --wait-for-intellisense false"),
                "warnings" => ("warnings", "--quick --wait-for-intellisense false"),
                "list_tabs" => ("list-tabs", string.Empty),
                "open_file" => ("open-document", BuildArgs(("file", args?["file"]?.GetValue<string>()), ("line", args?["line"]?.ToString()), ("column", args?["column"]?.ToString()))),
                "search_symbols" => ("search-symbols", BuildArgs(("query", args?["query"]?.GetValue<string>()), ("kind", args?["kind"]?.GetValue<string>()))),
                "quick_info" => ("quick-info", BuildArgs(("file", args?["file"]?.GetValue<string>()), ("line", args?["line"]?.ToString()), ("column", args?["column"]?.ToString()))),
                "apply_diff" => ("apply-diff", BuildArgs(("patch-text-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(args?["patch"]?.GetValue<string>() ?? string.Empty))), ("open-changed-files", "true"))),
                _ => throw new CliException($"Unknown MCP tool: {toolName}"),
            };

            var response = await SendBridgeAsync(discovery, options, command, commandArgs).ConfigureAwait(false);
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = response.ToJsonString(JsonOptions),
                    },
                },
                ["isError"] = !(response["Success"]?.GetValue<bool>() ?? false),
            };
        }

        private static JsonArray ListResources() => new()
        {
            Resource("bridge://current-solution", "Current solution"),
            Resource("bridge://active-document", "Active document"),
            Resource("bridge://open-tabs", "Open tabs"),
            Resource("bridge://error-list-snapshot", "Error list snapshot"),
        };

        private static JsonObject Resource(string uri, string name) => new() { ["uri"] = uri, ["name"] = name };

        private static async Task<JsonNode> ReadResourceAsync(JsonObject? p, PipeDiscovery discovery, CliOptions options)
        {
            var uri = p?["uri"]?.GetValue<string>() ?? string.Empty;
            JsonObject data = uri switch
            {
                "bridge://current-solution" => await SendBridgeAsync(discovery, options, "state", string.Empty).ConfigureAwait(false),
                "bridge://active-document" => await SendBridgeAsync(discovery, options, "state", string.Empty).ConfigureAwait(false),
                "bridge://open-tabs" => await SendBridgeAsync(discovery, options, "list-tabs", string.Empty).ConfigureAwait(false),
                "bridge://error-list-snapshot" => await SendBridgeAsync(discovery, options, "errors", "--quick --wait-for-intellisense false").ConfigureAwait(false),
                _ => throw new CliException($"Unknown resource uri: {uri}"),
            };

            return new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = uri,
                        ["mimeType"] = "application/json",
                        ["text"] = data.ToJsonString(JsonOptions),
                    },
                },
            };
        }

        private static JsonArray ListPrompts() => new()
        {
            new JsonObject { ["name"] = "help", ["description"] = "Show bridge and MCP usage guidance." },
            new JsonObject { ["name"] = "fix_current_errors", ["description"] = "Gather errors and propose patch flow." },
            new JsonObject { ["name"] = "open_solution_and_wait_ready", ["description"] = "Run ensure then ready flow." },
        };

        private static JsonNode GetPrompt(JsonObject? p)
        {
            var name = p?["name"]?.GetValue<string>() ?? string.Empty;
            var text = name switch
            {
                "help" => "Use tools state, errors, warnings, list_tabs, open_file, search_symbols, quick_info, and apply_diff.",
                "fix_current_errors" => "Call errors, inspect rows, then use open_file, quick_info, search_symbols, and apply_diff.",
                "open_solution_and_wait_ready" => "Outside MCP, run: vs-ide-bridge ensure --solution <path>; then call state until ready.",
                _ => throw new CliException($"Unknown prompt: {name}"),
            };

            return new JsonObject
            {
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
                    },
                },
            };
        }

        private static async Task<JsonObject> SendBridgeAsync(PipeDiscovery discovery, CliOptions options, string command, string args)
        {
            await using var client = new PipeClient(discovery.PipeName, options.GetInt32("timeout-ms", 10_000));
            var request = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N")[..8],
                ["command"] = command,
                ["args"] = args,
            };

            return await client.SendAsync(request).ConfigureAwait(false);
        }

        private static string BuildArgs(params (string Name, string? Value)[] items)
        {
            var builder = new PipeArgsBuilder();
            foreach (var (name, value) in items)
            {
                builder.Add(name, value);
            }

            return builder.Build();
        }

        private static async Task<JsonObject?> ReadMessageAsync(Stream input)
        {
            var header = await ReadHeaderAsync(input).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(header))
            {
                return null;
            }

            var lengthLine = header.Split('\n').FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (lengthLine is null)
            {
                throw new CliException("MCP request missing Content-Length header.");
            }

            var length = int.Parse(lengthLine.Split(':', 2)[1].Trim());
            var payloadBytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await input.ReadAsync(payloadBytes.AsMemory(offset, length - offset)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new CliException("Unexpected EOF while reading MCP payload.");
                }

                offset += read;
            }

            var json = Encoding.UTF8.GetString(payloadBytes);
            return JsonNode.Parse(json) as JsonObject;
        }

        private static async Task<string> ReadHeaderAsync(Stream input)
        {
            var bytes = new List<byte>();
            var lastFour = new Queue<byte>(4);
            while (true)
            {
                var b = new byte[1];
                var read = await input.ReadAsync(b, 0, 1).ConfigureAwait(false);
                if (read == 0)
                {
                    return string.Empty;
                }

                bytes.Add(b[0]);
                lastFour.Enqueue(b[0]);
                if (lastFour.Count > 4)
                {
                    lastFour.Dequeue();
                }

                if (lastFour.Count == 4 && lastFour.SequenceEqual(new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' }))
                {
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
            }
        }

        private static async Task WriteMessageAsync(Stream output, JsonObject response)
        {
            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString(McpJsonOptions));
            var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
            await output.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await output.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
    }
}

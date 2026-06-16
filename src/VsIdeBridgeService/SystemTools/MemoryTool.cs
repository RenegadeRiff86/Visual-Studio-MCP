using System.Globalization;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService.SystemTools;

internal static class MemoryTool
{
    private const int DefaultSearchResults = 25;
    private const int MaxSearchResults = 100;
    private const int DefaultSearchContextLines = 1;
    private const int MaxSearchContextLines = 5;
    private const int DefaultReadLines = 120;
    private const int MaxReadLines = 250;
    private const int DefaultCenteredContextLines = 20;
    private const int MaxCenteredContextLines = 100;
    private const long MaxSearchFileBytes = 5_000_000;

    public static Task<JsonNode> SearchAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string query = args?["query"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing required argument 'query'.");
        }

        int maxResults = Clamp(args?["max_results"]?.GetValue<int?>() ?? DefaultSearchResults, 1, MaxSearchResults);
        int contextLines = Clamp(args?["context_lines"]?.GetValue<int?>() ?? DefaultSearchContextLines, 0, MaxSearchContextLines);
        bool includeRollouts = args?["include_rollouts"]?.GetValue<bool?>() ?? true;
        string memoryRoot = ResolveMemoryRoot(id, bridge);

        JsonArray results = [];
        int searchedFiles = 0;
        bool truncated = false;

        foreach (string file in EnumerateMemoryFiles(memoryRoot, includeRollouts))
        {
            if (results.Count >= maxResults)
            {
                truncated = true;
                break;
            }

            searchedFiles++;
            if (ShouldSkipSearchFile(file))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            for (int index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(BuildSearchHit(memoryRoot, file, index, lines, contextLines));
                if (results.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }
            }
        }

        JsonObject payload = new()
        {
            ["success"] = true,
            ["memoryRoot"] = memoryRoot,
            ["query"] = query,
            ["maxResults"] = maxResults,
            ["contextLines"] = contextLines,
            ["includeRollouts"] = includeRollouts,
            ["searchedFiles"] = searchedFiles,
            ["count"] = results.Count,
            ["truncated"] = truncated,
            ["results"] = results,
        };

        string successText = $"Found {results.Count} memory match(es) across {searchedFiles} file(s).";
        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(payload, args, successText: successText));
    }

    public static Task<JsonNode> ReadAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string memoryRoot = ResolveMemoryRoot(id, bridge);
        string relativePath = args?["path"]?.GetValue<string>() ?? "MEMORY.md";
        string fullPath = ResolveMemoryPath(id, memoryRoot, relativePath);

        int? centerLine = args?["line"]?.GetValue<int?>();
        int startLine;
        int endLine;
        if (centerLine is not null)
        {
            if (centerLine <= 0)
            {
                throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Argument 'line' must be greater than zero.");
            }

            int contextLines = Clamp(args?["context_lines"]?.GetValue<int?>() ?? DefaultCenteredContextLines, 0, MaxCenteredContextLines);
            startLine = Math.Max(1, centerLine.Value - contextLines);
            endLine = centerLine.Value + contextLines;
        }
        else
        {
            startLine = Math.Max(1, args?["start_line"]?.GetValue<int?>() ?? 1);
            endLine = args?["end_line"]?.GetValue<int?>() ?? (startLine + DefaultReadLines - 1);
        }

        if (endLine < startLine)
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Argument 'end_line' must be greater than or equal to 'start_line'.");
        }

        if (endLine - startLine + 1 > MaxReadLines)
        {
            endLine = startLine + MaxReadLines - 1;
        }

        List<string> selected = [];
        int currentLine = 0;
        foreach (string line in File.ReadLines(fullPath))
        {
            currentLine++;
            if (currentLine < startLine)
            {
                continue;
            }

            if (currentLine > endLine)
            {
                break;
            }

            selected.Add($"{currentLine.ToString(CultureInfo.InvariantCulture)}: {line}");
        }

        JsonObject payload = new()
        {
            ["success"] = true,
            ["memoryRoot"] = memoryRoot,
            ["path"] = ToMemoryRelativePath(memoryRoot, fullPath),
            ["startLine"] = startLine,
            ["endLine"] = startLine + Math.Max(0, selected.Count - 1),
            ["lineCount"] = selected.Count,
            ["text"] = string.Join(Environment.NewLine, selected),
        };

        string successText = $"Read {selected.Count} memory line(s) from {payload["path"]!.GetValue<string>()}.";
        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(payload, args, successText: successText));
    }

    private static JsonObject BuildSearchHit(string root, string file, int lineIndex, string[] lines, int contextLines)
    {
        int lineNumber = lineIndex + 1;
        int start = Math.Max(0, lineIndex - contextLines);
        int end = Math.Min(lines.Length - 1, lineIndex + contextLines);
        JsonArray context = [];
        for (int index = start; index <= end; index++)
        {
            context.Add(new JsonObject
            {
                ["line"] = index + 1,
                ["text"] = Truncate(lines[index], 500),
            });
        }

        return new JsonObject
        {
            ["path"] = ToMemoryRelativePath(root, file),
            ["line"] = lineNumber,
            ["preview"] = Truncate(lines[lineIndex], 500),
            ["context"] = context,
        };
    }

    private static IEnumerable<string> EnumerateMemoryFiles(string root, bool includeRollouts)
    {
        string[] priorityFiles = ["memory_summary.md", "MEMORY.md"];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string fileName in priorityFiles)
        {
            string fullPath = Path.Combine(root, fileName);
            if (File.Exists(fullPath) && seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }

        foreach (string file in EnumerateFilesSafe(Path.Combine(root, "extensions"), "*.md", SearchOption.AllDirectories))
        {
            if (seen.Add(file))
            {
                yield return file;
            }
        }

        foreach (string file in EnumerateFilesSafe(Path.Combine(root, "skills"), "*.md", SearchOption.AllDirectories))
        {
            if (seen.Add(file))
            {
                yield return file;
            }
        }

        if (!includeRollouts)
        {
            yield break;
        }

        string rolloutDir = Path.Combine(root, "rollout_summaries");
        foreach (string file in EnumerateFilesSafe(rolloutDir, "*.md", SearchOption.TopDirectoryOnly)
            .Concat(EnumerateFilesSafe(rolloutDir, "*.jsonl", SearchOption.TopDirectoryOnly)))
        {
            if (seen.Add(file))
            {
                yield return file;
            }
        }
    }

    private static string[] EnumerateFilesSafe(string directory, string pattern, SearchOption searchOption)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(directory, pattern, searchOption)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool ShouldSkipSearchFile(string file)
    {
        try
        {
            return new FileInfo(file).Length > MaxSearchFileBytes;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string ResolveMemoryRoot(JsonNode? id, BridgeConnection bridge)
    {
        List<string> candidates = [];
        AddMemoryRootCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_HOME"));
        AddHomeCandidate(candidates, Environment.GetEnvironmentVariable("USERPROFILE"));
        AddHomeCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddHomeCandidate(candidates, InferUserHomeFromSolution(bridge));

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        string tried = string.Join(", ", candidates.Distinct(StringComparer.OrdinalIgnoreCase));
        throw new McpRequestException(id, McpErrorCodes.InvalidParams,
            $"Codex memory root was not found. Tried: {tried}");
    }

    private static void AddMemoryRootCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        if (string.Equals(Path.GetFileName(fullPath), "memories", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(fullPath);
        }
        else
        {
            candidates.Add(Path.Combine(fullPath, "memories"));
        }
    }

    private static void AddHomeCandidate(List<string> candidates, string? home)
    {
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, ".codex", "memories"));
        }
    }

    private static string? InferUserHomeFromSolution(BridgeConnection bridge)
    {
        try
        {
            DirectoryInfo? directory = new(ServiceToolPaths.ResolveSolutionDirectory(bridge));
            while (directory is not null)
            {
                if (string.Equals(directory.Parent?.Name, "Users", StringComparison.OrdinalIgnoreCase))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string ResolveMemoryPath(JsonNode? id, string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = "MEMORY.md";
        }

        string fullPath = Path.GetFullPath(Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootWithSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                "Memory path must stay under the resolved Codex memory root.");
        }

        if (!File.Exists(fullPath))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"Memory file not found: {ToMemoryRelativePath(root, fullPath)}");
        }

        return fullPath;
    }

    private static string ToMemoryRelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static int Clamp(int value, int min, int max)
        => Math.Min(max, Math.Max(min, value));

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";
}

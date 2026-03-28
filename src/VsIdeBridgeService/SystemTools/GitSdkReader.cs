using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using LibGit2Sharp;

namespace VsIdeBridgeService.SystemTools;

internal static class GitSdkReader
{
    public static Task<JsonNode> GetCurrentBranchAsync(JsonNode? id, string repoDirectory)
    {
        using Repository repo = OpenRepository(id, repoDirectory);
        Branch head = repo.Head;
        bool isDetached = repo.Info.IsHeadDetached;

        JsonObject payload = new()
        {
            ["success"] = true,
            ["branch"] = isDetached ? string.Empty : head.FriendlyName,
            ["isDetached"] = isDetached,
            ["headSha"] = head.Tip?.Sha,
            ["repositoryPath"] = repo.Info.WorkingDirectory,
        };

        string successText = isDetached
            ? $"Repository is in detached HEAD at {ShortSha(head.Tip?.Sha)}."
            : $"Current branch: {head.FriendlyName}";

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(payload, successText: successText));
    }

    public static Task<JsonNode> GetBranchListAsync(JsonNode? id, string repoDirectory)
    {
        using Repository repo = OpenRepository(id, repoDirectory);

        JsonArray branches = new();
        foreach (Branch branch in repo.Branches
                     .OrderBy(static b => b.IsRemote)
                     .ThenBy(static b => b.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            branches.Add(new JsonObject
            {
                ["name"] = branch.FriendlyName,
                ["canonicalName"] = branch.CanonicalName,
                ["isRemote"] = branch.IsRemote,
                ["isCurrent"] = branch.IsCurrentRepositoryHead,
                ["sha"] = branch.Tip?.Sha,
                ["trackedBranch"] = branch.TrackedBranch?.FriendlyName,
            });
        }

        JsonObject payload = new()
        {
            ["success"] = true,
            ["count"] = branches.Count,
            ["branches"] = branches,
            ["repositoryPath"] = repo.Info.WorkingDirectory,
        };

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(
            payload,
            successText: $"Listed {branches.Count} branches."));
    }

    public static Task<JsonNode> GetLogAsync(JsonNode? id, string repoDirectory, int maxCount)
    {
        using Repository repo = OpenRepository(id, repoDirectory);

        JsonArray commits = new();
        foreach (Commit commit in repo.Commits.Take(Math.Max(1, maxCount)))
        {
            commits.Add(new JsonObject
            {
                ["sha"] = commit.Sha,
                ["shortSha"] = ShortSha(commit.Sha),
                ["authorName"] = commit.Author.Name,
                ["authorEmail"] = commit.Author.Email,
                ["authorWhen"] = commit.Author.When.ToString("O", CultureInfo.InvariantCulture),
                ["messageShort"] = commit.MessageShort,
                ["message"] = commit.Message,
            });
        }

        JsonObject payload = new()
        {
            ["success"] = true,
            ["count"] = commits.Count,
            ["commits"] = commits,
            ["repositoryPath"] = repo.Info.WorkingDirectory,
        };

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(
            payload,
            successText: $"Listed {commits.Count} commits."));
    }

    public static Task<JsonNode> GetRemoteListAsync(JsonNode? id, string repoDirectory)
    {
        using Repository repo = OpenRepository(id, repoDirectory);

        JsonArray remotes = new();
        foreach (Remote remote in repo.Network.Remotes.OrderBy(static r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            remotes.Add(new JsonObject
            {
                ["name"] = remote.Name,
                ["fetchUrl"] = remote.Url,
                ["pushUrl"] = remote.PushUrl,
            });
        }

        JsonObject payload = new()
        {
            ["success"] = true,
            ["count"] = remotes.Count,
            ["remotes"] = remotes,
            ["repositoryPath"] = repo.Info.WorkingDirectory,
        };

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(
            payload,
            successText: $"Listed {remotes.Count} remotes."));
    }

    private static Repository OpenRepository(JsonNode? id, string repoDirectory)
    {
        string? repoPath = Repository.Discover(repoDirectory);
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"No Git repository was found under '{repoDirectory}'.");
        }

        try
        {
            return new Repository(repoPath);
        }
        catch (Exception ex)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to open repository at '{repoPath}': {ex.Message}");
        }
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrWhiteSpace(sha)
            ? string.Empty
            : sha.Length <= 7 ? sha : sha[..7];
}

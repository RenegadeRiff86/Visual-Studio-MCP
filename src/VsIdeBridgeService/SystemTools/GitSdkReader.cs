using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using LibGit2Sharp;

namespace VsIdeBridgeService.SystemTools;

internal static class GitSdkReader
{
    private const string RepositoryPathProperty = "repositoryPath";

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
            [RepositoryPathProperty] = repo.Info.WorkingDirectory,
        };

        string successText = isDetached
            ? $"Repository is in detached HEAD at {ShortSha(head.Tip?.Sha)}."
            : $"Current branch: {head.FriendlyName}";

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(payload, successText: successText));
    }

    public static Task<JsonNode> GetStatusAsync(JsonNode? id, string repoDirectory)
    {
        using Repository repo = OpenRepository(id, repoDirectory);
        RepositoryStatus status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeIgnored = false,
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });

        JsonArray entries = [];
        List<string> lines = [BuildBranchStatusLine(repo)];
        foreach (StatusEntry entry in status
                     .Where(static entry => ShouldIncludeStatus(entry.State))
                     .OrderBy(static entry => entry.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            string statusCode = GetPorcelainStatusCode(entry.State);
            string path = NormalizeGitPath(entry.FilePath);
            string line = $"{statusCode} {path}";
            lines.Add(line);
            entries.Add(new JsonObject
            {
                ["path"] = path,
                ["status"] = statusCode,
                ["state"] = entry.State.ToString(),
            });
        }

        string stdout = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        JsonObject payload = new()
        {
            ["success"] = true,
            ["exitCode"] = 0,
            ["stdout"] = stdout,
            ["stderr"] = string.Empty,
            [RepositoryPathProperty] = repo.Info.WorkingDirectory,
            ["count"] = entries.Count,
            ["entries"] = entries,
        };

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(
            payload,
            successText: $"Git status completed with {entries.Count} changed path(s)."));
    }

    public static Task<JsonNode> GetUnstagedDiffAsync(JsonNode? id, string repoDirectory, int contextLines)
    {
        using Repository repo = OpenRepository(id, repoDirectory);
        string[] changedPaths =
        [
            .. repo.RetrieveStatus(new StatusOptions
                {
                    IncludeIgnored = false,
                    IncludeUntracked = false,
                    RecurseUntrackedDirs = false,
                })
                .Where(static entry => HasUnstagedChange(entry.State))
                .Select(static entry => entry.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase),
        ];

        JsonArray entries = [];
        string stdout = string.Empty;
        int linesAdded = 0;
        int linesDeleted = 0;
        if (changedPaths.Length > 0)
        {
            LibGit2Sharp.CompareOptions options = new()
            {
                ContextLines = Math.Max(0, contextLines),
            };

            using Patch patch = repo.Diff.Compare<Patch>(
                changedPaths,
                includeUntracked: false,
                explicitPathsOptions: new ExplicitPathsOptions(),
                compareOptions: options);

            foreach (PatchEntryChanges entry in patch
                         .OrderBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase))
            {
                JsonObject item = new()
                {
                    ["path"] = NormalizeGitPath(entry.Path),
                    ["status"] = entry.Status.ToString(),
                };

                if (!string.IsNullOrWhiteSpace(entry.OldPath)
                    && !string.Equals(entry.OldPath, entry.Path, StringComparison.Ordinal))
                {
                    item["oldPath"] = NormalizeGitPath(entry.OldPath);
                }

                entries.Add(item);
            }

            stdout = patch.Content ?? string.Empty;
            linesAdded = patch.LinesAdded;
            linesDeleted = patch.LinesDeleted;
        }

        JsonObject payload = new()
        {
            ["success"] = true,
            ["exitCode"] = 0,
            ["stdout"] = stdout,
            ["stderr"] = string.Empty,
            [RepositoryPathProperty] = repo.Info.WorkingDirectory,
            ["filesChanged"] = entries.Count,
            ["linesAdded"] = linesAdded,
            ["linesDeleted"] = linesDeleted,
            ["entries"] = entries,
        };

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(
            payload,
            successText: $"Git unstaged diff completed with {entries.Count} changed file(s)."));
    }

    public static Task<JsonNode> GetBranchListAsync(JsonNode? id, string repoDirectory)
    {
        using Repository repo = OpenRepository(id, repoDirectory);

        JsonArray branches = [];
        foreach (Branch branch in repo.Branches
                     .OrderBy(static b => b.IsRemote)
                     .ThenBy(static b => b.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            string? trackedBranch = TryGetTrackedBranchFriendlyName(branch, out string? trackingError);
            JsonObject branchPayload = new()
            {
                ["name"] = branch.FriendlyName,
                ["canonicalName"] = branch.CanonicalName,
                ["isRemote"] = branch.IsRemote,
                ["isCurrent"] = branch.IsCurrentRepositoryHead,
                ["sha"] = branch.Tip?.Sha,
                ["trackedBranch"] = trackedBranch,
            };

            if (!string.IsNullOrWhiteSpace(trackingError))
            {
                branchPayload["trackingError"] = trackingError;
            }

            branches.Add(branchPayload);
        }

        JsonObject payload = new()
        {
            ["success"] = true,
            ["count"] = branches.Count,
            ["branches"] = branches,
            [RepositoryPathProperty] = repo.Info.WorkingDirectory,
        };

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(
            payload,
            successText: $"Listed {branches.Count} branches."));
    }

    private static string? TryGetTrackedBranchFriendlyName(Branch branch, out string? error)
    {
        try
        {
            error = null;
            return branch.TrackedBranch?.FriendlyName;
        }
        catch (Exception ex) when (ex is LibGit2SharpException or ArgumentException)
        {
            error = ex.Message;
            return null;
        }
    }

    public static Task<JsonNode> GetLogAsync(JsonNode? id, string repoDirectory, int maxCount)
    {
        using Repository repo = OpenRepository(id, repoDirectory);

        JsonArray commits = [];
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
            [RepositoryPathProperty] = repo.Info.WorkingDirectory,
        };

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(
            payload,
            successText: $"Listed {commits.Count} commits."));
    }

    public static Task<JsonNode> GetRemoteListAsync(JsonNode? id, string repoDirectory)
    {
        using Repository repo = OpenRepository(id, repoDirectory);

        JsonArray remotes = [];
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
            [RepositoryPathProperty] = repo.Info.WorkingDirectory,
        };

        return Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(
            payload,
            successText: $"Listed {remotes.Count} remotes."));
    }

    private static string BuildBranchStatusLine(Repository repo)
    {
        if (repo.Info.IsHeadDetached)
        {
            return "## HEAD (no branch)";
        }

        Branch head = repo.Head;
        string branchName = string.IsNullOrWhiteSpace(head.FriendlyName) ? "HEAD" : head.FriendlyName;
        Branch? trackedBranch = head.TrackedBranch;
        return trackedBranch is null
            ? $"## {branchName}"
            : $"## {branchName}...{trackedBranch.FriendlyName}{BuildTrackingSuffix(head.TrackingDetails)}";
    }

    private static string BuildTrackingSuffix(BranchTrackingDetails details)
    {
        int aheadBy = details.AheadBy.GetValueOrDefault();
        int behindBy = details.BehindBy.GetValueOrDefault();
        List<string> parts = [];
        if (aheadBy > 0)
        {
            parts.Add($"ahead {aheadBy}");
        }

        if (behindBy > 0)
        {
            parts.Add($"behind {behindBy}");
        }

        return parts.Count == 0 ? string.Empty : $" [{string.Join(", ", parts)}]";
    }

    private static bool ShouldIncludeStatus(FileStatus state)
        => state != FileStatus.Unaltered
            && state != FileStatus.Nonexistent
            && (state & FileStatus.Ignored) == 0;

    private static bool HasUnstagedChange(FileStatus state)
        => (state & FileStatus.ModifiedInWorkdir) != 0
            || (state & FileStatus.DeletedFromWorkdir) != 0
            || (state & FileStatus.RenamedInWorkdir) != 0
            || (state & FileStatus.TypeChangeInWorkdir) != 0
            || (state & FileStatus.Unreadable) != 0
            || (state & FileStatus.Conflicted) != 0;

    private static string GetPorcelainStatusCode(FileStatus state)
    {
        if ((state & FileStatus.Ignored) != 0)
        {
            return "!!";
        }

        if ((state & FileStatus.Conflicted) != 0)
        {
            return "UU";
        }

        char indexStatus = GetIndexStatusCode(state);
        if ((state & FileStatus.NewInWorkdir) != 0 && indexStatus == ' ')
        {
            return "??";
        }

        return new string([indexStatus, GetWorkTreeStatusCode(state)]);
    }

    private static char GetIndexStatusCode(FileStatus state)
    {
        if ((state & FileStatus.NewInIndex) != 0)
            return 'A';
        if ((state & FileStatus.ModifiedInIndex) != 0)
            return 'M';
        if ((state & FileStatus.DeletedFromIndex) != 0)
            return 'D';
        if ((state & FileStatus.RenamedInIndex) != 0)
            return 'R';
        if ((state & FileStatus.TypeChangeInIndex) != 0)
            return 'T';

        return ' ';
    }

    private static char GetWorkTreeStatusCode(FileStatus state)
    {
        if ((state & FileStatus.ModifiedInWorkdir) != 0)
            return 'M';
        if ((state & FileStatus.DeletedFromWorkdir) != 0)
            return 'D';
        if ((state & FileStatus.RenamedInWorkdir) != 0)
            return 'R';
        if ((state & FileStatus.TypeChangeInWorkdir) != 0)
            return 'T';
        if ((state & FileStatus.Unreadable) != 0)
            return '?';

        return ' ';
    }

    private static string NormalizeGitPath(string path)
        => path.Replace('\\', '/');

    private static Repository OpenRepository(JsonNode? id, string repoDirectory)
    {
        string? repoPath = Repository.Discover(repoDirectory);
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"No Git repository was found at '{repoDirectory}'. Ensure the solution directory is committed to a Git repository.");
        }

        Repository repo;
        try
        {
            repo = new Repository(repoPath);
        }
        catch (Exception ex) when (ex is not null) // re-throw as MCP error; LibGit2Sharp throws various types
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to open repository at '{repoPath}': {ex.Message}");
        }

        // Safety check: Repository.Discover walks up the directory tree and may return an
        // ancestor repo (a parent mega-repo).  If the working directory doesn't match what
        // the caller expected, dispose and throw a clear error rather than silently operating
        // on the wrong repository.
        string foundRoot = repo.Info.WorkingDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string expectedRoot = repoDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(foundRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            repo.Dispose();
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"No Git repository found at '{expectedRoot}'. " +
                $"The nearest repository is at '{foundRoot}', but the solution directory does not appear to have any committed files there. " +
                $"Ensure the solution files are committed to a Git repository.");
        }

        return repo;
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrWhiteSpace(sha)
            ? string.Empty
            : sha.Length <= 7 ? sha : sha[..7];
}

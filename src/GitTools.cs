using System.Text.Json.Nodes;
using VsIdeBridgeService.SystemTools;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const int DefaultDiffContext = 3;
    private const string GitStatusTool = "git_status";
    private const string GitDiffStagedTool = "git_diff_staged";
    private const string GitDiffUnstagedTool = "git_diff_unstaged";
    private const string GitCommitTool = "git_commit";
    private const string GitCheckoutTool = "git_checkout";
    private const string GitPullTool = "git_pull";
    private const string GitPushTool = "git_push";
    private const string GitFetchTool = "git_fetch";
    private const string GitBranchListTool = "git_branch_list";
    private const string GitLogRangeTool = "git_log_range";
    private const string GitCompareRefsTool = "git_compare_refs";
    private const string GitDiffRangeTool = "git_diff_range";
    private const string GitFileHistoryTool = "git_file_history";
    private const string GitBlameTool = "git_blame";
    private const string GitMergeBaseTool = "git_merge_base";
    private const string GitCherryTool = "git_cherry";
    private const string GitConflictsTool = "git_conflicts";
    private const string GitShowTool = "git_show";
    private const string MaxCountArg = "max_count";
    private const string GitNoColorArg = "--no-color";
    private const string GitRebaseTool = "git_rebase";
    private const string GitRebaseContinueTool = "git_rebase_continue";
    private const string GitRebaseAbortTool = "git_rebase_abort";
    private const string GitRebaseSkipTool = "git_rebase_skip";
    private const string PathSingleAliasDesc = "Single file path — shorthand for paths:[\"...\"].";

    private static IEnumerable<ToolEntry> GitTools()
        =>
        GitStatusAndHistoryTools()
            .Concat(GitFileInspectionTools())
            .Concat(GitRangeHistoryTools())
            .Concat(GitDiffAndMetaTools())
            .Concat(GitRangeDiffTools())
            .Concat(GitStagingCommitTools())
            .Concat(GitBranchTools())
            .Concat(GitNetworkTools())
            .Concat(GitMergeTools())
            .Concat(GitMergeInspectionTools())
            .Concat(GitRebaseTools())
            .Concat(GitStashTools());

    private static IEnumerable<ToolEntry> GitStatusAndHistoryTools()
    {
        yield return new(GitStatusTool,
            "Show working-tree status (porcelain v1 + branch). Use this before committing " +
            "to see modified, staged, and untracked files.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitSdkReader.GetStatusAsync(id, repo)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("git_add", "Stage modified files"), (GitCommitTool, "Commit staged changes")],
                related: [(GitDiffUnstagedTool, "See unstaged change details"), (GitDiffStagedTool, "See staged change details")]));

        yield return new("git_current_branch",
            "Return the name of the currently checked-out branch.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitSdkReader.GetCurrentBranchAsync(id, repo)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [(GitBranchListTool, "List all branches"), (GitCheckoutTool, "Switch to another branch"), ("git_create_branch", "Create a new branch")]));

        yield return new(GitBranchListTool,
            "List all local and remote branches with full SHAs.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitSdkReader.GetBranchListAsync(id, repo)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCheckoutTool, "Switch to a listed branch")],
                related: [("git_current_branch", "Get only the active branch"), ("git_create_branch", "Create a new branch")]));

        yield return new("git_log",
            "Show the commit history. Defaults to the last 20 commits in ISO date format. " +
            "Pass path to limit history to commits that touched a specific file or directory.",
            ObjectSchema(
                OptInt(MaxCountArg, "Max number of commits to show (default 20)."),
                Opt("path", "Optional file or directory path; limits results to commits that touched it (equivalent to git log -- <path>).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int max = args?[MaxCountArg]?.GetValue<int?>() ?? 20;
                string? path = args?["path"]?.GetValue<string>();
                return await GitSdkReader.GetLogAsync(id, repo, max, path).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitShowTool, "Inspect a specific commit from the log")],
                related: [(GitDiffUnstagedTool, "See uncommitted changes"), (GitDiffStagedTool, "See staged changes")]));

        yield return new(GitShowTool,
            "Show the diff and metadata for a single commit, tag, or tree-ish revision.",
            ObjectSchema(Req("revision", "Commit SHA, tag, or branch name to show.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string rev = RequiredString(id, args, "revision");
                return await GitRunner.RunArgumentsAsync(id, repo, ["show", GitNoColorArg, rev])
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [(GitLogRangeTool, "Browse commits in a specific revision range"), (GitDiffUnstagedTool, "See current uncommitted changes")]));
    }

    private static IEnumerable<ToolEntry> GitFileInspectionTools()
    {
        yield return new(GitFileHistoryTool,
            "Show commit history for one file or directory. Defaults to following renames for single-file history.",
            ObjectSchema(
                Req("path", "File or directory path to inspect."),
                OptInt(MaxCountArg, "Max number of commits to show (default 50, max 500)."),
                OptBool("follow", "Follow file renames with --follow (default true).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string path = RequiredString(id, args, "path");
                int max = Math.Clamp(args?[MaxCountArg]?.GetValue<int?>() ?? 50, 1, 500);
                bool follow = args?["follow"]?.GetValue<bool?>() ?? true;
                List<string> gitArgs = ["log", GitNoColorArg, "--date=iso", "--pretty=format:%H%x09%ad%x09%an%x09%s", $"--max-count={max}"];
                if (follow)
                    gitArgs.Add("--follow");
                gitArgs.Add("--");
                gitArgs.Add(path);
                return await GitRunner.RunArgumentsAsync(id, repo, gitArgs).ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitShowTool, "Inspect a commit that touched the file"), (GitBlameTool, "See line-by-line authorship")],
                related: [("git_log", "Browse general commit history"), (GitDiffRangeTool, "Inspect changes between refs")]));

        yield return new(GitBlameTool,
            "Show line-by-line author and commit information for a file. Pass start_line and end_line together to bound output.",
            ObjectSchema(
                Req("path", "File path to blame."),
                Opt("revision", "Optional revision to blame from (default HEAD)."),
                OptInt("start_line", "First 1-based line to include. Must be paired with end_line."),
                OptInt("end_line", "Last 1-based line to include. Must be paired with start_line.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string path = RequiredString(id, args, "path");
                int? startLine = args?["start_line"]?.GetValue<int?>();
                int? endLine = args?["end_line"]?.GetValue<int?>();
                if (startLine.HasValue != endLine.HasValue)
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams, "start_line and end_line must be provided together.");
                if (startLine is <= 0 || endLine is <= 0 || (startLine.HasValue && endLine < startLine))
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Blame line range must be positive and end_line must be >= start_line.");

                List<string> gitArgs = ["blame", "--date=iso"];
                if (startLine.HasValue)
                {
                    gitArgs.Add("-L");
                    gitArgs.Add($"{startLine},{endLine}");
                }

                string? revision = args?["revision"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(revision))
                    gitArgs.Add(revision);
                gitArgs.Add("--");
                gitArgs.Add(path);
                return await GitRunner.RunArgumentsAsync(id, repo, gitArgs).ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitFileHistoryTool, "Browse commits that changed this file"), (GitShowTool, "Inspect the blamed commit")],
                related: [("git_log", "Browse commit history"), (GitDiffUnstagedTool, "See current file changes")]));
    }

    private static IEnumerable<ToolEntry> GitRangeHistoryTools()
    {
        yield return new(GitLogRangeTool,
            "Show commits reachable from a revision range such as base..head or base...head.",
            ObjectSchema(
                Req("range", "Git revision range, for example upstream/master_27..HEAD or base...head."),
                OptInt(MaxCountArg, "Max number of commits to show (default 50)."),
                Opt("path", "Optional file or directory path; limits results to commits that touched it.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string range = RequiredString(id, args, "range");
                int max = Math.Max(1, args?[MaxCountArg]?.GetValue<int?>() ?? 50);
                string? path = args?["path"]?.GetValue<string>();
                List<string> gitArgs = ["log", GitNoColorArg, "--date=iso", "--pretty=format:%H%x09%ad%x09%an%x09%s", $"--max-count={max}", range];
                if (!string.IsNullOrWhiteSpace(path))
                {
                    gitArgs.Add("--");
                    gitArgs.Add(path);
                }

                return await GitRunner.RunArgumentsAsync(id, repo, gitArgs).ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitShowTool, "Inspect a specific commit from the range")],
                related: [(GitCompareRefsTool, "Count ahead and behind commits between refs"), (GitDiffRangeTool, "Inspect file changes between refs")]));

        yield return new(GitCompareRefsTool,
            "Count commits unique to each side of two refs using git rev-list --left-right --count base...head.",
            ObjectSchema(
                Req("base", "Base ref, usually the target branch or upstream ref."),
                Opt("head", "Head ref to compare against base (default HEAD).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string baseRef = RequiredString(id, args, "base");
                string headRef = args?["head"]?.GetValue<string>() ?? "HEAD";
                return await GitRunner.RunArgumentsAsync(id, repo,
                    ["rev-list", "--left-right", "--count", $"{baseRef}...{headRef}"])
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitLogRangeTool, "List commits on either side after comparing refs")],
                related: [(GitDiffRangeTool, "Inspect changed files between refs"), (GitRebaseTool, "Rebase the current branch onto a target ref")]));
    }

    private static IEnumerable<ToolEntry> GitDiffAndMetaTools()
    {
        yield return new(GitDiffUnstagedTool,
            "Show unstaged changes in the working tree (not yet git-added). " +
            "Pass paths or path to scope the diff to specific files.",
            ObjectSchema(
                OptInt("context", "Lines of context around each hunk (default 3)."),
                OptArr(Paths, "Optional list of file paths to scope the diff, e.g. [\"src/Foo.cs\"]."),
                Opt("path", PathSingleAliasDesc)),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int ctx = args?["context"]?.GetValue<int?>() ?? DefaultDiffContext;
                IEnumerable<string>? paths = GetEffectivePathListOrNull(args);
                return await GitSdkReader.GetUnstagedDiffAsync(id, repo, ctx, paths)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("git_add", "Stage the changes after reviewing"), (GitCommitTool, "Commit after staging")],
                related: [(GitDiffStagedTool, "See already-staged changes"), (GitStatusTool, "See which files changed")]));

        yield return new(GitDiffStagedTool,
            "Show staged changes ready to commit (git-added but not yet committed). " +
            "Pass paths or path to scope the diff to specific files.",
            ObjectSchema(
                OptInt("context", "Lines of context around each hunk (default 3)."),
                OptArr(Paths, "Optional list of file paths to scope the diff, e.g. [\"src/Foo.cs\"]."),
                Opt("path", PathSingleAliasDesc)),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int ctx = args?["context"]?.GetValue<int?>() ?? DefaultDiffContext;
                List<string> gitArgs = ["diff", "--cached", GitNoColorArg, $"--unified={ctx}"];
                IEnumerable<string>? paths = GetEffectivePathListOrNull(args);
                if (paths is not null) { gitArgs.Add("--"); gitArgs.AddRange(paths); }
                return await GitRunner.RunArgumentsAsync(id, repo, gitArgs)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCommitTool, "Commit the staged changes")],
                related: [(GitDiffUnstagedTool, "See unstaged changes"), (GitStatusTool, "See overall working-tree state")]));

        yield return new("git_remote_list",
            "List configured remotes with their fetch and push URLs.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitSdkReader.GetRemoteListAsync(id, repo).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [(GitFetchTool, "Fetch from a remote"), (GitPushTool, "Push to a remote"), (GitPullTool, "Pull from a remote")]));

        yield return new("git_tag_list",
            "List tags sorted by version (newest first).",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunArgumentsAsync(id, repo, ["tag", "--list", "--sort=-version:refname"])
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [("git_log", "Browse commit history"), (GitShowTool, "Inspect a tag's commit")]));

    }

    private static IEnumerable<ToolEntry> GitRangeDiffTools()
    {
        yield return new(GitDiffRangeTool,
            "Show file changes between two refs, with patch, --stat, or --name-status output.",
            ObjectSchema(
                Req("base", "Base ref, usually the target branch or upstream ref."),
                Opt("head", "Head ref to compare against base (default HEAD)."),
                OptInt("context", "Lines of context around each patch hunk (default 3)."),
                OptBool("stat", "Include --stat output."),
                OptBool("name_status", "Include --name-status output."),
                OptArr(Paths, "Optional list of file paths to scope the diff."),
                Opt("path", PathSingleAliasDesc)),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string baseRef = RequiredString(id, args, "base");
                string headRef = args?["head"]?.GetValue<string>() ?? "HEAD";
                int ctx = args?["context"]?.GetValue<int?>() ?? DefaultDiffContext;
                bool stat = args?["stat"]?.GetValue<bool?>() ?? false;
                bool nameStatus = args?["name_status"]?.GetValue<bool?>() ?? false;
                IEnumerable<string>? paths = GetEffectivePathListOrNull(args);
                List<string> gitArgs = ["diff", GitNoColorArg];
                if (stat)
                    gitArgs.Add("--stat");
                if (nameStatus)
                    gitArgs.Add("--name-status");
                if (!stat && !nameStatus)
                    gitArgs.Add($"--unified={ctx}");
                gitArgs.Add($"{baseRef}..{headRef}");
                if (paths is not null)
                {
                    gitArgs.Add("--");
                    gitArgs.AddRange(paths);
                }

                return await GitRunner.RunArgumentsAsync(id, repo, gitArgs).ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitLogRangeTool, "Inspect commits behind the changed files")],
                related: [(GitCompareRefsTool, "Count ahead and behind commits between refs"), (GitDiffUnstagedTool, "See current uncommitted changes")]));
    }

    private static IEnumerable<ToolEntry> GitStagingCommitTools()
    {
        yield return new("git_add",
                "Stage files for the next commit. Pass paths array or path string, " +
                "or [\".\" ] to stage everything.",
                ObjectSchema(
                    OptArr(Paths, "Array of file paths or globs to stage, e.g. [\"src/Foo.cs\"] or [\".\"]."),
                    Opt("path", PathSingleAliasDesc)),
                Git,
                 async (id, args, bridge) =>
                {
                    string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                    return await GitSdkReader.StageAsync(id, repo, GetEffectivePathList(args))
                        .ConfigureAwait(false);
                },
                searchHints: BuildSearchHints(
                    workflow: [(GitCommitTool, "Commit after staging"), (GitDiffStagedTool, "Review what was staged")],
                    related: [("git_restore", "Discard working-tree changes"), ("git_reset", "Unstage files"), (GitStatusTool, "Check which files are staged")]));

            yield return new("git_restore",
                "Discard working-tree changes for the specified files, restoring them to HEAD. " +
                "Does not touch the index.",
                ObjectSchema(
                    OptArr(Paths, "Array of file paths to restore, e.g. [\"src/Foo.cs\"]."),
                    Opt("path", PathSingleAliasDesc)),
                Git,
                 async (id, args, bridge) =>
                {
                    string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                    return await GitSdkReader.RestoreAsync(id, repo, GetEffectivePathList(args))
                        .ConfigureAwait(false);
                },
                searchHints: BuildSearchHints(
                    related: [(GitStatusTool, "Check remaining changes"), (GitDiffUnstagedTool, "Preview changes before discarding"), ("git_reset", "Unstage instead of discard")]));

            yield return new("git_reset",
                "Unstage files (mixed reset). If no paths are given, unstages everything.",
                ObjectSchema(
                    OptArr(Paths, "Array of paths to unstage, or omit for all."),
                    Opt("path", PathSingleAliasDesc)),
                Git,
                 async (id, args, bridge) =>
                {
                    string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                    return await GitSdkReader.UnstageAsync(id, repo, GetEffectivePathListOrNull(args))
                        .ConfigureAwait(false);
                },
                searchHints: BuildSearchHints(
                    related: [("git_restore", "Discard working-tree changes"), ("git_diff_staged", "Review staged changes before resetting"), ("git_status", "Check resulting state")]));

            yield return new("git_untrack",
                "Remove files from the git index without deleting them from disk (git rm --cached). " +
                "Use this to stop tracking files that should be .gitignored.",
                ObjectSchema(
                    OptArr(Paths, "Array of file paths to untrack, e.g. [\"bin/\", \"obj/foo.cs\"]."),
                    Opt("path", PathSingleAliasDesc)),
                Git,
                async (id, args, bridge) =>
                {
                    string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                    IEnumerable<string> paths = GetEffectivePathList(args);
                    string[] gitArgs = ["rm", "--cached", "--", ..paths];
                    return await GitRunner.RunArgumentsAsync(id, repo, gitArgs)
                        .ConfigureAwait(false);
                },
                searchHints: BuildSearchHints(
                    related: [("git_add", "Stage the file again"), (GitStatusTool, "Check resulting state"), ("git_restore", "Discard working-tree changes instead")]));

            yield return new(GitCommitTool,
                "Create a commit with a message. Stage files with git_add first.",
                ObjectSchema(Req(Message, "Commit message.")),
                Git,
                async (id, args, bridge) =>
                {
                    string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                    string msg = args?[Message]?.GetValue<string>() ?? string.Empty;
                    return await GitSdkReader.CommitAsync(id, repo, msg)
                        .ConfigureAwait(false);
                },
                searchHints: BuildSearchHints(
                    workflow: [(GitPushTool, "Push after committing"), (GitStatusTool, "Confirm clean working tree")],
                    related: [("git_add", "Stage files before committing"), ("git_commit_amend", "Amend the commit message"), (GitDiffStagedTool, "Review staged changes before committing")]));

            yield return new("git_commit_amend",
                "Amend the most recent commit. Pass a new message or set no_edit to true to keep it.",
                ObjectSchema(
                    Opt(Message, "New commit message. Omit to use --no-edit."),
                    OptBool("no_edit", "Keep the existing commit message (default true when no message given).")),
                Git,
                async (id, args, bridge) =>
                {
                    string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                    string? msg = args?[Message]?.GetValue<string>();
                    // Pass null when no message supplied so AmendCommitAsync keeps the existing message.
                    return await GitSdkReader.AmendCommitAsync(id, repo, msg)
                        .ConfigureAwait(false);
                },
                searchHints: BuildSearchHints(
                    related: [(GitCommitTool, "Create a new commit instead"), (GitDiffStagedTool, "Review staged changes"), (GitStatusTool, "Check state after amending")]));
    }

    private static IEnumerable<ToolEntry> GitBranchTools()
    {
        yield return new(GitCheckoutTool,
            "Switch to an existing branch, tag, or commit.",
            ObjectSchema(Req("target", "Branch name, tag, or commit SHA to check out.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string target = args?["target"]?.GetValue<string>() ?? string.Empty;
                return await GitRunner.RunAsync(id, repo, $"checkout {EscapeArg(target)}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check working-tree state after switching"), (GitPullTool, "Pull latest changes after switching")],
                related: [(GitBranchListTool, "List available branches"), ("git_create_branch", "Create a new branch")]));

        yield return new("git_create_branch",
            "Create and switch to a new branch, optionally starting from a given ref.",
            ObjectSchema(
                Req("name", "New branch name."),
                Opt("start_point", "Optional commit, tag, or branch to branch from.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string name = args?["name"]?.GetValue<string>() ?? string.Empty;
                string? start = args?["start_point"]?.GetValue<string>();
                string startArg = string.IsNullOrWhiteSpace(start) ? string.Empty : $" {EscapeArg(start)}";
                return await GitRunner.RunAsync(id, repo, $"checkout -b {EscapeArg(name)}{startArg}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCheckoutTool, "Switch to the new branch"), (GitPushTool, "Push the new branch to remote")],
                related: [(GitBranchListTool, "List existing branches")]));
    }

    private static IEnumerable<ToolEntry> GitNetworkTools()
    {
        yield return new(GitFetchTool,
            "Fetch from a remote without merging. Defaults to all remotes.",
            ObjectSchema(
                Opt("remote", "Remote name (default: all remotes)."),
                OptBool("all", "Fetch all remotes (default true when remote omitted)."),
                OptBool("prune", "Remove stale remote-tracking branches (default false).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string? remote = args?["remote"]?.GetValue<string>();
                bool all = string.IsNullOrWhiteSpace(remote) &&
                           (args?["all"]?.GetValue<bool?>() ?? true);
                bool prune = args?["prune"]?.GetValue<bool?>() ?? false;
                string remoteArg = all ? "--all" : EscapeArg(remote!);
                string pruneArg = prune ? "--prune" : string.Empty;
                return await GitRunner.RunNetworkAsync(id, repo,
                    $"fetch {remoteArg} {pruneArg}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitPullTool, "Merge after fetching"), (GitBranchListTool, "See new remote branches after fetching")],
                related: [("git_remote_list", "List configured remotes"), ("git_merge", "Merge fetched changes manually")]));

        yield return new(GitPullTool,
            "Fetch and merge from a remote branch.",
            ObjectSchema(
                Opt("remote", "Remote name (default: origin)."),
                Opt("branch", "Branch name (default: current tracking branch).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string remote = args?["remote"]?.GetValue<string>() ?? string.Empty;
                string branch = args?["branch"]?.GetValue<string>() ?? string.Empty;
                string remoteArg = string.IsNullOrWhiteSpace(remote) ? string.Empty : EscapeArg(remote);
                string branchArg = string.IsNullOrWhiteSpace(branch) ? string.Empty : EscapeArg(branch);
                return await GitRunner.RunNetworkAsync(id, repo,
                    string.Join(" ", new[] { "pull", remoteArg, branchArg }.Where(s => !string.IsNullOrEmpty(s))))
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check state after pulling"), ("git_merge", "Resolve conflicts if pull created a merge")],
                related: [(GitFetchTool, "Fetch without merging"), (GitPushTool, "Push after pulling")]));

        yield return new(GitPushTool,
            "Push commits to a remote branch.",
            ObjectSchema(
                Opt("remote", "Remote name (default: origin)."),
                Opt("branch", "Branch name (default: current branch)."),
                OptBool("set_upstream", "Set the upstream tracking reference (-u flag).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string remote = args?["remote"]?.GetValue<string>() ?? string.Empty;
                string branch = args?["branch"]?.GetValue<string>() ?? string.Empty;
                bool setUpstream = args?["set_upstream"]?.GetValue<bool?>() ?? false;
                string uFlag = setUpstream ? "-u" : string.Empty;
                if (setUpstream && string.IsNullOrWhiteSpace(remote))
                    remote = "origin";
                string remoteArg = string.IsNullOrWhiteSpace(remote) ? string.Empty : EscapeArg(remote);
                string branchArg = string.IsNullOrWhiteSpace(branch) ? string.Empty : EscapeArg(branch);
                return await GitRunner.RunNetworkAsync(id, repo,
                    string.Join(" ", new[] { "push", uFlag, remoteArg, branchArg }.Where(s => !string.IsNullOrEmpty(s))))
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Confirm clean state before pushing"), ("git_log", "Review commits being pushed")],
                related: [(GitPullTool, "Pull before pushing to avoid conflicts"), ("git_remote_list", "Check available remotes")]));
    }

    private static IEnumerable<ToolEntry> GitMergeTools()
    {
        yield return new("git_merge",
            "Merge a source branch into the current branch.",
            ObjectSchema(
                Req("source", "Branch or commit to merge."),
                OptBool("ff_only", "Refuse to merge unless fast-forward is possible."),
                OptBool("no_ff", "Always create a merge commit even when fast-forward is possible."),
                OptBool("squash", "Squash all commits into a single staged change."),
                Opt(Message, "Optional merge commit message.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string source = args?["source"]?.GetValue<string>() ?? string.Empty;
                bool ffOnly = args?["ff_only"]?.GetValue<bool?>() ?? false;
                bool noFf = args?["no_ff"]?.GetValue<bool?>() ?? false;
                bool squash = args?["squash"]?.GetValue<bool?>() ?? false;
                string? msg = args?[Message]?.GetValue<string>();
                string flags = string.Join(" ", new[]
                {
                    ffOnly ? "--ff-only" : string.Empty,
                    noFf   ? "--no-ff"   : string.Empty,
                    squash ? "--squash"  : string.Empty,
                    !string.IsNullOrWhiteSpace(msg) ? $"-m {EscapeArg(msg)}" : string.Empty,
                }.Where(s => !string.IsNullOrEmpty(s)));
                return await GitRunner.RunNetworkAsync(id, repo,
                    $"merge {flags} {EscapeArg(source)}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check for merge conflicts after merging")],
                related: [(GitFetchTool, "Fetch before merging"), (GitPullTool, "Fetch and merge in one step"), (GitCommitTool, "Commit after resolving conflicts")]));

    }

    private static IEnumerable<ToolEntry> GitMergeInspectionTools()
    {
        yield return new(GitMergeBaseTool,
            "Return the merge-base commit for two refs using git merge-base.",
            ObjectSchema(
                Req("base", "Base ref."),
                Opt("head", "Head ref (default HEAD)."),
                OptBool("all", "Return all merge bases with --all (default false).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string baseRef = RequiredString(id, args, "base");
                string headRef = args?["head"]?.GetValue<string>() ?? "HEAD";
                bool all = args?["all"]?.GetValue<bool?>() ?? false;
                List<string> gitArgs = ["merge-base"];
                if (all)
                    gitArgs.Add("--all");
                gitArgs.Add(baseRef);
                gitArgs.Add(headRef);
                return await GitRunner.RunArgumentsAsync(id, repo, gitArgs).ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitCompareRefsTool, "Count ahead/behind after finding the base"), (GitLogRangeTool, "Inspect commits from the merge base")],
                related: [(GitDiffRangeTool, "Inspect changed files between refs"), (GitCherryTool, "See patch-equivalent commits")]));

        yield return new(GitCherryTool,
            "List commits in head not yet applied upstream using git cherry. Prefix + means not equivalent upstream; - means equivalent patch exists.",
            ObjectSchema(
                Req("upstream", "Upstream ref to compare against."),
                Opt("head", "Head ref to inspect (default HEAD)."),
                OptBool("verbose", "Include commit subjects with -v (default true).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string upstream = RequiredString(id, args, "upstream");
                string headRef = args?["head"]?.GetValue<string>() ?? "HEAD";
                bool verbose = args?["verbose"]?.GetValue<bool?>() ?? true;
                List<string> gitArgs = ["cherry"];
                if (verbose)
                    gitArgs.Add("-v");
                gitArgs.Add(upstream);
                gitArgs.Add(headRef);
                return await GitRunner.RunArgumentsAsync(id, repo, gitArgs).ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitLogRangeTool, "Inspect commits reported by git cherry"), (GitCompareRefsTool, "Count ahead/behind commits")],
                related: [(GitMergeBaseTool, "Find the common ancestor"), (GitDiffRangeTool, "Inspect file changes")]));

        yield return new(GitConflictsTool,
            "List files with unresolved merge conflicts using git diff --name-only --diff-filter=U.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunArgumentsAsync(id, repo, ["diff", "--name-only", "--diff-filter=U"])
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "See the full conflict status"), (GitDiffUnstagedTool, "Inspect conflict markers in files")],
                related: [(GitRebaseContinueTool, "Continue a rebase after conflicts are resolved"), (GitMergeBaseTool, "Inspect merge ancestry")]));
    }

    private static IEnumerable<ToolEntry> GitRebaseTools()
    {
        yield return new(GitRebaseTool,
            "Rebase the current branch, or an optional named branch, onto another ref. Non-interactive only.",
            ObjectSchema(
                Req("upstream", "Upstream ref to rebase onto, such as upstream/master_27."),
                Opt("branch", "Optional branch to rebase instead of the current branch."),
                Opt("onto", "Optional --onto ref for advanced rebases."),
                OptBool("autostash", "Automatically stash and reapply local changes during the rebase."),
                OptBool("rebase_merges", "Preserve merge commits with --rebase-merges.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string upstream = RequiredString(id, args, "upstream");
                string? branch = args?["branch"]?.GetValue<string>();
                string? onto = args?["onto"]?.GetValue<string>();
                bool autostash = args?["autostash"]?.GetValue<bool?>() ?? false;
                bool rebaseMerges = args?["rebase_merges"]?.GetValue<bool?>() ?? false;
                List<string> gitArgs = ["rebase"];
                if (autostash)
                    gitArgs.Add("--autostash");
                if (rebaseMerges)
                    gitArgs.Add("--rebase-merges");
                if (!string.IsNullOrWhiteSpace(onto))
                {
                    gitArgs.Add("--onto");
                    gitArgs.Add(onto);
                }
                gitArgs.Add(upstream);
                if (!string.IsNullOrWhiteSpace(branch))
                    gitArgs.Add(branch);

                return await GitRunner.RunArgumentsAsync(id, repo, gitArgs, timeoutMs: 120_000)
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: true,
            destructive: true,
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check for conflicts after the rebase"), (GitRebaseContinueTool, "Continue after resolving conflicts"), (GitRebaseAbortTool, "Abort a conflicted rebase")],
                related: [(GitFetchTool, "Fetch the target ref first"), (GitCompareRefsTool, "Compare refs before rebasing"), (GitLogRangeTool, "Inspect commits before rebasing")]));

        yield return new(GitRebaseContinueTool,
            "Continue an in-progress rebase after conflicts have been resolved and staged.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunArgumentsAsync(id, repo, ["rebase", "--continue"], timeoutMs: 120_000)
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: true,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check whether the rebase completed or needs more conflict resolution")],
                related: [(GitRebaseAbortTool, "Abort the in-progress rebase"), (GitRebaseSkipTool, "Skip the current commit")]));

        yield return new(GitRebaseAbortTool,
            "Abort an in-progress rebase and return to the pre-rebase state.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunArgumentsAsync(id, repo, ["rebase", "--abort"], timeoutMs: 120_000)
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: true,
            destructive: true,
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Confirm the working tree after aborting the rebase")],
                related: [(GitRebaseTool, "Start a new rebase after reviewing status")]));

        yield return new(GitRebaseSkipTool,
            "Skip the current commit in an in-progress rebase.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunArgumentsAsync(id, repo, ["rebase", "--skip"], timeoutMs: 120_000)
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: true,
            destructive: true,
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check whether the rebase completed or needs another action")],
                related: [(GitRebaseContinueTool, "Continue after resolving conflicts"), (GitRebaseAbortTool, "Abort the in-progress rebase")]));
    }

    private static IEnumerable<ToolEntry> GitStashTools()
    {
        yield return new("git_stash_list",
            "List stash entries.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunArgumentsAsync(id, repo, ["stash", "list"]).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("git_stash_pop", "Apply the most recent stash entry")],
                related: [("git_stash_push", "Save changes to a new stash")]));

        yield return new("git_stash_push",
            "Stash current working-tree and index changes.",
            ObjectSchema(
                Opt(Message, "Optional stash description."),
                OptBool("include_untracked", "Also stash untracked files (default false).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string? msg = args?[Message]?.GetValue<string>();
                bool untracked = args?["include_untracked"]?.GetValue<bool?>() ?? false;
                string msgArg = !string.IsNullOrWhiteSpace(msg) ? $"-m {EscapeArg(msg)}" : string.Empty;
                string untrackedArg = untracked ? "--include-untracked" : string.Empty;
                return await GitRunner.RunAsync(id, repo,
                    $"stash push {untrackedArg} {msgArg}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCheckoutTool, "Switch branch after stashing"), ("git_stash_pop", "Restore stashed changes later")],
                related: [("git_stash_list", "List saved stash entries"), (GitStatusTool, "Confirm clean working tree after stashing")]));

        yield return new("git_stash_pop",
            "Apply and remove the most recent stash entry.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunAsync(id, repo, "stash pop").ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check working tree after popping")],
                related: [("git_stash_list", "List available stash entries"), ("git_stash_push", "Save more changes to a stash")]));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Wrap a shell argument in double-quotes and escape internal double-quotes.
    /// </summary>
    private static string EscapeArg(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Extract path strings from a JSON array arg for use with LibGit2Sharp.
    /// Falls back to <c>["."]</c> if the arg is absent.
    /// </summary>
    private static IEnumerable<string> GetPathList(JsonObject? args, string argName)
    {
        if (args?[argName] is JsonArray arr)
        {
            List<string> items =
            [
                .. arr
                    .Select(n => n?.GetValue<string>() ?? string.Empty)
                    .Where(s => !string.IsNullOrWhiteSpace(s)),
            ];
            return items.Count > 0 ? items : ["."];
        }

        string? single = args?[argName]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(single) ? ["."] : [single];
    }

    /// <summary>
    /// Like <see cref="GetPathList"/> but returns <see langword="null"/> when the arg is absent,
    /// signalling "all paths" to operations like unstage.
    /// </summary>
    private static IEnumerable<string>? GetPathListOrNull(JsonObject? args, string argName)
        => args?[argName] is null ? null : GetPathList(args, argName);

    /// <summary>
    /// Resolves paths from <c>paths</c> (array) if present, otherwise from the <c>path</c> singular
    /// string alias if present, otherwise falls back to <c>["."]</c>.
    /// </summary>
    private static IEnumerable<string> GetEffectivePathList(JsonObject? args)
    {
        if (args?[Paths] is not null) return GetPathList(args, Paths);
        if (args?["path"] is not null) return GetPathList(args, "path");
        return ["."];
    }

    /// <summary>
    /// Like <see cref="GetEffectivePathList"/> but returns <see langword="null"/> when both <c>paths</c>
    /// and <c>path</c> are absent, signalling "all paths" to operations like unstage.
    /// </summary>
    private static IEnumerable<string>? GetEffectivePathListOrNull(JsonObject? args)
    {
        if (args?[Paths] is not null) return GetPathListOrNull(args, Paths);
        if (args?["path"] is not null) return GetPathList(args, "path");
        return null;
    }

}

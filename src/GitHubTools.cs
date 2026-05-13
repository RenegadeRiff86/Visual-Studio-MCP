using System.Text.Json.Nodes;
using VsIdeBridgeService.SystemTools;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static IEnumerable<ToolEntry> GitHubTools()
    {
        yield return new("github_pr_list",
            "List pull requests for the current repository. Returns number, title, author, " +
            "state, draft flag, review decision, and branch names. " +
            "Requires GitHub CLI (gh) installed and authenticated.",
            ObjectSchema(
                Opt("state", "Filter by PR state: open (default), closed, merged, or all."),
                OptInt("limit", "Maximum number of PRs to return (default 30).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string state = args?["state"]?.GetValue<string>() ?? "open";
                int limit = args?["limit"]?.GetValue<int?>() ?? 30;
                return await GhRunner.RunAsync(id, repo,
                    [
                        "pr", "list",
                        "--state", state,
                        "--limit", limit.ToString(),
                        "--json", "number,title,author,state,createdAt,updatedAt,headRefName,baseRefName,isDraft,reviewDecision",
                    ]).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("github_pr_view", "Read full description and metadata for a specific PR"), ("github_pr_diff", "See the code changes in a PR")],
                related: [("git_log", "See recent commit history"), ("git_branch_list", "List branches")]));

        yield return new("github_pr_view",
            "Show details for a specific pull request: title, author, body, state, review decision, " +
            "branch names, and change stats. " +
            "Requires GitHub CLI (gh) installed and authenticated.",
            ObjectSchema(ReqInt("number", "Pull request number.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                return await GhRunner.RunAsync(id, repo,
                    [
                        "pr", "view", number.ToString(),
                        "--json", "number,title,author,state,body,createdAt,updatedAt,headRefName,baseRefName,isDraft,reviewDecision,mergeable,additions,deletions,changedFiles",
                    ]).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("github_pr_diff", "See the full code diff for this PR")],
                related: [("github_pr_list", "List all open PRs"), ("git_log", "See commit history")]));

        yield return new("github_pr_diff",
            "Show the unified diff for a pull request — all changed files and lines. " +
            "Requires GitHub CLI (gh) installed and authenticated.",
            ObjectSchema(ReqInt("number", "Pull request number.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                return await GhRunner.RunAsync(id, repo,
                    ["pr", "diff", number.ToString()])
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("github_pr_view", "Read PR description and metadata"), ("find_text", "Search for changed symbols in the solution")],
                related: [("github_pr_list", "List open PRs"), ("git_diff_unstaged", "See local unstaged changes")]));
    }
}

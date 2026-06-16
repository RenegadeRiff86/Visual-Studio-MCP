using System.Text.Json.Nodes;
using VsIdeBridgeService.SystemTools;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string GitHubCliRequirement = "Requires GitHub CLI (gh) installed and authenticated.";
    private const string GitHubIssueListTool = "github_issue_list";
    private const string GitHubIssueSearchTool = "github_issue_search";
    private const string GitHubIssueViewTool = "github_issue_view";
    private const string GitHubIssueCommentTool = "github_issue_comment";
    private const string GitHubIssueCloseTool = "github_issue_close";
    private const string GitHubIssueReopenTool = "github_issue_reopen";
    private const string GitHubIssueEditTool = "github_issue_edit";
    private const string GitHubIssueCreateTool = "github_issue_create";
    private const string GitHubPrListTool = "github_pr_list";
    private const string GitHubPrViewTool = "github_pr_view";
    private const string GitHubPrDiffTool = "github_pr_diff";
    private const string GitHubPrCommentsTool = "github_pr_comments";
    private const string GitHubPrReviewsTool = "github_pr_reviews";
    private const string GitHubPrReviewThreadsTool = "github_pr_review_threads";
    private const string GitHubPrChecksTool = "github_pr_checks";
    private const string GitHubActionsFailedLogsTool = "github_actions_failed_logs";
    private const string PullRequestNumberDescription = "Pull request number.";
    private const string IssueListFields = "number,title,author,state,labels,assignees,comments,createdAt,updatedAt,url";
    private const string IssueViewFields = "number,title,author,state,body,labels,assignees,createdAt,updatedAt,url";
    private const string IssueViewFieldsWithComments = "number,title,author,state,body,labels,assignees,comments,createdAt,updatedAt,url";

    private static IEnumerable<ToolEntry> GitHubTools()
    {
        foreach (ToolEntry tool in GitHubIssueTools())
        {
            yield return tool;
        }

        foreach (ToolEntry tool in GitHubIssueCommentAndCloseTools())
        {
            yield return tool;
        }

        foreach (ToolEntry tool in GitHubIssueEditAndCreateTools())
        {
            yield return tool;
        }

        foreach (ToolEntry tool in GitHubPullRequestTools())
        {
            yield return tool;
        }

        foreach (ToolEntry tool in GitHubPullRequestDiscussionTools())
        {
            yield return tool;
        }

        foreach (ToolEntry tool in GitHubPullRequestStatusTools())
        {
            yield return tool;
        }
    }

    private static void AppendLabelArgs(List<string> ghArgs, string labelFlag, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return;
        foreach (string label in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ghArgs.Add(labelFlag);
            ghArgs.Add(label);
        }
    }

    private static IEnumerable<ToolEntry> GitHubIssueTools()
    {
        yield return new(GitHubIssueListTool,
            "List issues for the current repository. Returns number, title, author, state, labels, " +
            "assignees, comment count, timestamps, and URL in the tool result without writing files. " +
            GitHubCliRequirement,
            ObjectSchema(
                Opt("state", "Filter by issue state: open (default), closed, or all."),
                OptInt("limit", "Maximum number of issues to return (default 30).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string state = args?["state"]?.GetValue<string>() ?? "open";
                int limit = args?["limit"]?.GetValue<int?>() ?? 30;
                return await GhRunner.RunAsync(id, repo,
                    [
                        "issue", "list",
                        "--state", state,
                        "--limit", limit.ToString(),
                        "--json", IssueListFields,
                    ]).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitHubIssueViewTool, "Read issue body and optional comments"), (GitHubIssueSearchTool, "Search current-repository issues")],
                related: [(GitHubPrListTool, "List pull requests"), ("git_log", "See recent commit history")]));

        yield return new(GitHubIssueSearchTool,
            "Search issues in the current repository using GitHub search syntax. Returns matching issue " +
            "metadata in the tool result without writing files. " + GitHubCliRequirement,
            ObjectSchema(
                Req("query", "Issue search query, using GitHub issue search syntax."),
                Opt("state", "Filter by issue state: open, closed, or all (default all)."),
                OptInt("limit", "Maximum number of issues to return (default 30).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string query = args!["query"]!.GetValue<string>();
                string state = args?["state"]?.GetValue<string>() ?? "all";
                int limit = args?["limit"]?.GetValue<int?>() ?? 30;
                return await GhRunner.RunAsync(id, repo,
                    [
                        "issue", "list",
                        "--search", query,
                        "--state", state,
                        "--limit", limit.ToString(),
                        "--json", IssueListFields,
                    ]).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitHubIssueViewTool, "Read a matching issue's body and comments")],
                related: [(GitHubIssueListTool, "List issues without a search query"), (GitHubPrListTool, "List pull requests")]));

        yield return new(GitHubIssueViewTool,
            "Show details for a specific issue: title, author, body, state, labels, assignees, " +
            "timestamps, URL, and optional comments. Results stay in the tool response and are not saved to disk. " +
            GitHubCliRequirement,
            ObjectSchema(
                ReqInt("number", "Issue number."),
                OptBool("comments", "Include issue comments in the JSON output (default true).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                bool includeComments = args?["comments"]?.GetValue<bool?>() ?? true;
                string fields = includeComments ? IssueViewFieldsWithComments : IssueViewFields;
                return await GhRunner.RunAsync(id, repo,
                    [
                        "issue", "view", number.ToString(),
                        "--json", fields,
                    ]).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitHubIssueSearchTool, "Find related issues"), (GitHubPrListTool, "Look for linked pull requests")],
                related: [(GitHubIssueListTool, "List open issues"), ("git_log", "See recent commit history")]));
    }

    private static IEnumerable<ToolEntry> GitHubIssueCommentAndCloseTools()
    {
        yield return new(GitHubIssueCommentTool,
            "Add a comment to an existing issue. " + GitHubCliRequirement,
            ObjectSchema(
                ReqInt("number", "Issue number to comment on."),
                Req("body", "Comment body (Markdown).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                string body = args!["body"]!.GetValue<string>();
                return await GhRunner.RunAsync(id, repo,
                    ["issue", "comment", number.ToString(), "--body", body])
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitHubIssueViewTool, "Re-read the issue after commenting")],
                related: [(GitHubIssueCloseTool, "Close the issue"), (GitHubIssueEditTool, "Edit labels or fields")]));

        yield return new(GitHubIssueCloseTool,
            "Close an issue, optionally with a reason and a closing comment. " + GitHubCliRequirement,
            ObjectSchema(
                ReqInt("number", "Issue number to close."),
                Opt("reason", "Close reason: completed (default) or 'not planned'."),
                Opt("comment", "Optional comment to add when closing.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                List<string> ghArgs = ["issue", "close", number.ToString()];
                string? reason = args?["reason"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    ghArgs.Add("--reason");
                    ghArgs.Add(reason);
                }
                string? comment = args?["comment"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    ghArgs.Add("--comment");
                    ghArgs.Add(comment);
                }
                return await GhRunner.RunAsync(id, repo, ghArgs).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitHubIssueReopenTool, "Reopen if closed by mistake")],
                related: [(GitHubIssueCommentTool, "Comment without closing"), (GitHubIssueViewTool, "Read issue details")]));

        yield return new(GitHubIssueReopenTool,
            "Reopen a closed issue, optionally with a comment. " + GitHubCliRequirement,
            ObjectSchema(
                ReqInt("number", "Issue number to reopen."),
                Opt("comment", "Optional comment to add when reopening.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                List<string> ghArgs = ["issue", "reopen", number.ToString()];
                string? comment = args?["comment"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    ghArgs.Add("--comment");
                    ghArgs.Add(comment);
                }
                return await GhRunner.RunAsync(id, repo, ghArgs).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitHubIssueViewTool, "Read issue details after reopening")],
                related: [(GitHubIssueCloseTool, "Close the issue again")]));
    }

    private static IEnumerable<ToolEntry> GitHubIssueEditAndCreateTools()
    {
        yield return new(GitHubIssueEditTool,
            "Edit an issue: add or remove labels, and optionally change the title or body. " +
            "Labels are comma-separated. " + GitHubCliRequirement,
            ObjectSchema(
                ReqInt("number", "Issue number to edit."),
                Opt("add_labels", "Comma-separated labels to add."),
                Opt("remove_labels", "Comma-separated labels to remove."),
                Opt("title", "New issue title."),
                Opt("body", "New issue body (Markdown).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                List<string> ghArgs = ["issue", "edit", number.ToString()];
                AppendLabelArgs(ghArgs, "--add-label", args?["add_labels"]?.GetValue<string>());
                AppendLabelArgs(ghArgs, "--remove-label", args?["remove_labels"]?.GetValue<string>());
                string? title = args?["title"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    ghArgs.Add("--title");
                    ghArgs.Add(title);
                }
                string? body = args?["body"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    ghArgs.Add("--body");
                    ghArgs.Add(body);
                }
                return await GhRunner.RunAsync(id, repo, ghArgs).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitHubIssueViewTool, "Re-read the issue after editing")],
                related: [(GitHubIssueCommentTool, "Add a comment"), (GitHubIssueCloseTool, "Close the issue")]));

        yield return new(GitHubIssueCreateTool,
            "Create a new issue with a title, optional body, and optional comma-separated labels. " +
            GitHubCliRequirement,
            ObjectSchema(
                Req("title", "Issue title."),
                Opt("body", "Issue body (Markdown). Defaults to empty."),
                Opt("labels", "Comma-separated labels to apply.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string title = args!["title"]!.GetValue<string>();
                string body = args?["body"]?.GetValue<string>() ?? string.Empty;
                List<string> ghArgs = ["issue", "create", "--title", title, "--body", body];
                AppendLabelArgs(ghArgs, "--label", args?["labels"]?.GetValue<string>());
                return await GhRunner.RunAsync(id, repo, ghArgs).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitHubIssueViewTool, "Read the created issue"), (GitHubIssueEditTool, "Adjust labels or fields")],
                related: [(GitHubIssueListTool, "List issues")]));
    }

    private static IEnumerable<ToolEntry> GitHubPullRequestTools()
    {
        yield return new(GitHubPrListTool,
            "List pull requests for the current repository. Returns number, title, author, " +
            "state, draft flag, review decision, and branch names. " +
            GitHubCliRequirement,
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
                workflow: [(GitHubPrViewTool, "Read full description and metadata for a specific PR"), (GitHubPrDiffTool, "See the code changes in a PR")],
                related: [(GitHubIssueListTool, "List related issues"), ("git_log", "See recent commit history"), ("git_branch_list", "List branches")]));

        yield return new(GitHubPrViewTool,
            "Show details for a specific pull request: title, author, body, state, review decision, " +
            "branch names, and change stats. " +
            GitHubCliRequirement,
            ObjectSchema(ReqInt("number", PullRequestNumberDescription)),
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
                workflow: [(GitHubPrDiffTool, "See the full code diff for this PR")],
                related: [(GitHubIssueListTool, "List issues"), (GitHubPrListTool, "List all open PRs"), ("git_log", "See commit history")]));

        yield return new(GitHubPrDiffTool,
            "Show the unified diff for a pull request — all changed files and lines. " +
            GitHubCliRequirement,
            ObjectSchema(ReqInt("number", PullRequestNumberDescription)),
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
                workflow: [(GitHubPrViewTool, "Read PR description and metadata"), ("find_text", "Search for changed symbols in the solution")],
                related: [(GitHubIssueListTool, "List related issues"), (GitHubPrListTool, "List open PRs"), ("git_diff_unstaged", "See local unstaged changes")]));
    }

    private static IEnumerable<ToolEntry> GitHubPullRequestDiscussionTools()
    {
        yield return new(GitHubPrCommentsTool,
            "Read top-level pull request conversation comments via GitHub's issue comments API. " +
            "Results stay in the tool response and are not saved to disk. " + GitHubCliRequirement,
            ObjectSchema(ReqInt("number", PullRequestNumberDescription)),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                return await GhRunner.RunAsync(id, repo,
                    ["api", "--paginate", $"repos/{{owner}}/{{repo}}/issues/{number}/comments"])
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitHubPrViewTool, "Read PR metadata before reviewing comments")],
                related: [(GitHubPrReviewsTool, "Read submitted PR reviews"), (GitHubPrReviewThreadsTool, "Read inline review comments")]));

        yield return new(GitHubPrReviewsTool,
            "Read submitted pull request reviews, including states and review bodies. Results stay in the tool response. " +
            GitHubCliRequirement,
            ObjectSchema(ReqInt("number", PullRequestNumberDescription)),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                return await GhRunner.RunAsync(id, repo,
                    ["api", "--paginate", $"repos/{{owner}}/{{repo}}/pulls/{number}/reviews"])
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitHubPrReviewThreadsTool, "Read inline comments from the reviews")],
                related: [(GitHubPrCommentsTool, "Read top-level PR conversation comments"), (GitHubPrChecksTool, "Read PR check status")]));

        yield return new(GitHubPrReviewThreadsTool,
            "Read inline pull request review comments with file paths, positions, and threaded replies when present. " +
            "This REST-backed view does not include GitHub's resolved-thread state. " + GitHubCliRequirement,
            ObjectSchema(ReqInt("number", PullRequestNumberDescription)),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                return await GhRunner.RunAsync(id, repo,
                    ["api", "--paginate", $"repos/{{owner}}/{{repo}}/pulls/{number}/comments"])
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitHubPrDiffTool, "Open the diff for inline comment context")],
                related: [(GitHubPrReviewsTool, "Read submitted review bodies"), (GitHubPrCommentsTool, "Read top-level PR comments")]));
    }

    private static IEnumerable<ToolEntry> GitHubPullRequestStatusTools()
    {
        yield return new(GitHubPrChecksTool,
            "Read pull request check status using gh pr checks. Results stay in the tool response. " + GitHubCliRequirement,
            ObjectSchema(ReqInt("number", PullRequestNumberDescription)),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int number = args!["number"]!.GetValue<int>();
                return await GhRunner.RunAsync(id, repo, ["pr", "checks", number.ToString()])
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitHubActionsFailedLogsTool, "Read failed GitHub Actions logs for a run")],
                related: [(GitHubPrViewTool, "Read PR metadata"), (GitHubPrDiffTool, "Inspect PR code changes")]));

        yield return new(GitHubActionsFailedLogsTool,
            "Read failed-job logs for a GitHub Actions workflow run using gh run view --log-failed. " +
            "Results stay in the tool response and are not saved to disk. " + GitHubCliRequirement,
            ObjectSchema(
                ReqInt("run_id", "GitHub Actions workflow run ID."),
                OptInt("attempt", "Optional workflow run attempt number.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int runId = args!["run_id"]!.GetValue<int>();
                int? attempt = args?["attempt"]?.GetValue<int?>();
                List<string> ghArgs = ["run", "view", runId.ToString(), "--log-failed"];
                if (attempt is not null)
                {
                    ghArgs.Add("--attempt");
                    ghArgs.Add(attempt.Value.ToString());
                }

                return await GhRunner.RunAsync(id, repo, ghArgs, timeoutMs: 120_000)
                    .ConfigureAwait(false);
            },
            readOnly: false,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(GitHubPrChecksTool, "Find failing checks before reading logs")],
                related: [(GitHubPrViewTool, "Read PR metadata"), ("git_status", "Check local state before fixing CI")]));
    }
}

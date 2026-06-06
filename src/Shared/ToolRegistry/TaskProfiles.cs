namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    private static TaskProfile CreateTaskProfile(string task)
    {
        string normalizedTask = task.Trim().ToLowerInvariant();
        string[] tokens = normalizedTask.Split(
            [' ', '_', '-', '.', ',', ':', ';', '?', '!', '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        return new TaskProfile(
            tokens,
            ContainsAny(normalizedTask,
                "find", "search", "grep", "glob", "ls", "list files", "list directory",
                "symbol", "definition", "reference", "read", "view", "cat", "where",
                "inspect", "navigate", "understand", "outline", "trace", "code",
                "context", "repo map", "repository map"),
            ContainsAny(normalizedTask,
                "solution explorer", "filename", "file name", "path fragment", "path", "folder",
                "directory", "list files", "list directory", "ls"),
            ContainsAny(normalizedTask,
                "error", "errors", "warning", "warnings", "message", "messages", "diagnostic",
                "problem", "problems", "build", "broken", "failing", "failure", "failures",
                "log", "logs", "output", "test", "tests", "lint"),
            ContainsAny(normalizedTask,
                "change", "edit", "write", "patch", "refactor", "rename", "update",
                "fix", "replace", "overwrite", "modify", "create", "insert", "append",
                "delete", "remove", "move", "multi edit", "multiedit", "replace text",
                "write file"),
            ContainsAny(normalizedTask,
                "build", "compile", "installer", "publish", "msbuild", "rebuild", "make",
                "cmake"),
            ContainsAny(normalizedTask,
                "run", "execute", "bash", "shell", "powershell", "cmd", "command line",
                "process", "exe", "script", "iscc", "terminal", "test", "tests", "lint",
                "npm", "pytest", "dotnet test"),
            ContainsAny(normalizedTask,
                "which tool", "what tool", "recommend", "discover", "connect", "instance",
                "bridge", "available tools", "tool list", "list tools", "find tool", "what should",
                "how do i", "getting started", "setup", "mcp", "tool search", "toolsearch",
                "tool_help", "call_tool", "schema", "category", "categories", "tools/list",
                "agent", "agents", "local model", "local models", "claude", "codex", "qwen",
                "qwen code", "gemini", "gemni", "grok", "deepseek"),
            ContainsAny(normalizedTask,
                "python", "venv", "virtualenv", "virtual environment", "pip", "conda",
                "interpreter"),
            ContainsAny(normalizedTask,
                "git", "commit", "branch", "stash", "merge", "rebase", "checkout",
                "push", "pull", "fetch", "remote", "staged", "unstaged"),
            ContainsAny(normalizedTask,
                "nuget", "dotnet add", "dotnet remove", "dotnet package"),
            ContainsAny(normalizedTask,
                "breakpoint", "debug", "debugger", "watch", "step into", "step over",
                "step out", "stack frame", "callstack", "call stack", "locals", "exception"),
            ContainsAny(normalizedTask,
                "restore", "discard", "revert file", "checkout --", "git checkout --",
                "corrupted file", "undo file"),
            ContainsAny(normalizedTask,
                "untrack", "git rm", "rm --cached", "stop tracking", "gitignore",
                "git ignore", "exclude from git"));
    }

    private readonly record struct TaskProfile(
        string[] Tokens,
        bool LooksLikeNavigationTask,
        bool LooksLikeSolutionExplorerTask,
        bool LooksLikeDiagnosticTask,
        bool LooksLikeEditTask,
        bool LooksLikeBuildTask,
        bool LooksLikeExternalProcessTask,
        bool LooksLikeDiscoveryTask,
        bool LooksLikePythonTask,
        bool LooksLikeGitTask,
        bool LooksLikeNuGetTask,
        bool LooksLikeDebugTask,
        bool LooksLikeRestoreTask,
        bool LooksLikeUntrackTask);
}

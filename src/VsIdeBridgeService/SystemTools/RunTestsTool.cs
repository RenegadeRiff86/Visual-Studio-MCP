using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VsIdeBridge.ServiceDomain;

namespace VsIdeBridgeService.SystemTools;

internal static partial class RunTestsTool
{
    private const int DefaultTimeoutMs = 120_000;

    [GeneratedRegex(@"(?<outcome>Passed|Failed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+),\s+Total:\s+(?<total>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetTestSummaryRegex();

    public static async Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
        string target = ResolveTarget(solutionDir, args?["project"]?.GetValue<string>());
        if (!string.Equals(target, solutionDir, StringComparison.OrdinalIgnoreCase) && !File.Exists(target))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"Test target '{target}' was not found.");
        }

        int timeoutMs = args?["timeout_ms"]?.GetValue<int?>() ?? DefaultTimeoutMs;
        int tailLines = args?["tail_lines"]?.GetValue<int?>() ?? 0;
        int headLines = args?["head_lines"]?.GetValue<int?>() ?? 0;
        int maxLines = args?["max_lines"]?.GetValue<int?>() ?? 200;
        bool useMaxCap = headLines <= 0 && tailLines <= 0;
        int effectiveHead = useMaxCap ? maxLines : headLines;

        ProcessStartInfo startInfo = BuildStartInfo(id, target, args);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using Process process = Process.Start(startInfo)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to start process '{startInfo.FileName}'.");
        process.StandardInput.Close();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();
        Task completedTask = await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (!ReferenceEquals(completedTask, waitTask))
        {
            TryKill(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"'dotnet test' timed out after {timeoutMs} ms.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        TestSummary? summary = TryParseSummary(stdout + Environment.NewLine + stderr);
        bool outputTruncated = useMaxCap && (LineCount(stdout) > maxLines || LineCount(stderr) > maxLines);
        bool success = process.ExitCode == 0 && (summary is null || summary.Failed == 0);

        JsonObject payload = new()
        {
            ["success"] = success,
            ["exitCode"] = process.ExitCode,
            ["command"] = startInfo.FileName,
            ["args"] = string.Join(" ", startInfo.ArgumentList.Select(FormatDisplayArgument)),
            ["workingDirectory"] = startInfo.WorkingDirectory,
            ["target"] = target,
            ["durationMs"] = stopwatch.ElapsedMilliseconds,
            ["stdout"] = Truncate(stdout, effectiveHead, tailLines),
            ["stderr"] = Truncate(stderr, effectiveHead, tailLines),
            ["outputTruncated"] = outputTruncated,
        };

        if (summary is not null)
        {
            payload["testSummary"] = new JsonObject
            {
                ["outcome"] = summary.Outcome,
                ["failed"] = summary.Failed,
                ["passed"] = summary.Passed,
                ["skipped"] = summary.Skipped,
                ["total"] = summary.Total,
            };
        }

        string successText = summary is null
            ? $"dotnet test exited with code {process.ExitCode}."
            : $"dotnet test {summary.Outcome.ToLowerInvariant()}: {summary.Passed} passed, {summary.Failed} failed, {summary.Skipped} skipped.";
        return ToolResultFormatter.StructuredToolResult(payload, args, isError: !success, successText: successText);
    }

    internal static ProcessStartInfo BuildStartInfo(JsonNode? id, string target, JsonObject? args)
    {
        string dotnet = ResolveDotnetExecutable(id);
        ProcessStartInfo startInfo = new()
        {
            FileName = dotnet,
            WorkingDirectory = File.Exists(target) ? Path.GetDirectoryName(target) ?? string.Empty : target,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("test");
        if (File.Exists(target))
            startInfo.ArgumentList.Add(target);

        AddOptionalValue(startInfo.ArgumentList, "--configuration", args, "configuration");
        AddOptionalValue(startInfo.ArgumentList, "--framework", args, "framework");
        AddOptionalValue(startInfo.ArgumentList, "--runtime", args, "runtime");
        AddOptionalValue(startInfo.ArgumentList, "--settings", args, "settings");
        AddOptionalValue(startInfo.ArgumentList, "--filter", args, "filter");
        AddOptionalValue(startInfo.ArgumentList, "--logger", args, "logger");
        AddOptionalValue(startInfo.ArgumentList, "--results-directory", args, "results_directory");
        AddOptionalValue(startInfo.ArgumentList, "--collect", args, "collect");
        AddOptionalValue(startInfo.ArgumentList, "--verbosity", args, "verbosity");

        if (args?["no_restore"]?.GetValue<bool?>() ?? true)
            startInfo.ArgumentList.Add("--no-restore");
        if (args?["no_build"]?.GetValue<bool?>() ?? false)
            startInfo.ArgumentList.Add("--no-build");
        if (args?["blame"]?.GetValue<bool?>() ?? false)
            startInfo.ArgumentList.Add("--blame");

        return startInfo;
    }

    internal static TestSummary? TryParseSummary(string output)
    {
        Match match = DotnetTestSummaryRegex().Match(output);
        if (!match.Success)
            return null;

        return new TestSummary(
            match.Groups["outcome"].Value,
            int.Parse(match.Groups["failed"].Value),
            int.Parse(match.Groups["passed"].Value),
            int.Parse(match.Groups["skipped"].Value),
            int.Parse(match.Groups["total"].Value));
    }

    private static void AddOptionalValue(Collection<string> arguments, string option, JsonObject? args, string propertyName)
    {
        string? value = args?[propertyName]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            return;

        arguments.Add(option);
        arguments.Add(value);
    }

    private static string ResolveTarget(string solutionDir, string? projectArg)
    {
        if (!string.IsNullOrWhiteSpace(projectArg))
            return ResolveProjectPath(solutionDir, projectArg);

        string[] slnFiles = Directory.GetFiles(solutionDir, "*.sln");
        if (slnFiles.Length > 0)
            return slnFiles[0];

        string[] slnxFiles = Directory.GetFiles(solutionDir, "*.slnx");
        if (slnxFiles.Length > 0)
            return slnxFiles[0];

        return solutionDir;
    }

    private static string ResolveProjectPath(string solutionDir, string projectArg)
    {
        if (Path.IsPathRooted(projectArg))
            return projectArg;

        string relative = Path.Combine(solutionDir, projectArg);
        if (File.Exists(relative))
            return relative;

        string[] projectPatterns = ["*.csproj", "*.vbproj", "*.fsproj"];
        foreach (string pattern in projectPatterns)
        {
            foreach (string proj in Directory.EnumerateFiles(solutionDir, pattern, SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(proj).Equals(projectArg, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(proj).Equals(projectArg, StringComparison.OrdinalIgnoreCase))
                    return proj;
            }
        }

        return relative;
    }

    private static string ResolveDotnetExecutable(JsonNode? id)
    {
        string? explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitHost) && File.Exists(explicitHost))
            return explicitHost;

        string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(candidate))
            return candidate;

        candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe");
        if (File.Exists(candidate))
            return candidate;

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "Could not locate dotnet.exe for run_tests.");
    }

    private static string Truncate(string text, int headLines, int tailLines)
    {
        if (string.IsNullOrEmpty(text) || (headLines <= 0 && tailLines <= 0))
            return text;

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (headLines > 0 && tailLines > 0)
        {
            int headEnd = Math.Min(headLines, lines.Length);
            int tailStart = Math.Max(headEnd, lines.Length - tailLines);
            if (tailStart <= headEnd)
                return string.Join(Environment.NewLine, lines);
            int omitted = tailStart - headEnd;
            return string.Join(Environment.NewLine, lines[..headEnd])
                + $"{Environment.NewLine}... ({omitted} lines omitted) ...{Environment.NewLine}"
                + string.Join(Environment.NewLine, lines[tailStart..]);
        }

        if (headLines > 0)
            return string.Join(Environment.NewLine, lines[..Math.Min(headLines, lines.Length)]);

        int startIndex = Math.Max(0, lines.Length - tailLines);
        return string.Join(Environment.NewLine, lines[startIndex..]);
    }

    private static int LineCount(string text)
        => string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;

    private static string FormatDisplayArgument(string argument)
        => argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex)
        {
            McpServerLog.WriteException("failed to terminate run-tests child process", ex);
        }
        catch (Win32Exception ex)
        {
            McpServerLog.WriteException("failed to terminate run-tests child process", ex);
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException("failed to terminate run-tests child process", ex);
        }
    }

    internal sealed record TestSummary(string Outcome, int Failed, int Passed, int Skipped, int Total);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;
using Microsoft.Build.Locator;

namespace VsIdeBridgeService.SystemTools;

internal static class BuildErrorsTool
{
    private const int DefaultMax = 20;
    private const int BuildTimeoutMs = 120_000;
    private static readonly object RegistrationGate = new();
    private static bool _msBuildRegistered;

    public static async Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string configuration = args?["configuration"]?.GetValue<string>() ?? "Release";
        string? projectArg = args?["project"]?.GetValue<string>();
        int max = args?["max"]?.GetValue<int?>() ?? DefaultMax;

        EnsureMsBuildRegistered(id);

        string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
        string target = string.IsNullOrWhiteSpace(projectArg)
            ? FindSolutionFile(solutionDir)
            : ResolveProjectPath(solutionDir, projectArg);

        if (!File.Exists(target))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"Build target '{target}' was not found.");
        }

        Dictionary<string, string?> globalProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = configuration,
        };

        Stopwatch sw = Stopwatch.StartNew();
        BuildRunResult buildRun = await RunBuildAsync(id, target, solutionDir, globalProperties)
            .ConfigureAwait(false);
        sw.Stop();

        List<JsonObject> allErrors = buildRun.Errors;
        int totalErrors = allErrors.Count;
        bool truncated = totalErrors > max;

        JsonArray errorArray = new();
        foreach (JsonObject error in truncated ? allErrors.GetRange(0, max) : allErrors)
            errorArray.Add(error);

        JsonObject payload = new()
        {
            ["success"] = totalErrors == 0 && buildRun.ResultCode == BuildResultCode.Success,
            ["errorCount"] = totalErrors,
            ["truncated"] = truncated,
            ["errors"] = errorArray,
            ["buildDuration"] = $"{sw.Elapsed.TotalSeconds:F1}s",
            ["configuration"] = configuration,
            ["target"] = Path.GetFileName(target),
            ["resultCode"] = buildRun.ResultCode.ToString(),
        };

        string successText = totalErrors == 0
            ? $"Build succeeded with 0 errors in {sw.Elapsed.TotalSeconds:F1}s."
            : null!;

        return ToolResultFormatter.StructuredToolResult(
            payload,
            args,
            isError: totalErrors > 0,
            successText: totalErrors == 0 ? successText : null);
    }

    private static async Task<BuildRunResult> RunBuildAsync(
        JsonNode? id,
        string target,
        string workingDirectory,
        Dictionary<string, string?> globalProperties)
    {
        using ProjectCollection projectCollection = new(globalProperties);
        ErrorCaptureLogger logger = new();
        BuildParameters parameters = new(projectCollection)
        {
            Loggers = new ILogger[] { logger },
            MaxNodeCount = Math.Max(1, Environment.ProcessorCount),
        };

        string[] targets = ["Build"];
        BuildRequestData request = new(target, globalProperties, toolsVersion: null, targetsToBuild: targets, hostServices: null);
        BuildManager buildManager = BuildManager.DefaultBuildManager;

        buildManager.BeginBuild(parameters);
        try
        {
            BuildSubmission submission = buildManager.PendBuildRequest(request);
            TaskCompletionSource<BuildResult?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            submission.ExecuteAsync(
                completedSubmission => completion.TrySetResult(completedSubmission.BuildResult),
                context: null);

            Task finished = await Task.WhenAny(completion.Task, Task.Delay(BuildTimeoutMs)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, completion.Task))
            {
                buildManager.CancelAllSubmissions();
                throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                    $"MSBuild timed out after {BuildTimeoutMs / 1000}s.");
            }

            BuildResult? result = await completion.Task.ConfigureAwait(false);
            BuildResultCode resultCode = result?.OverallResult ?? BuildResultCode.Failure;
            return new BuildRunResult(resultCode, logger.Errors);
        }
        catch (McpRequestException)
        {
            throw;
        }
        catch (InvalidProjectFileException ex)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError, ex.Message);
        }
        catch (IOException ex)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError, ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError, ex.Message);
        }
        finally
        {
            buildManager.EndBuild();
            projectCollection.UnloadAllProjects();
            Environment.CurrentDirectory = workingDirectory;
        }
    }

    private static void EnsureMsBuildRegistered(JsonNode? id)
    {
        lock (RegistrationGate)
        {
            if (_msBuildRegistered)
                return;

            try
            {
                if (!MSBuildLocator.IsRegistered)
                    MSBuildLocator.RegisterDefaults();

                _msBuildRegistered = true;
            }
            catch (InvalidOperationException ex)
            {
                throw new McpRequestException(id, McpErrorCodes.BridgeError,
                    $"Failed to initialize MSBuild APIs: {ex.Message}");
            }
            catch (FileNotFoundException ex)
            {
                throw new McpRequestException(id, McpErrorCodes.BridgeError,
                    $"Failed to initialize MSBuild APIs: {ex.Message}");
            }
            catch (FileLoadException ex)
            {
                throw new McpRequestException(id, McpErrorCodes.BridgeError,
                    $"Failed to initialize MSBuild APIs: {ex.Message}");
            }
            catch (BadImageFormatException ex)
            {
                throw new McpRequestException(id, McpErrorCodes.BridgeError,
                    $"Failed to initialize MSBuild APIs: {ex.Message}");
            }
        }
    }

    private sealed record BuildRunResult(BuildResultCode ResultCode, List<JsonObject> Errors);

    private sealed class ErrorCaptureLogger : ILogger
    {
        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;

        public string? Parameters { get; set; } = string.Empty;

        public List<JsonObject> Errors { get; } = [];

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += OnErrorRaised;
        }

        public void Shutdown()
        {
        }

        private void OnErrorRaised(object sender, BuildErrorEventArgs args)
        {
            Errors.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(args.File),
                ["path"] = args.File,
                ["line"] = args.LineNumber,
                ["column"] = args.ColumnNumber,
                ["code"] = args.Code,
                ["message"] = args.Message,
                ["project"] = Path.GetFileNameWithoutExtension(args.ProjectFile),
                ["projectPath"] = args.ProjectFile,
            });
        }
    }

    private static string FindSolutionFile(string directory)
    {
        string[] slnFiles = Directory.GetFiles(directory, "*.sln");
        if (slnFiles.Length > 0)
            return slnFiles[0];

        string[] slnxFiles = Directory.GetFiles(directory, "*.slnx");
        if (slnxFiles.Length > 0)
            return slnxFiles[0];

        return directory;
    }

    private static string ResolveProjectPath(string solutionDir, string projectArg)
    {
        if (Path.IsPathRooted(projectArg))
            return projectArg;

        string relative = Path.Combine(solutionDir, projectArg);
        if (File.Exists(relative))
            return relative;

        string[] projectPatterns = ["*.csproj", "*.vbproj", "*.fsproj", "*.vcxproj", "*.pyproj", "*.sqlproj", "*.wapproj"];
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
}

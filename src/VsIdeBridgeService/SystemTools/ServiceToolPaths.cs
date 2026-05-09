using System;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace VsIdeBridgeService.SystemTools;

internal static class ServiceToolPaths
{
    public static string ResolveInstalledCompanionPath(string fileName, string? solutionPath = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Companion file name is required.", nameof(fileName));
        }

        string installedCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(installedCandidate))
        {
            return installedCandidate;
        }

        string? solutionDirectory = TryGetSolutionDirectory(solutionPath);
        if (string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return installedCandidate;
        }

        string[] candidates =
        [
            Path.Combine(solutionDirectory, "src", "VsIdeBridgeLauncher", "bin", "Release", "net472", fileName),
            Path.Combine(solutionDirectory, "src", "VsIdeBridgeLauncher", "bin", "Release", fileName),
            Path.Combine(solutionDirectory, "src", "VsIdeBridgeLauncher", "bin", "Debug", "net472", fileName),
            Path.Combine(solutionDirectory, "src", "VsIdeBridgeLauncher", "bin", "Debug", fileName),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return installedCandidate;
    }

    public static string ResolveSolutionDirectory(BridgeConnection bridge)
    {
        string? solutionDirectory = TryGetCurrentSolutionDirectory(bridge);
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return solutionDirectory;
        }

        solutionDirectory = TryDiscoverSolutionDirectory(bridge);
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return solutionDirectory;
        }

        return Environment.CurrentDirectory;
    }

    private static string? TryGetCurrentSolutionDirectory(BridgeConnection bridge)
    {
        string? solutionDirectory = TryGetSolutionDirectory(bridge.CurrentSolutionPath);
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return solutionDirectory;
        }

        BridgeInstance? currentInstance = bridge.CurrentInstance;
        if (currentInstance is not null)
        {
            solutionDirectory = TryGetSolutionDirectory(currentInstance.SolutionPath);
            if (!string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return solutionDirectory;
            }
        }

        return null;
    }

    private static string? TryDiscoverSolutionDirectory(BridgeConnection bridge)
    {
        try
        {
            BridgeInstance instance = VsDiscovery.SelectAsync(bridge.CurrentSelector, bridge.Mode)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            return TryGetSolutionDirectory(instance.SolutionPath);
        }
        catch (BridgeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the root directory of the git repository that contains the current or discoverable solution.
    /// Walks up from the solution directory looking for a <c>.git</c> directory or file.
    /// Falls back to the solution directory when no git root is found.
    /// </summary>
    public static string ResolveRepoRootDirectory(BridgeConnection bridge)
    {
        string? solutionDirectory = TryGetCurrentSolutionDirectory(bridge)
            ?? TryDiscoverSolutionDirectory(bridge);
        if (string.IsNullOrWhiteSpace(solutionDirectory))
        {
            throw new McpRequestException(null, McpErrorCodes.BridgeError,
                "Repository-scoped bridge tools require a bound or uniquely discoverable Visual Studio solution. " +
                "Call list_instances and bind_solution or bind_instance before running this tool.");
        }

        return FindGitRoot(solutionDirectory) ?? solutionDirectory;
    }

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> to find the directory that contains
    /// a <c>.git</c> entry (directory for normal repos, file for git worktrees).
    /// Returns <see langword="null"/> when no git root is found within a reasonable depth.
    /// </summary>
    public static string? FindGitRoot(string startDirectory)
    {
        string current = startDirectory;
        for (int depth = 0; depth < 10 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (Directory.Exists(Path.Combine(current, ".git")) ||
                File.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            string parent = Path.GetDirectoryName(current) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return null;
    }

    internal static string? TryGetSolutionDirectory(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(solutionPath);
        if (Directory.Exists(fullPath))
        {
            return Path.TrimEndingDirectorySeparator(fullPath);
        }

        string? solutionDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return null;
        }

        return solutionDirectory;
    }
}

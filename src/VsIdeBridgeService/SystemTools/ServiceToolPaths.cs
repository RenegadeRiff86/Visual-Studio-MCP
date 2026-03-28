using System;
using System.IO;

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

        return Environment.CurrentDirectory;
    }

    private static string? TryGetSolutionDirectory(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return null;
        }

        string? solutionDirectory = Path.GetDirectoryName(solutionPath);
        if (string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return null;
        }

        return solutionDirectory;
    }
}

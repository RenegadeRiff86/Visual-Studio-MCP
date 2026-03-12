using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VsIdeBridgeCli;

internal static class VsOpenInstanceSelector
{
    public static PipeDiscovery? SelectInstance(
        IReadOnlyCollection<string> existingInstanceIds,
        IReadOnlyCollection<int> existingProcessIds,
        IReadOnlyList<PipeDiscovery> currentInstances,
        int launchedProcessId,
        string? requestedSolutionPath)
    {
        var normalizedRequestedSolution = NormalizePath(requestedSolutionPath);
        var orderedInstances = currentInstances
            .OrderByDescending(instance => instance.LastWriteTimeUtc)
            .ToArray();

        var newInstances = orderedInstances
            .Where(instance =>
                !existingInstanceIds.Contains(instance.InstanceId)
                || !existingProcessIds.Contains(instance.ProcessId))
            .ToArray();

        return SelectPreferredInstance(newInstances, orderedInstances, launchedProcessId, normalizedRequestedSolution);
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }

    private static PipeDiscovery? SelectPreferredInstance(
        PipeDiscovery[] newInstances,
        PipeDiscovery[] allInstances,
        int launchedProcessId,
        string? requestedSolutionPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedSolutionPath))
        {
            var matchingNewSolution = newInstances.FirstOrDefault(instance => PathsEqual(instance.SolutionPath, requestedSolutionPath));
            if (matchingNewSolution is not null)
            {
                return matchingNewSolution;
            }
        }

        var launchedNewInstance = newInstances.FirstOrDefault(instance => instance.ProcessId == launchedProcessId);
        if (launchedNewInstance is not null)
        {
            return launchedNewInstance;
        }

        if (newInstances.Length > 0)
        {
            return newInstances[0];
        }

        if (!string.IsNullOrWhiteSpace(requestedSolutionPath))
        {
            var matchingExistingSolution = allInstances.FirstOrDefault(instance => PathsEqual(instance.SolutionPath, requestedSolutionPath));
            if (matchingExistingSolution is not null)
            {
                return matchingExistingSolution;
            }
        }

        return allInstances.FirstOrDefault(instance => instance.ProcessId == launchedProcessId);
    }

    internal static bool PathsEqual(string? left, string? right)
    {
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }
}

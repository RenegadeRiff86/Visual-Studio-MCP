using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    private static string GetNormalizedOutputType(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? raw = GetNormalizedProjectPropertyString(project, "OutputType");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Unknown";
        }

        if (!int.TryParse(raw, out int numericValue))
        {
            return raw!;
        }

        return numericValue switch
        {
            2 => "Library",
            0 or 1 => "Exe",
            _ => raw!,
        };
    }

    private static string[] GetOutputDirectoryCandidates(string projectDirectory, string configurationName, string? platformName, string? targetFramework)
    {
        List<string> candidates = [];

        void AddCandidate(params string[] parts)
        {
            string[] filtered = [..parts.Where(part => !string.IsNullOrWhiteSpace(part))];
            if (filtered.Length == 0)
            {
                return;
            }

            string candidate = PathNormalization.NormalizeFilePath(Path.Combine(filtered));
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        AddCandidate(projectDirectory, "bin", configurationName, targetFramework ?? string.Empty);
        AddCandidate(projectDirectory, "bin", configurationName);

        if (!string.IsNullOrWhiteSpace(platformName) && !string.Equals(platformName, "Any CPU", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(projectDirectory, "bin", platformName!, configurationName, targetFramework ?? string.Empty);
            AddCandidate(projectDirectory, "bin", platformName!, configurationName);
        }

        return [.. candidates];
    }

    private static string? FindPrimaryOutputPath(IEnumerable<string> candidateDirectories, string assemblyName)
    {
        foreach (string directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string extension in PreferredOutputExtensions)
            {
                string candidate = Path.Combine(directory, assemblyName + extension);
                if (File.Exists(candidate))
                {
                    return PathNormalization.NormalizeFilePath(candidate);
                }
            }
        }

        return null;
    }

    private static string[] EnumerateOutputArtifacts(string? outputDirectory, string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(outputDirectory)
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), assemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(PathNormalization.NormalizeFilePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string GetFallbackTargetExtension(Project project, string normalizedOutputType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string projectDirectory = Path.GetDirectoryName(project.FullName) ?? string.Empty;
        if (File.Exists(Path.Combine(projectDirectory, "source.extension.vsixmanifest")))
        {
            return ".vsix";
        }

        return string.Equals(normalizedOutputType, "Library", StringComparison.OrdinalIgnoreCase)
            ? ".dll"
            : ".exe";
    }

    private static JObject ProjectOutputToJson(
        Project project,
        string configurationName,
        string? platformName,
        string? targetFramework,
        string assemblyName,
        string normalizedOutputType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string projectDirectory = Path.GetDirectoryName(project.FullName) ?? string.Empty;
        string[] candidateDirectories = GetOutputDirectoryCandidates(projectDirectory, configurationName, platformName, targetFramework);
        string? primaryOutputPath = FindPrimaryOutputPath(candidateDirectories, assemblyName);
        string? outputDirectory = primaryOutputPath is null
            ? candidateDirectories.FirstOrDefault()
            : Path.GetDirectoryName(primaryOutputPath);
        string targetExtension = primaryOutputPath is null
            ? GetFallbackTargetExtension(project, normalizedOutputType)
            : Path.GetExtension(primaryOutputPath);
        string targetName = primaryOutputPath is null
            ? assemblyName
            : Path.GetFileNameWithoutExtension(primaryOutputPath);
        string targetFileName = primaryOutputPath is null
            ? targetName + targetExtension
            : Path.GetFileName(primaryOutputPath);

        return new JObject
        {
            ["project"] = project.Name,
            [UniqueNamePropertyName] = project.UniqueName,
            ["configuration"] = configurationName,
            ["platform"] = platformName,
            ["targetFramework"] = targetFramework,
            ["assemblyName"] = assemblyName,
            ["outputType"] = normalizedOutputType,
            ["outputDirectory"] = outputDirectory,
            ["targetName"] = targetName,
            ["targetExtension"] = targetExtension,
            ["targetFileName"] = targetFileName,
            ["targetPath"] = primaryOutputPath,
            ["exists"] = primaryOutputPath is not null && File.Exists(primaryOutputPath),
            ["searchedDirectories"] = new JArray(candidateDirectories),
            ["artifacts"] = new JArray(EnumerateOutputArtifacts(outputDirectory, assemblyName)),
        };
    }
}

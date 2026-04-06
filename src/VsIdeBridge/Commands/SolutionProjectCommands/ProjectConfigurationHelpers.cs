using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    // Data class for extracted configuration info (no COM dependencies)
    private sealed class ConfigurationData(string configurationName, string platformName, string moniker, bool isActive)
    {
        public string ConfigurationName { get; } = configurationName;
        public string PlatformName { get; } = platformName;
        public string Moniker { get; } = moniker;
        public bool IsActive { get; } = isActive;
    }

    private static IEnumerable<Configuration> EnumerateProjectConfigurations(ConfigurationManager? configurationManager)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (configurationManager is null)
        {
            yield break;
        }

        foreach (Configuration configuration in configurationManager)
        {
            yield return configuration;
        }
    }

    private static string? GetConfigurationMoniker(Configuration? configuration)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (configuration is null)
        {
            return null;
        }

        return $"{configuration.ConfigurationName}|{configuration.PlatformName}";
    }

    private static JObject ProjectConfigurationToJson(Configuration configuration, Configuration? activeConfiguration)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string moniker = GetConfigurationMoniker(configuration) ?? string.Empty;
        return new JObject
        {
            ["name"] = moniker,
            ["configurationName"] = configuration.ConfigurationName,
            ["platformName"] = configuration.PlatformName,
            ["isActive"] = string.Equals(moniker, GetConfigurationMoniker(activeConfiguration), StringComparison.OrdinalIgnoreCase),
        };
    }

    private static ConfigurationManager? TryGetConfigurationManager(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.ConfigurationManager;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Project '{project.Name}' does not expose configurations: {ex.Message}");
            return null;
        }
    }

    private static Configuration? TryGetActiveProjectConfiguration(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ConfigurationManager? configurationManager = TryGetConfigurationManager(project);
        if (configurationManager is null)
        {
            return null;
        }

        try
        {
            return configurationManager.ActiveConfiguration;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Unable to read active configuration for '{project.Name}': {ex.Message}");
            return null;
        }
    }
}

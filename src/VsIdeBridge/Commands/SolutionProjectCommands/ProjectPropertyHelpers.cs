using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    private static JToken? TryGetPropertyValue(Properties? properties, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (properties is null)
        {
            return null;
        }

        try
        {
            return ToJsonToken(properties.Item(name)?.Value);
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Project property '{name}' is unavailable: {ex.Message}");
            return null;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Project property '{name}' could not be read: {ex.Message}");
            return null;
        }
        catch (NotImplementedException ex)
        {
            Debug.WriteLine($"Project property '{name}' is not implemented by this project type: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"Project property '{name}' is not supported by this project type: {ex.Message}");
            return null;
        }
    }

    private static JToken? TryGetNormalizedPropertyValue(Project project, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return NormalizeProjectPropertyValue(project, name, TryGetPropertyValue(project.Properties, name));
    }

    private static string? TryGetPropertyString(Properties? properties, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        JToken? value = TryGetPropertyValue(properties, name);
        return value?.Type == JTokenType.Null
            ? null
            : value?.ToString();
    }

    private static IReadOnlyList<(string Name, JToken? Value)> EnumerateProjectProperties(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Properties? properties = project.Properties;
        if (properties is null)
        {
            return [];
        }

        List<(string Name, JToken? Value)> values = [];

        foreach (Property property in properties)
        {
            string? name = null;
            try
            {
                name = property.Name;
                JToken? value = NormalizeProjectPropertyValue(project, name, ToJsonToken(property.Value));
                if (!string.IsNullOrWhiteSpace(name) && value is not null)
                {
                    values.Add((name, value));
                }
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}': {ex.Message}");
            }
            catch (NotImplementedException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}' because it is not implemented by this project type: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}' because it is not supported by this project type: {ex.Message}");
            }
        }

        return values;
    }

    private static JToken? NormalizeProjectPropertyValue(Project project, string name, JToken? value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (value is null)
        {
            return null;
        }

        return string.Equals(name, "TargetFramework", StringComparison.OrdinalIgnoreCase)
            ? NormalizeTargetFrameworkValue(project, value)
            : value;
    }

    private static JToken NormalizeTargetFrameworkValue(Project project, JToken value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (value.Type == JTokenType.String)
        {
            string current = value.ToString();
            if (!string.IsNullOrWhiteSpace(current) && !int.TryParse(current, out _))
            {
                return value;
            }
        }

        string? targetFrameworks = TryGetPropertyString(project.Properties, "TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return targetFrameworks;
        }

        string? moniker = TryGetPropertyString(project.Properties, "TargetFrameworkMoniker")
            ?? TryGetPropertyString(project.Properties, "TargetFrameworkMonikers");

        string? friendlyTargetFramework = TryConvertFrameworkMonikerToTfm(
            moniker,
            TryGetPropertyString(project.Properties, "TargetPlatformIdentifier"),
            TryGetPropertyString(project.Properties, "TargetPlatformVersion"));

        return string.IsNullOrWhiteSpace(friendlyTargetFramework)
            ? value
            : new JValue(friendlyTargetFramework);
    }

    private static string? TryConvertFrameworkMonikerToTfm(string? moniker, string? targetPlatformIdentifier, string? targetPlatformVersion)
    {
        if (string.IsNullOrWhiteSpace(moniker))
        {
            return null;
        }

        string monikerText = moniker!;
        string? primaryMoniker = monikerText
            .Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(static value => value.Length > 0);

        if (string.IsNullOrWhiteSpace(primaryMoniker))
        {
            return null;
        }

        const string NetCoreAppPrefix = ".NETCoreApp,Version=v";
        const string NetStandardPrefix = ".NETStandard,Version=v";
        const string NetFrameworkPrefix = ".NETFramework,Version=v";

        string? baseTfm = primaryMoniker switch
        {
            string value when value.StartsWith(NetCoreAppPrefix, StringComparison.OrdinalIgnoreCase)
                => "net" + value.Substring(NetCoreAppPrefix.Length),
            string value when value.StartsWith(NetStandardPrefix, StringComparison.OrdinalIgnoreCase)
                => "netstandard" + value.Substring(NetStandardPrefix.Length),
            string value when value.StartsWith(NetFrameworkPrefix, StringComparison.OrdinalIgnoreCase)
                => "net" + value.Substring(NetFrameworkPrefix.Length).Replace(".", string.Empty),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(baseTfm))
        {
            return null;
        }

        if (!string.Equals(targetPlatformIdentifier, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return baseTfm;
        }

        return string.IsNullOrWhiteSpace(targetPlatformVersion)
            ? baseTfm + "-windows"
            : baseTfm + "-windows" + targetPlatformVersion;
    }

    private static string? GetPrimaryValue(string? value)
    {
        return NormalizeXmlValue(
            value?
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.Length > 0));
    }

    private static string? GetNormalizedProjectPropertyString(Project project, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        JToken? token = TryGetNormalizedPropertyValue(project, name);
        return token is null || token.Type == JTokenType.Null
            ? null
            : NormalizeXmlValue(token.ToString());
    }
}

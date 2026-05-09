using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VsIdeBridge.Shared;

public static class BridgeLogPaths
{
    private const string ProductDirectoryName = "VsIdeBridge";
    private const string LogsDirectoryName = "logs";
    private const string McpServerLogFileName = "mcp-server.log";
    private const string RegistryKeyPath = @"SOFTWARE\VsIdeBridge";
    private const string RegistryInstallPathValue = "InstallPath";

    public static string GetSharedLogDirectory()
    {
        string? installPath = TryGetRegistryInstallPath();
        if (!string.IsNullOrWhiteSpace(installPath))
            return Path.Combine(installPath, LogsDirectoryName);

        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonAppData))
            return Path.Combine(commonAppData, ProductDirectoryName, LogsDirectoryName);

        return GetTempLogDirectory();
    }

    public static string? GetInstallPath()
    {
        return TryGetRegistryInstallPath();
    }

    private static string? TryGetRegistryInstallPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
            return key?.GetValue(RegistryInstallPathValue) as string;
        }
        catch
        {
            return null;
        }
    }

    public static string GetTempLogDirectory()
    {
        return Path.Combine(Path.GetTempPath(), ProductDirectoryName, LogsDirectoryName);
    }

    public static string GetMcpServerLogPath()
    {
        return Path.Combine(GetSharedLogDirectory(), McpServerLogFileName);
    }

    public static string GetMcpServerTempLogPath()
    {
        return Path.Combine(GetTempLogDirectory(), McpServerLogFileName);
    }

    public static string GetVisualStudioExtensionLogPath()
    {
        return GetVisualStudioExtensionLogPath(DateTime.Now);
    }

    public static string GetVisualStudioExtensionLogPath(DateTime timestamp)
    {
        return Path.Combine(GetSharedLogDirectory(), $"vs-ide-bridge-{timestamp:yyyy-MM-dd}.log");
    }
}

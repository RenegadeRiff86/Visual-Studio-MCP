using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.Shell;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Infrastructure;

internal static class BridgeActivityLog
{
    private static volatile bool _prunedOnce;

    public static void LogWarning(string source, string context, Exception ex)
    {
        ActivityLog.LogWarning(source, $"{context}: {ex.Message}");
        Debug.WriteLine($"{source} warning: {context}: {ex}");
        WriteToFile("WARNING", source, context, ex.ToString());
    }

    /// <summary>
    /// Logs at verbose level: debug output only, no VS ActivityLog entry and no file write.
    /// Use for expected fallback conditions that should not appear in the log file.
    /// </summary>
    public static void LogVerbose(string source, string context, Exception ex)
    {
        Debug.WriteLine($"{source} verbose: {context}: {ex.Message}");
    }

    private static void WriteToFile(string level, string source, string context, string detail)
    {
        try
        {
            string logDir = BridgeLogPaths.GetSharedLogDirectory();
            Directory.CreateDirectory(logDir);
            if (!_prunedOnce)
            {
                _prunedOnce = true;
                BridgeLogPaths.PruneExtensionLogs(logDir);
                BridgeLogPaths.CleanupLegacyLogs(logDir);
            }
            string logPath = BridgeLogPaths.GetVisualStudioExtensionLogPath();
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{source}] {context}{Environment.NewLine}{detail}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch (IOException ex)
        {
            // Logging must never crash the host process; fall back to the debugger output only.
            System.Diagnostics.Debug.WriteLine($"BridgeActivityLog.WriteToFile failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"BridgeActivityLog.WriteToFile failed: {ex.Message}");
        }
    }
}

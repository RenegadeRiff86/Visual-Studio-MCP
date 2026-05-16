using System;
using System.IO;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Services;

/// <summary>
/// Manages the enabled/disabled state of the best-practice analyzer.
/// Best-practice analysis is <b>enabled by default</b>; creating the flag file suppresses it.
/// </summary>
internal static class BestPracticeStateManager
{
    private static readonly string FlagFilePath = HttpServerStatePaths.GetBestPracticeDisabledFlagPath();

    /// <summary>
    /// Returns <c>true</c> when best-practice analysis is enabled (default when no flag file exists).
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                return !File.Exists(FlagFilePath);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BestPracticeStateManager] Could not read flag file: {ex.Message}");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BestPracticeStateManager] Could not read flag file: {ex.Message}");
                return true;
            }
            catch (NotSupportedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BestPracticeStateManager] Could not read flag file: {ex.Message}");
                return true;
            }
        }
    }

    /// <summary>Enable best-practice analysis by removing the disabled flag file.</summary>
    public static void Enable()
    {
        try
        {
            if (File.Exists(FlagFilePath))
                File.Delete(FlagFilePath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to enable best-practice analysis: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to enable best-practice analysis: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException($"Failed to enable best-practice analysis: {ex.Message}", ex);
        }
    }

    /// <summary>Disable best-practice analysis by writing the disabled flag file.</summary>
    public static void Disable()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FlagFilePath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(FlagFilePath, string.Empty);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to disable best-practice analysis: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to disable best-practice analysis: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException($"Failed to disable best-practice analysis: {ex.Message}", ex);
        }
    }
}

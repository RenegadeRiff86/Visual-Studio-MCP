using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VsIdeBridge.Infrastructure;

internal static class CommandResultWriter
{
    public static async Task WriteAsync(string outputPath, CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        string normalizedPath = PathNormalization.NormalizeFilePath(outputPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new CommandErrorException("output_write_failed", "Output path is empty.");
        }

        string? directory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new CommandErrorException("output_write_failed", $"Could not determine output directory from '{normalizedPath}'.");
        }

        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, Path.GetRandomFileName());
        string json = JsonConvert.SerializeObject(envelope, Formatting.Indented);

        try
        {
            using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(normalizedPath))
            {
                File.Delete(normalizedPath);
            }

            File.Move(tempPath, normalizedPath);
        }
        catch (IOException ex)
        {
            throw new CommandErrorException("output_write_failed", $"Failed to write output file '{normalizedPath}'.", new { exception = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CommandErrorException("output_write_failed", $"Failed to write output file '{normalizedPath}'.", new { exception = ex.Message });
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Temp file cleanup failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Temp file cleanup failed: {ex.Message}");
        }
    }
}

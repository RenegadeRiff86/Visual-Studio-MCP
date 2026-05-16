using VsIdeBridgeService.SystemTools;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class ServiceToolPathsTests
{
    [Fact]
    public void TryGetSolutionDirectoryReturnsDirectorySolutionPath()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string? result = ServiceToolPaths.TryGetSolutionDirectory(directory);

            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryGetSolutionDirectoryReturnsParentForSolutionFilePath()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string solutionPath = Path.Combine(directory, "Sample.sln");

            string? result = ServiceToolPaths.TryGetSolutionDirectory(solutionPath);

            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VsIdeBridgeServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

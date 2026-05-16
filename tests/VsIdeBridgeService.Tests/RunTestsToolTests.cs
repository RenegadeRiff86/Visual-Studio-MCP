using System.Diagnostics;
using System.Text.Json.Nodes;
using VsIdeBridgeService.SystemTools;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class RunTestsToolTests
{
    [Fact]
    public void ParsesDotnetTestSummary()
    {
        RunTestsTool.TestSummary? summary = RunTestsTool.TryParseSummary(
            "Passed!  - Failed:     0, Passed:     4, Skipped:     1, Total:     5, Duration: 18 ms");

        Assert.NotNull(summary);
        Assert.Equal("Passed", summary!.Outcome);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(4, summary.Passed);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(5, summary.Total);
    }

    [Fact]
    public void BuildsDotnetTestArgumentsFromStructuredInput()
    {
        JsonObject args = new()
        {
            ["configuration"] = "Release",
            ["filter"] = "FullyQualifiedName~StdioHostLeaseTests",
            ["logger"] = "trx;LogFilePrefix=testResults",
            ["results_directory"] = "TestResults",
            ["no_restore"] = true,
            ["no_build"] = true,
            ["blame"] = true,
        };

        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string projectPath = Path.Combine(tempDir, "Sample.Tests.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        try
        {
            ProcessStartInfo startInfo = RunTestsTool.BuildStartInfo(
                id: null,
                target: projectPath,
                args);

            string[] arguments = [.. startInfo.ArgumentList];
            Assert.Equal("test", arguments[0]);
            Assert.Contains(projectPath, arguments);
            Assert.Contains("--configuration", arguments);
            Assert.Contains("Release", arguments);
            Assert.Contains("--filter", arguments);
            Assert.Contains("FullyQualifiedName~StdioHostLeaseTests", arguments);
            Assert.Contains("--logger", arguments);
            Assert.Contains("trx;LogFilePrefix=testResults", arguments);
            Assert.Contains("--results-directory", arguments);
            Assert.Contains("TestResults", arguments);
            Assert.Contains("--no-restore", arguments);
            Assert.Contains("--no-build", arguments);
            Assert.Contains("--blame", arguments);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using VsIdeBridgeLauncher;
using Xunit;

namespace VsIdeBridgeLauncher.Tests;

public class LauncherProcessSelectionTempRootTests
{
    [Fact]
    public void NormalizeTempRoots_RemovesEmptyAndDuplicateEntries()
    {
        IReadOnlyList<string> roots = LauncherProcessSelection.NormalizeTempRoots(
            new object?[]
            {
                null,
                "",
                "C:\\Temp\\",
                "C:\\Temp",
                "D:\\Scratch\\",
                "D:\\Scratch"
            }.Cast<string>());

        Assert.Equal(LauncherProcessSelectionTestData.ExpectedNormalizedPathCount, roots.Count);
        Assert.Equal("C:\\Temp", roots[0]);
        Assert.Equal("D:\\Scratch", roots[1]);
    }

    [Fact]
    public void BuildDiscoveryFileCandidates_UsesNormalizedRoots()
    {
        IReadOnlyList<string> paths = LauncherProcessSelection.BuildDiscoveryFileCandidates(
            [
                "C:\\Temp\\",
                "D:\\Scratch"
            ],
            processId: 4321);

        Assert.Equal(LauncherProcessSelectionTestData.ExpectedNormalizedPathCount, paths.Count);
        Assert.Equal("C:\\Temp\\vs-ide-bridge\\pipes\\bridge-4321.json", paths[0]);
        Assert.Equal("D:\\Scratch\\vs-ide-bridge\\pipes\\bridge-4321.json", paths[1]);
    }
}

using LibGit2Sharp;
using System.Text.Json.Nodes;
using VsIdeBridgeService.SystemTools;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class GitSdkReaderStatusTests : IDisposable
{
    private readonly string _repoDirectory;

    public GitSdkReaderStatusTests()
    {
        _repoDirectory = Path.Combine(Path.GetTempPath(), "VsIdeBridgeStatusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDirectory);
        Repository.Init(_repoDirectory);

        using Repository repo = new(_repoDirectory);
        File.WriteAllText(Path.Combine(_repoDirectory, "tracked.txt"), "one");
        Commands.Stage(repo, "tracked.txt");
        Signature signature = new("VS IDE Bridge", "bridge@example.invalid", DateTimeOffset.UtcNow);
        repo.Commit("initial", signature, signature);
    }

    [Fact]
    public async Task GetStatusAsyncReturnsPorcelainBranchAndChangedPaths()
    {
        File.WriteAllText(Path.Combine(_repoDirectory, "tracked.txt"), "two");
        File.WriteAllText(Path.Combine(_repoDirectory, "new.txt"), "new");

        JsonObject result = Assert.IsType<JsonObject>(await GitSdkReader.GetStatusAsync(null, _repoDirectory));

        Assert.False(result["isError"]!.GetValue<bool>());
        JsonObject structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        string stdout = structured["stdout"]!.GetValue<string>().Replace("\r\n", "\n");
        JsonArray entries = Assert.IsType<JsonArray>(structured["entries"]);

        Assert.StartsWith("## ", stdout);
        Assert.Contains("\n M tracked.txt\n", stdout);
        Assert.Contains("\n?? new.txt\n", stdout);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task GetUnstagedDiffAsyncReturnsWorkingTreePatchWithoutUntrackedFiles()
    {
        File.WriteAllText(Path.Combine(_repoDirectory, "tracked.txt"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(_repoDirectory, "new.txt"), "new\n");

        JsonObject result = Assert.IsType<JsonObject>(await GitSdkReader.GetUnstagedDiffAsync(null, _repoDirectory, 1));

        Assert.False(result["isError"]!.GetValue<bool>());
        JsonObject structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        string stdout = structured["stdout"]!.GetValue<string>().Replace("\r\n", "\n");
        JsonArray entries = Assert.IsType<JsonArray>(structured["entries"]);

        Assert.Contains("diff --git a/tracked.txt b/tracked.txt", stdout);
        Assert.Contains("+two", stdout);
        Assert.DoesNotContain("new.txt", stdout);
        Assert.Equal(1, structured["filesChanged"]!.GetValue<int>());
        Assert.Single(entries);
        Assert.True(structured["linesAdded"]!.GetValue<int>() >= 1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoDirectory))
        {
            ResetAttributesForDeletion(_repoDirectory);
            Directory.Delete(_repoDirectory, recursive: true);
        }
    }

    private static void ResetAttributesForDeletion(string directory)
    {
        File.SetAttributes(directory, FileAttributes.Normal);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}

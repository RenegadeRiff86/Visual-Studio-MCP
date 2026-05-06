using System.Text.Json.Nodes;
using VsIdeBridge.Tooling.Documents;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class ReadSliceCollectionTests
{
    private const int DefaultChunkSize = 10;
    private const int FirstChunkIndex = 0;
    private const int SecondChunkIndex = 1;
    private const int OutOfRangeChunkIndex = 99;
    private const int AllRowsChunkSize = 0;
    private const int SliceCount = 3;

    [Fact]
    public void FiltersByPathAndText()
    {
        ReadSliceCollection collection = ReadSliceCollection.FromJsonObject(CreateBucket());
        ReadQueryOptions options = new(DefaultChunkSize, FirstChunkIndex, null, false, "Tooling", "needle", null);

        JsonObject result = collection.ToJsonObject(options, CreateBucket());
        JsonArray slices = Assert.IsType<JsonArray>(result[ReadJsonNames.Slices]);
        JsonObject slice = Assert.IsType<JsonObject>(slices.Single());

        Assert.Equal(1, result[ReadJsonNames.Count]!.GetValue<int>());
        Assert.Equal(1, result[ReadJsonNames.FilteredCount]!.GetValue<int>());
        Assert.Equal("src/Tooling/Search.cs", slice[ReadJsonNames.ResolvedPath]!.GetValue<string>());
        Assert.Equal("keep-me", slice["authority"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("path", "src/Alpha.cs", "src/Zeta.cs")]
    [InlineData("name", "Alpha.cs", "Zeta.cs")]
    [InlineData("requestedStartLine", "2", "30")]
    [InlineData("requestedEndLine", "8", "50")]
    [InlineData("actualStartLine", "2", "30")]
    [InlineData("actualEndLine", "8", "50")]
    [InlineData("lineCount", "7", "21")]
    [InlineData("text", "aaa", "zzz")]
    public void SortsAscendingAndDescending(string sortBy, string ascendingFirst, string descendingFirst)
    {
        ReadSliceCollection collection = ReadSliceCollection.FromJsonObject(CreateBucket());

        JsonObject ascending = collection.ToJsonObject(CreateSortOptions(sortBy, false), CreateBucket());
        JsonObject descending = collection.ToJsonObject(CreateSortOptions(sortBy, true), CreateBucket());

        Assert.Equal(ascendingFirst, GetSortValue(ascending, sortBy));
        Assert.Equal(descendingFirst, GetSortValue(descending, sortBy));
        Assert.Equal("desc", descending[ReadJsonNames.SortDirection]!.GetValue<string>());
    }

    [Fact]
    public void PagesRowsAndKeepsChunkMetadataStable()
    {
        ReadSliceCollection collection = ReadSliceCollection.FromJsonObject(CreateBucket());
        JsonObject page = collection.ToJsonObject(new(1, SecondChunkIndex, "path", false, null, null, null), CreateBucket());

        JsonArray slices = Assert.IsType<JsonArray>(page[ReadJsonNames.Slices]);
        Assert.Single(slices);
        Assert.Equal(SliceCount, page[ReadJsonNames.FilteredCount]!.GetValue<int>());
        Assert.Equal(SliceCount, page[ReadJsonNames.ChunkCount]!.GetValue<int>());
        Assert.Equal(SecondChunkIndex, page[ReadJsonNames.ChunkStart]!.GetValue<int>());
        Assert.Equal(SecondChunkIndex + 1, page[ReadJsonNames.ChunkEnd]!.GetValue<int>());
        Assert.True(page[ReadJsonNames.HasMoreChunks]!.GetValue<bool>());
    }

    [Fact]
    public void ChunkSizeZeroReturnsAllRowsAsOneChunk()
    {
        ReadSliceCollection collection = ReadSliceCollection.FromJsonObject(CreateBucket());
        JsonObject result = collection.ToJsonObject(new(AllRowsChunkSize, FirstChunkIndex, "path", false, null, null, null), CreateBucket());

        Assert.Equal(SliceCount, result[ReadJsonNames.Count]!.GetValue<int>());
        Assert.Equal(1, result[ReadJsonNames.ChunkCount]!.GetValue<int>());
        Assert.Equal(AllRowsChunkSize, result[ReadJsonNames.ChunkSize]!.GetValue<int>());
        Assert.False(result[ReadJsonNames.HasMoreChunks]!.GetValue<bool>());
    }

    [Fact]
    public void OutOfRangeChunkReturnsEmptyRowsAndFlag()
    {
        ReadSliceCollection collection = ReadSliceCollection.FromJsonObject(CreateBucket());
        JsonObject result = collection.ToJsonObject(new(1, OutOfRangeChunkIndex, "path", false, null, null, null), CreateBucket());

        JsonArray slices = Assert.IsType<JsonArray>(result[ReadJsonNames.Slices]);
        Assert.Empty(slices);
        Assert.Equal(SliceCount, result[ReadJsonNames.ChunkStart]!.GetValue<int>());
        Assert.Equal(SliceCount, result[ReadJsonNames.ChunkEnd]!.GetValue<int>());
        Assert.True(result[ReadJsonNames.ChunkOutOfRange]!.GetValue<bool>());
    }

    [Fact]
    public void SerializationPreservesMetadataAndGroups()
    {
        ReadSliceCollection collection = ReadSliceCollection.FromJsonObject(CreateBucket());
        JsonObject result = collection.ToJsonObject(new(2, FirstChunkIndex, "path", false, null, null, "file"), CreateBucket());

        Assert.NotNull(result[ReadJsonNames.Slices]);
        Assert.NotNull(result[ReadJsonNames.Count]);
        Assert.NotNull(result[ReadJsonNames.TotalCount]);
        Assert.NotNull(result[ReadJsonNames.FilteredCount]);
        Assert.NotNull(result[ReadJsonNames.ChunkIndex]);
        Assert.NotNull(result[ReadJsonNames.ChunkSize]);
        Assert.NotNull(result[ReadJsonNames.Groups]);
    }

    private static string GetSortValue(JsonObject result, string sortBy)
    {
        JsonArray slices = Assert.IsType<JsonArray>(result[ReadJsonNames.Slices]);
        JsonObject first = Assert.IsType<JsonObject>(slices.First());
        return sortBy switch
        {
            "path" => first[ReadJsonNames.ResolvedPath]!.GetValue<string>(),
            "name" => System.IO.Path.GetFileName(first[ReadJsonNames.ResolvedPath]!.GetValue<string>()),
            "requestedStartLine" => first[ReadJsonNames.RequestedStartLine]!.GetValue<int>().ToString(),
            "requestedEndLine" => first[ReadJsonNames.RequestedEndLine]!.GetValue<int>().ToString(),
            "actualStartLine" => first[ReadJsonNames.ActualStartLine]!.GetValue<int>().ToString(),
            "actualEndLine" => first[ReadJsonNames.ActualEndLine]!.GetValue<int>().ToString(),
            "lineCount" => first[ReadJsonNames.LineCount]!.GetValue<int>().ToString(),
            _ => first[ReadJsonNames.Text]!.GetValue<string>(),
        };
    }

    private static ReadQueryOptions CreateSortOptions(string sortBy, bool sortDescending)
        => new(DefaultChunkSize, FirstChunkIndex, sortBy, sortDescending, null, null, null);

    private static JsonObject CreateBucket()
        => new()
        {
            [ReadJsonNames.Count] = SliceCount,
            [ReadJsonNames.TotalCount] = SliceCount,
            [ReadJsonNames.Slices] = new JsonArray
            {
                CreateSlice("src/Tooling/Search.cs", 10, 20, "middle needle", "keep-me"),
                CreateSlice("src/Alpha.cs", 2, 8, "aaa", null),
                CreateSlice("src/Zeta.cs", 30, 50, "zzz", null),
            },
        };

    private static JsonObject CreateSlice(string path, int start, int end, string text, string? authority)
    {
        JsonObject slice = new()
        {
            [ReadJsonNames.ResolvedPath] = path,
            [ReadJsonNames.RequestedStartLine] = start,
            [ReadJsonNames.RequestedEndLine] = end,
            [ReadJsonNames.ActualStartLine] = start,
            [ReadJsonNames.ActualEndLine] = end,
            [ReadJsonNames.LineCount] = end - start + 1,
            [ReadJsonNames.Text] = text,
            [ReadJsonNames.RevealedInEditor] = true,
        };

        if (authority is not null)
        {
            slice["authority"] = authority;
        }

        return slice;
    }
}

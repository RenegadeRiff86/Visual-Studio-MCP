using System.Text.Json.Nodes;
using VsIdeBridge.Tooling.Search;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class SearchResultCollectionTests
{
    private const int DefaultChunkSize = 10;
    private const int AllRowsChunkSize = 0;
    private const int FirstChunkIndex = 0;
    private const int SecondChunkIndex = 1;
    private const int OutOfRangeChunkIndex = 99;
    private const int RowCount = 3;
    private static readonly int SecondChunkEnd = SecondChunkIndex + 1;
    private static readonly int PrimitiveFilteredCount = SecondChunkEnd;
    private static readonly int AlphaLine = SecondChunkEnd;

    [Fact]
    public void FiltersByProjectPathKindSourceAndText()
    {
        SearchResultCollection collection = SearchResultCollection.FromJsonObject(CreateMatchBucket(), SearchJsonNames.Matches);
        SearchQueryOptions options = new(
            chunkSize: DefaultChunkSize,
            chunkIndex: FirstChunkIndex,
            sortBy: null,
            sortDescending: false,
            project: "Service",
            path: "Tooling",
            kind: "class",
            source: "roslyn",
            text: "serializer",
            groupBy: null);

        JsonObject result = collection.ToJsonObject(options, CreateMatchBucket(), SearchJsonNames.Matches);
        JsonArray rows = Assert.IsType<JsonArray>(result[SearchJsonNames.Matches]);
        JsonObject row = Assert.IsType<JsonObject>(rows.Single());

        Assert.Equal(1, result[SearchJsonNames.Count]!.GetValue<int>());
        Assert.Equal(1, result[SearchJsonNames.FilteredCount]!.GetValue<int>());
        Assert.Equal("SearchResultCollection", row[SearchJsonNames.Name]!.GetValue<string>());
        Assert.Equal("keep-me", row["authority"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("path", "src/Alpha.cs", "src/Zeta.cs")]
    [InlineData("name", "AlphaTool", "ZetaTool")]
    [InlineData("project", "AlphaProject", "ZetaProject")]
    [InlineData("kind", "class", "property")]
    [InlineData("source", "bridge", "roslyn")]
    [InlineData("line", "2", "40")]
    [InlineData("column", "1", "9")]
    [InlineData("score", "10", "90")]
    [InlineData("text", "aaa", "zzz")]
    [InlineData("preview", "alpha preview", "zeta preview")]
    [InlineData("message", "alpha message", "zeta message")]
    [InlineData("signature", "AlphaTool()", "ZetaTool()")]
    public void SortsAscendingAndDescending(string sortBy, string ascendingFirst, string descendingFirst)
    {
        SearchResultCollection collection = SearchResultCollection.FromJsonObject(CreateMatchBucket(), SearchJsonNames.Matches);

        JsonObject ascending = collection.ToJsonObject(CreateSortOptions(sortBy, sortDescending: false), CreateMatchBucket(), SearchJsonNames.Matches);
        JsonObject descending = collection.ToJsonObject(CreateSortOptions(sortBy, sortDescending: true), CreateMatchBucket(), SearchJsonNames.Matches);

        Assert.Equal(ascendingFirst, GetSortValue(ascending, sortBy));
        Assert.Equal(descendingFirst, GetSortValue(descending, sortBy));
        Assert.Equal(sortBy is "file" ? "path" : sortBy, ascending[SearchJsonNames.SortBy]!.GetValue<string>());
        Assert.Equal("desc", descending[SearchJsonNames.SortDirection]!.GetValue<string>());
    }

    [Fact]
    public void PagesRowsAndKeepsChunkMetadataStable()
    {
        SearchResultCollection collection = SearchResultCollection.FromJsonObject(CreateMatchBucket(), SearchJsonNames.Matches);
        JsonObject firstPage = collection.ToJsonObject(new(1, FirstChunkIndex, "name", false, null, null, null, null, null, null), CreateMatchBucket(), SearchJsonNames.Matches);
        JsonObject secondPage = collection.ToJsonObject(new(1, SecondChunkIndex, "name", false, null, null, null, null, null, null), CreateMatchBucket(), SearchJsonNames.Matches);

        Assert.Equal(1, firstPage[SearchJsonNames.Count]!.GetValue<int>());
        Assert.Equal(RowCount, firstPage[SearchJsonNames.FilteredCount]!.GetValue<int>());
        Assert.Equal(RowCount, firstPage[SearchJsonNames.ChunkCount]!.GetValue<int>());
        Assert.Equal(FirstChunkIndex, firstPage[SearchJsonNames.ChunkStart]!.GetValue<int>());
        Assert.Equal(1, firstPage[SearchJsonNames.ChunkEnd]!.GetValue<int>());
        Assert.True(firstPage[SearchJsonNames.HasMoreChunks]!.GetValue<bool>());

        Assert.Equal(SecondChunkIndex, secondPage[SearchJsonNames.ChunkStart]!.GetValue<int>());
        Assert.Equal(SecondChunkEnd, secondPage[SearchJsonNames.ChunkEnd]!.GetValue<int>());
    }

    [Fact]
    public void ChunkSizeZeroReturnsAllRowsAsOneChunk()
    {
        SearchResultCollection collection = SearchResultCollection.FromJsonObject(CreateMatchBucket(), SearchJsonNames.Matches);
        JsonObject result = collection.ToJsonObject(new(AllRowsChunkSize, FirstChunkIndex, "name", false, null, null, null, null, null, null), CreateMatchBucket(), SearchJsonNames.Matches);

        Assert.Equal(RowCount, result[SearchJsonNames.Count]!.GetValue<int>());
        Assert.Equal(RowCount, result[SearchJsonNames.FilteredCount]!.GetValue<int>());
        Assert.Equal(1, result[SearchJsonNames.ChunkCount]!.GetValue<int>());
        Assert.Equal(AllRowsChunkSize, result[SearchJsonNames.ChunkSize]!.GetValue<int>());
        Assert.False(result[SearchJsonNames.HasMoreChunks]!.GetValue<bool>());
    }

    [Fact]
    public void OutOfRangeChunkReturnsEmptyRowsAndFlag()
    {
        SearchResultCollection collection = SearchResultCollection.FromJsonObject(CreateMatchBucket(), SearchJsonNames.Matches);
        JsonObject result = collection.ToJsonObject(new(1, OutOfRangeChunkIndex, "name", false, null, null, null, null, null, null), CreateMatchBucket(), SearchJsonNames.Matches);

        JsonArray rows = Assert.IsType<JsonArray>(result[SearchJsonNames.Matches]);
        Assert.Empty(rows);
        Assert.Equal(RowCount, result[SearchJsonNames.ChunkStart]!.GetValue<int>());
        Assert.Equal(RowCount, result[SearchJsonNames.ChunkEnd]!.GetValue<int>());
        Assert.True(result[SearchJsonNames.ChunkOutOfRange]!.GetValue<bool>());
        Assert.True(result[SearchJsonNames.Truncated]!.GetValue<bool>());
    }

    [Fact]
    public void PrimitiveStringRowsCanBeFilteredSortedAndPaged()
    {
        JsonObject bucket = new()
        {
            [SearchJsonNames.Count] = RowCount,
            [SearchJsonNames.Files] = new JsonArray("src/A.cs", "docs/readme.md", "src/B.cs"),
        };

        SearchResultCollection collection = SearchResultCollection.FromJsonObject(bucket, SearchJsonNames.Files);
        JsonObject result = collection.ToJsonObject(new(1, FirstChunkIndex, "path", true, null, null, null, null, "src/", null), bucket, SearchJsonNames.Files);

        JsonArray files = Assert.IsType<JsonArray>(result[SearchJsonNames.Files]);
        Assert.Equal("src/B.cs", files.Single()!.GetValue<string>());
        Assert.Equal(1, result[SearchJsonNames.Count]!.GetValue<int>());
        Assert.Equal(PrimitiveFilteredCount, result[SearchJsonNames.FilteredCount]!.GetValue<int>());
        Assert.True(result[SearchJsonNames.HasMoreChunks]!.GetValue<bool>());
    }

    [Fact]
    public void SerializationPreservesItemNameMetadataAndGroups()
    {
        SearchResultCollection collection = SearchResultCollection.FromJsonObject(CreateMatchBucket(), SearchJsonNames.Matches);
        JsonObject result = collection.ToJsonObject(new(2, FirstChunkIndex, "name", false, null, null, null, null, null, "kind"), CreateMatchBucket(), SearchJsonNames.Matches);

        Assert.NotNull(result[SearchJsonNames.Matches]);
        Assert.NotNull(result[SearchJsonNames.Count]);
        Assert.NotNull(result[SearchJsonNames.TotalCount]);
        Assert.NotNull(result[SearchJsonNames.FilteredCount]);
        Assert.NotNull(result[SearchJsonNames.ChunkIndex]);
        Assert.NotNull(result[SearchJsonNames.ChunkSize]);
        Assert.NotNull(result[SearchJsonNames.ChunkCount]);
        Assert.NotNull(result[SearchJsonNames.ChunkStart]);
        Assert.NotNull(result[SearchJsonNames.ChunkEnd]);
        Assert.NotNull(result[SearchJsonNames.HasMoreChunks]);
        Assert.NotNull(result[SearchJsonNames.Truncated]);
        Assert.Equal("name", result[SearchJsonNames.SortBy]!.GetValue<string>());
        Assert.Equal("asc", result[SearchJsonNames.SortDirection]!.GetValue<string>());

        JsonArray groups = Assert.IsType<JsonArray>(result[SearchJsonNames.Groups]);
        Assert.Contains(groups.OfType<JsonObject>(), group => group[SearchJsonNames.Key]?.GetValue<string>() == "class");
    }

    private static string GetSortValue(JsonObject result, string sortBy)
    {
        JsonArray rows = Assert.IsType<JsonArray>(result[SearchJsonNames.Matches]);
        JsonObject first = Assert.IsType<JsonObject>(rows.First());
        return sortBy switch
        {
            "path" => first[SearchJsonNames.Path]!.GetValue<string>(),
            "line" => first[SearchJsonNames.Line]!.GetValue<int>().ToString(),
            "column" => first[SearchJsonNames.Column]!.GetValue<int>().ToString(),
            "score" => first[SearchJsonNames.Score]!.GetValue<int>().ToString(),
            _ => first[sortBy]!.GetValue<string>(),
        };
    }

    private static SearchQueryOptions CreateSortOptions(string sortBy, bool sortDescending)
    {
        JsonObject args = new()
        {
            ["chunk_size"] = DefaultChunkSize,
            ["chunk_index"] = FirstChunkIndex,
            ["sort_by"] = sortBy,
            ["sort_direction"] = sortDescending ? "desc" : "asc",
        };

        return SearchQueryOptions.FromJsonObject(args, DefaultChunkSize);
    }

    private static JsonObject CreateMatchBucket()
    {
        return new JsonObject
        {
            [SearchJsonNames.Count] = RowCount,
            [SearchJsonNames.TotalCount] = RowCount,
            [SearchJsonNames.Matches] = new JsonArray
            {
                new JsonObject
                {
                    [SearchJsonNames.Path] = "src/Tooling/Search.cs",
                    [SearchJsonNames.Name] = "SearchResultCollection",
                    [SearchJsonNames.FullName] = "VsIdeBridge.Tooling.Search.SearchResultCollection",
                    [SearchJsonNames.Project] = "VsIdeBridgeService",
                    [SearchJsonNames.Kind] = "class",
                    [SearchJsonNames.Source] = "roslyn",
                    [SearchJsonNames.Line] = 12,
                    [SearchJsonNames.Column] = 3,
                    [SearchJsonNames.Score] = 30,
                    [SearchJsonNames.Text] = "middle",
                    [SearchJsonNames.Preview] = "serializer search container",
                    [SearchJsonNames.Message] = "middle message",
                    [SearchJsonNames.Signature] = "class SearchResultCollection",
                    ["authority"] = "keep-me",
                },
                new JsonObject
                {
                    [SearchJsonNames.Path] = "src/Alpha.cs",
                    [SearchJsonNames.Name] = "AlphaTool",
                    [SearchJsonNames.FullName] = "AlphaTool",
                    [SearchJsonNames.Project] = "AlphaProject",
                    [SearchJsonNames.Kind] = "method",
                    [SearchJsonNames.Source] = "bridge",
                    [SearchJsonNames.Line] = AlphaLine,
                    [SearchJsonNames.Column] = 1,
                    [SearchJsonNames.Score] = 10,
                    [SearchJsonNames.Text] = "aaa",
                    [SearchJsonNames.Preview] = "alpha preview",
                    [SearchJsonNames.Message] = "alpha message",
                    [SearchJsonNames.Signature] = "AlphaTool()",
                },
                new JsonObject
                {
                    [SearchJsonNames.Path] = "src/Zeta.cs",
                    [SearchJsonNames.Name] = "ZetaTool",
                    [SearchJsonNames.FullName] = "ZetaTool",
                    [SearchJsonNames.Project] = "ZetaProject",
                    [SearchJsonNames.Kind] = "property",
                    [SearchJsonNames.Source] = "csharp",
                    [SearchJsonNames.Line] = 40,
                    [SearchJsonNames.Column] = 9,
                    [SearchJsonNames.Score] = 90,
                    [SearchJsonNames.Text] = "zzz",
                    [SearchJsonNames.Preview] = "zeta preview",
                    [SearchJsonNames.Message] = "zeta message",
                    [SearchJsonNames.Signature] = "ZetaTool()",
                },
            },
        };
    }
}

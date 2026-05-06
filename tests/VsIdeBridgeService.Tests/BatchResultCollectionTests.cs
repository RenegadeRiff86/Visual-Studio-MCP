using System.Text.Json.Nodes;
using VsIdeBridge.Tooling.Batch;
using Xunit;

namespace VsIdeBridgeService.Tests;

public sealed class BatchResultCollectionTests
{
    private const int DefaultChunkSize = 10;
    private const int FirstChunkIndex = 0;
    private const int SecondChunkIndex = 1;
    private const int AllRowsChunkSize = 0;
    private const int StepCount = 3;
    private const int NestedRowCount = 3;
    private const string WarningsCommand = BatchJsonNames.Warnings;

    [Fact]
    public void FiltersByCommandSuccessAndText()
    {
        BatchResultCollection collection = BatchResultCollection.FromJsonObject(CreateBatchData());
        BatchQueryOptions options = new(DefaultChunkSize, FirstChunkIndex, null, false, WarningsCommand, true, "captured", null, BatchDataMode.Summary);

        JsonObject result = collection.ToJsonObject(options, CreateBatchData());
        JsonArray rows = Assert.IsType<JsonArray>(result[BatchJsonNames.Results]);
        JsonObject row = Assert.IsType<JsonObject>(rows.Single());

        Assert.Equal(1, result[BatchJsonNames.Count]!.GetValue<int>());
        Assert.Equal(WarningsCommand, row[BatchJsonNames.Command]!.GetValue<string>());
        Assert.Equal(1, result[BatchJsonNames.FilteredSuccessCount]!.GetValue<int>());
    }

    [Theory]
    [InlineData("index", "0", "2")]
    [InlineData("id", "alpha", "gamma")]
    [InlineData("command", "errors", WarningsCommand)]
    [InlineData("success", "False", "True")]
    [InlineData("summary", "Captured warnings", "No errors")]
    [InlineData(BatchJsonNames.Warnings, "0", "2")]
    [InlineData("error", "", "unknown_command")]
    public void SortsAscendingAndDescending(string sortBy, string ascendingFirst, string descendingFirst)
    {
        BatchResultCollection collection = BatchResultCollection.FromJsonObject(CreateBatchData());

        JsonObject ascending = collection.ToJsonObject(CreateSortOptions(sortBy, false), CreateBatchData());
        JsonObject descending = collection.ToJsonObject(CreateSortOptions(sortBy, true), CreateBatchData());

        Assert.Equal(ascendingFirst, GetSortValue(ascending, sortBy));
        Assert.Equal(descendingFirst, GetSortValue(descending, sortBy));
    }

    [Fact]
    public void PagesRowsAndKeepsChunkMetadataStable()
    {
        BatchResultCollection collection = BatchResultCollection.FromJsonObject(CreateBatchData());
        JsonObject result = collection.ToJsonObject(new(1, SecondChunkIndex, "index", false, null, null, null, null, BatchDataMode.Summary), CreateBatchData());

        JsonArray rows = Assert.IsType<JsonArray>(result[BatchJsonNames.Results]);
        Assert.Single(rows);
        Assert.Equal(StepCount, result[BatchJsonNames.FilteredCount]!.GetValue<int>());
        Assert.Equal(StepCount, result[BatchJsonNames.ChunkCount]!.GetValue<int>());
        Assert.Equal(SecondChunkIndex, result[BatchJsonNames.ChunkStart]!.GetValue<int>());
        Assert.Equal(SecondChunkIndex + 1, result[BatchJsonNames.ChunkEnd]!.GetValue<int>());
        Assert.True(result[BatchJsonNames.HasMoreChunks]!.GetValue<bool>());
    }

    [Fact]
    public void ChunkSizeZeroReturnsAllRowsAsOneChunk()
    {
        BatchResultCollection collection = BatchResultCollection.FromJsonObject(CreateBatchData());
        JsonObject result = collection.ToJsonObject(new(AllRowsChunkSize, FirstChunkIndex, "index", false, null, null, null, null, BatchDataMode.Summary), CreateBatchData());

        Assert.Equal(StepCount, result[BatchJsonNames.Count]!.GetValue<int>());
        Assert.Equal(1, result[BatchJsonNames.ChunkCount]!.GetValue<int>());
        Assert.Equal(AllRowsChunkSize, result[BatchJsonNames.ChunkSize]!.GetValue<int>());
    }

    [Fact]
    public void SummaryDataModeCompactsNestedStepData()
    {
        BatchResultCollection collection = BatchResultCollection.FromJsonObject(CreateBatchData());
        JsonObject result = collection.ToJsonObject(new(DefaultChunkSize, FirstChunkIndex, "index", false, null, null, null, null, BatchDataMode.Summary), CreateBatchData());

        JsonObject first = FirstRow(result);
        JsonObject data = Assert.IsType<JsonObject>(first[BatchJsonNames.StepData]);
        JsonObject rowsSummary = Assert.IsType<JsonObject>(data["rows"]);

        Assert.Equal("summary", result[BatchJsonNames.DataMode]!.GetValue<string>());
        Assert.Equal("array", rowsSummary["kind"]!.GetValue<string>());
        Assert.Equal(NestedRowCount, rowsSummary[BatchJsonNames.Count]!.GetValue<int>());
        Assert.NotNull(rowsSummary["last"]);
    }

    [Fact]
    public void FullDataModePreservesNestedStepData()
    {
        BatchResultCollection collection = BatchResultCollection.FromJsonObject(CreateBatchData());
        JsonObject result = collection.ToJsonObject(new(DefaultChunkSize, FirstChunkIndex, "index", false, null, null, null, null, BatchDataMode.Full), CreateBatchData());

        JsonObject first = FirstRow(result);
        JsonObject data = Assert.IsType<JsonObject>(first[BatchJsonNames.StepData]);
        JsonArray rows = Assert.IsType<JsonArray>(data["rows"]);

        Assert.Equal(NestedRowCount, rows.Count);
        Assert.Equal("full", result[BatchJsonNames.DataMode]!.GetValue<string>());
    }

    [Fact]
    public void GroupsByCommandSuccessOrError()
    {
        BatchResultCollection collection = BatchResultCollection.FromJsonObject(CreateBatchData());
        JsonObject result = collection.ToJsonObject(new(DefaultChunkSize, FirstChunkIndex, "index", false, null, null, null, "success", BatchDataMode.Summary), CreateBatchData());

        JsonArray groups = Assert.IsType<JsonArray>(result[BatchJsonNames.Groups]);
        Assert.Contains(groups.OfType<JsonObject>(), group => group[BatchJsonNames.Key]?.GetValue<string>() == "True");
        Assert.Contains(groups.OfType<JsonObject>(), group => group[BatchJsonNames.Key]?.GetValue<string>() == "False");
    }

    private static JsonObject FirstRow(JsonObject result)
    {
        JsonArray rows = Assert.IsType<JsonArray>(result[BatchJsonNames.Results]);
        return Assert.IsType<JsonObject>(rows.First());
    }

    private static string GetSortValue(JsonObject result, string sortBy)
    {
        JsonObject first = FirstRow(result);
        return sortBy switch
        {
            "index" => first[BatchJsonNames.Index]!.GetValue<int>().ToString(),
            "success" => first[BatchJsonNames.Success]!.GetValue<bool>().ToString(),
            BatchJsonNames.Warnings => Assert.IsType<JsonArray>(first[BatchJsonNames.Warnings]).Count.ToString(),
            "error" => first[BatchJsonNames.Error]?["code"]?.GetValue<string>() ?? string.Empty,
            _ => first[sortBy]!.GetValue<string>(),
        };
    }

    private static BatchQueryOptions CreateSortOptions(string sortBy, bool sortDescending)
        => new(DefaultChunkSize, FirstChunkIndex, sortBy, sortDescending, null, null, null, null, BatchDataMode.Summary);

    private static JsonObject CreateBatchData()
        => new()
        {
            [BatchJsonNames.BatchCount] = StepCount,
            [BatchJsonNames.SuccessCount] = 2,
            [BatchJsonNames.FailureCount] = 1,
            [BatchJsonNames.StoppedEarly] = false,
            [BatchJsonNames.Results] = new JsonArray
            {
                CreateStep(0, "alpha", WarningsCommand, true, "Captured warnings", 2, null),
                CreateStep(1, "beta", "errors", true, "No errors", 0, null),
                CreateStep(2, "gamma", "missing", false, "Missing command", 0, "unknown_command"),
            },
        };

    private static JsonObject CreateStep(int index, string id, string command, bool success, string summary, int warningCount, string? errorCode)
    {
        JsonArray warnings = [];
        for (int i = 0; i < warningCount; i++)
        {
            warnings.Add($"warning-{i}");
        }

        JsonObject step = new()
        {
            [BatchJsonNames.Index] = index,
            [BatchJsonNames.Id] = id,
            [BatchJsonNames.Command] = command,
            [BatchJsonNames.Success] = success,
            [BatchJsonNames.Summary] = summary,
            [BatchJsonNames.Warnings] = warnings,
            [BatchJsonNames.StepData] = new JsonObject
            {
                ["rows"] = new JsonArray("first", "middle", "last"),
                ["count"] = NestedRowCount,
            },
        };

        step[BatchJsonNames.Error] = errorCode is null
            ? null
            : new JsonObject
            {
                ["code"] = errorCode,
                ["message"] = "Tool was not registered.",
            };

        return step;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ParallelSearch.Api;

namespace ParallelSearch.Tests;

public sealed class IndexEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CorpusDirectoryConfigurationKey = "Corpus:DirectoryPath";
    private const string CorpusMaxDocumentsConfigurationKey = "Corpus:MaxDocuments";
    private readonly WebApplicationFactory<Program> _factory;

    public IndexEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostIndex_WithConfiguredCorpus_ReturnsBuildSummary()
    {
        using var corpus = CreateCorpus(documentCount: 3);
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement json = await ReadJson(response);
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal(3, json.GetProperty("documents_indexed").GetInt32());
        AssertNonNegativeNumber(json.GetProperty("time_ms"));
    }

    [Fact]
    public async Task GetStats_AfterIndexing_ReturnsCurrentBuildStatistics()
    {
        using var corpus = CreateCorpus(documentCount: 4);
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage indexResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);

        using HttpResponseMessage statsResponse = await client.GetAsync("/api/stats");

        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);

        JsonElement json = await ReadJson(statsResponse);
        Assert.Equal(4, json.GetProperty("documents").GetInt32());
        AssertNonNegativeNumber(json.GetProperty("index_build_time_ms"));
        Assert.Equal(2, json.GetProperty("threads_used").GetInt32());
    }

    [Fact]
    public async Task PostIndex_RebuildWithDifferentThreadCount_UpdatesStatistics()
    {
        using var corpus = CreateCorpus(documentCount: 5);
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage firstIndexResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 1 });
        Assert.Equal(HttpStatusCode.OK, firstIndexResponse.StatusCode);

        using HttpResponseMessage firstStatsResponse = await client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, firstStatsResponse.StatusCode);
        JsonElement firstStats = await ReadJson(firstStatsResponse);
        Assert.Equal(5, firstStats.GetProperty("documents").GetInt32());
        Assert.Equal(1, firstStats.GetProperty("threads_used").GetInt32());

        using HttpResponseMessage secondIndexResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 4 });
        Assert.Equal(HttpStatusCode.OK, secondIndexResponse.StatusCode);

        using HttpResponseMessage secondStatsResponse = await client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, secondStatsResponse.StatusCode);
        JsonElement secondStats = await ReadJson(secondStatsResponse);
        Assert.Equal(5, secondStats.GetProperty("documents").GetInt32());
        AssertNonNegativeNumber(secondStats.GetProperty("index_build_time_ms"));
        Assert.Equal(4, secondStats.GetProperty("threads_used").GetInt32());
    }

    [Fact]
    public async Task PostIndex_WithZeroThreads_ReturnsBadRequestWithoutBuildingIndex()
    {
        using var corpus = CreateCorpus(documentCount: 3);
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage indexResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, indexResponse.StatusCode);

        JsonElement errorJson = await ReadJson(indexResponse);
        string? error = errorJson.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));

        using HttpResponseMessage statsResponse = await client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);

        JsonElement statsJson = await ReadJson(statsResponse);
        Assert.Equal(0, statsJson.GetProperty("documents").GetInt32());
        Assert.Equal(0, statsJson.GetProperty("index_build_time_ms").GetDouble());
        Assert.Equal(0, statsJson.GetProperty("threads_used").GetInt32());
    }

    [Fact]
    public async Task PostIndex_WhenRebuildFails_PreservesPreviousSnapshot()
    {
        using var corpus = CreateCorpus(documentCount: 3);
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage successfulIndexResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });
        Assert.Equal(HttpStatusCode.OK, successfulIndexResponse.StatusCode);

        using HttpResponseMessage previousStatsResponse = await client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, previousStatsResponse.StatusCode);
        JsonElement previousStats = await ReadJson(previousStatsResponse);
        double previousBuildTime = previousStats
            .GetProperty("index_build_time_ms")
            .GetDouble();

        corpus.Delete();

        using HttpResponseMessage failedIndexResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 4 });

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            failedIndexResponse.StatusCode);
        JsonElement errorJson = await ReadJson(failedIndexResponse);
        Assert.False(string.IsNullOrWhiteSpace(
            errorJson.GetProperty("error").GetString()));

        using HttpResponseMessage currentStatsResponse = await client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, currentStatsResponse.StatusCode);
        JsonElement currentStats = await ReadJson(currentStatsResponse);
        Assert.Equal(3, currentStats.GetProperty("documents").GetInt32());
        Assert.Equal(
            previousBuildTime,
            currentStats.GetProperty("index_build_time_ms").GetDouble());
        Assert.Equal(2, currentStats.GetProperty("threads_used").GetInt32());
    }

    [Fact]
    public async Task PostIndex_WithNestedDirectories_IndexesRecursiveUniqueRelativeIds()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("root.txt", "rootword");
        corpus.CreateTextFile("first/shared.txt", "firstword");
        corpus.CreateTextFile("second/shared.txt", "secondword");
        corpus.CreateTextFile("first/deeper/third.txt", "deepword");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage indexResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 4 });

        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        JsonElement indexJson = await ReadJson(indexResponse);
        Assert.Equal(4, indexJson.GetProperty("documents_indexed").GetInt32());

        using HttpResponseMessage statsResponse = await client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);
        JsonElement statsJson = await ReadJson(statsResponse);
        Assert.Equal(4, statsJson.GetProperty("documents").GetInt32());

        IndexStateService indexState = factory.Services
            .GetRequiredService<IndexStateService>();
        IndexSnapshot snapshot = indexState.GetSnapshot();

        Assert.Equal(["root.txt"], snapshot.Index.Search("rootword"));
        Assert.Equal(["first/shared.txt"], snapshot.Index.Search("firstword"));
        Assert.Equal(["second/shared.txt"], snapshot.Index.Search("secondword"));
        Assert.Equal(
            ["first/deeper/third.txt"],
            snapshot.Index.Search("deepword"));
    }

    [Fact]
    public async Task PostIndex_WithMaxDocuments_UsesStableDocumentOrder()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("c.txt", "thirdword");
        corpus.CreateTextFile("a.txt", "firstword");
        corpus.CreateTextFile("b.txt", "secondword");
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            maxDocuments: "2");
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.Equal(2, json.GetProperty("documents_indexed").GetInt32());

        IndexSnapshot snapshot = factory.Services
            .GetRequiredService<IndexStateService>()
            .GetSnapshot();
        Assert.Equal(["a.txt"], snapshot.Index.Search("firstword"));
        Assert.Equal(["b.txt"], snapshot.Index.Search("secondword"));
        Assert.Empty(snapshot.Index.Search("thirdword"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task PostIndex_WithInvalidMaxDocuments_ReturnsConfigurationError(
        string maxDocuments)
    {
        using var corpus = CreateCorpus(documentCount: 3);
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            maxDocuments);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        JsonElement error = await ReadJson(response);
        Assert.Contains(
            CorpusMaxDocumentsConfigurationKey,
            error.GetProperty("error").GetString(),
            StringComparison.Ordinal);

        using HttpResponseMessage statsResponse = await client.GetAsync("/api/stats");
        JsonElement stats = await ReadJson(statsResponse);
        Assert.Equal(0, stats.GetProperty("documents").GetInt32());
    }

    [Fact]
    public async Task PostIndex_WithoutMaxDocuments_StillIndexesAllDocuments()
    {
        using var corpus = CreateCorpus(documentCount: 5);
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.Equal(5, json.GetProperty("documents_indexed").GetInt32());
    }

    private WebApplicationFactory<Program> CreateFactory(
        string corpusDirectoryPath,
        string? maxDocuments = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                CorpusDirectoryConfigurationKey,
                corpusDirectoryPath);

            if (maxDocuments is not null)
            {
                builder.UseSetting(
                    CorpusMaxDocumentsConfigurationKey,
                    maxDocuments);
            }
        });
    }

    private static TemporaryCorpus CreateCorpus(int documentCount)
    {
        var corpus = new TemporaryCorpus();

        for (int index = 0; index < documentCount; index++)
        {
            corpus.CreateTextFile(
                $"document-{index}.txt",
                $"parallel document content unique{index}");
        }

        return corpus;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        await using Stream content = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(content);
        return document.RootElement.Clone();
    }

    private static void AssertNonNegativeNumber(JsonElement value)
    {
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.True(value.GetDouble() >= 0);
    }

    private sealed class TemporaryCorpus : IDisposable
    {
        public TemporaryCorpus()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "ParallelSearch.Api.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void CreateTextFile(string fileName, string content)
        {
            string filePath = Path.Combine(DirectoryPath, fileName);
            string? parentDirectory = Path.GetDirectoryName(filePath);

            if (parentDirectory is not null)
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllText(filePath, content);
        }

        public void Delete()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }

        public void Dispose()
        {
            Delete();
        }
    }
}

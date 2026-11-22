using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ParallelSearch.Tests;

public sealed class SearchEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CorpusDirectoryConfigurationKey = "Corpus:DirectoryPath";
    private readonly WebApplicationFactory<Program> _factory;

    public SearchEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostSearch_AfterIndexing_ReturnsMatchingDocumentIds()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("doc-1.txt", "parallel text search");
        corpus.CreateTextFile("doc-2.txt", "parallel algorithms");
        corpus.CreateTextFile("doc-3.txt", "sequential processing");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage response = await Search(client, "parallel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.Equal(
            ["doc-1.txt", "doc-2.txt"],
            GetOrderedDocumentIds(json));
    }

    [Fact]
    public async Task PostSearch_WhenWordIsMissing_ReturnsEmptyResults()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("doc-1.txt", "parallel text search");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage response = await Search(client, "missing");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.Empty(json.GetProperty("results").EnumerateArray());
        AssertNonNegativeNumber(json.GetProperty("time_ms"));
    }

    [Fact]
    public async Task PostSearch_ReturnsRequiredJsonStructure()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("doc-1.txt", "keyword search content");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage response = await Search(client, "keyword");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        JsonElement results = json.GetProperty("results");
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        AssertNonNegativeNumber(json.GetProperty("time_ms"));

        JsonElement result = Assert.Single(results.EnumerateArray());
        Assert.Equal(JsonValueKind.String, result.GetProperty("doc_id").ValueKind);
        Assert.Equal(JsonValueKind.Number, result.GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.String, result.GetProperty("snippet").ValueKind);
    }

    [Fact]
    public async Task PostSearch_IsCaseInsensitive()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("case.txt", "Parallel Search");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage response = await Search(client, "PARALLEL");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.Equal(["case.txt"], GetOrderedDocumentIds(json));
    }

    [Fact]
    public async Task PostSearch_WithMultipleWords_TokenizesTheQuery()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("combined.txt", "parallel text search");
        corpus.CreateTextFile("unrelated.txt", "unrelated content");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage response = await Search(client, "parallel search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.Equal(["combined.txt"], GetOrderedDocumentIds(json));
    }

    [Fact]
    public async Task PostSearch_BeforeFirstIndex_ReturnsEmptyResults()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("not-indexed.txt", "parallel search");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await Search(client, "parallel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.Empty(json.GetProperty("results").EnumerateArray());
        AssertNonNegativeNumber(json.GetProperty("time_ms"));
    }

    [Fact]
    public async Task PostSearch_WithEmptyQuery_ReturnsBadRequest()
    {
        using var corpus = new TemporaryCorpus();
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await Search(client, string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.False(string.IsNullOrWhiteSpace(
            json.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task PostSearch_AfterFailedRebuild_UsesPreviousIndex()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("persistent.txt", "persistent searchable content");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        corpus.Delete();

        using HttpResponseMessage rebuildResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 4 });
        Assert.Equal(HttpStatusCode.InternalServerError, rebuildResponse.StatusCode);

        using HttpResponseMessage searchResponse = await Search(client, "persistent");

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        JsonElement json = await ReadJson(searchResponse);
        Assert.Equal(["persistent.txt"], GetOrderedDocumentIds(json));
    }

    [Fact]
    public async Task PostSearch_WithTwoWords_UsesOrMatching()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("both.txt", "alpha beta");
        corpus.CreateTextFile("first.txt", "alpha only");
        corpus.CreateTextFile("second.txt", "beta only");
        corpus.CreateTextFile("unrelated.txt", "gamma only");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage response = await Search(client, "alpha beta");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.Equal(
            ["both.txt", "first.txt", "second.txt"],
            GetOrderedDocumentIds(json));
    }

    [Fact]
    public async Task PostSearch_RanksByScoreThenDocumentId()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("a-first.txt", "alpha only");
        corpus.CreateTextFile("b-second.txt", "beta only");
        corpus.CreateTextFile("z-both.txt", "alpha beta");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage response = await Search(client, "alpha beta");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJson(response);
        JsonElement[] results = json.GetProperty("results").EnumerateArray().ToArray();

        Assert.Equal(
            ["z-both.txt", "a-first.txt", "b-second.txt"],
            results.Select(result => result.GetProperty("doc_id").GetString()!).ToArray());
        Assert.Equal(
            [1.0, 0.5, 0.5],
            results.Select(result => result.GetProperty("score").GetDouble()).ToArray());
    }

    [Fact]
    public async Task PostSearch_WithRepeatedWords_ReturnsSameResultsAndScores()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("both.txt", "parallel search");
        corpus.CreateTextFile("one.txt", "parallel processing");
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage uniqueResponse = await Search(
            client,
            "parallel search");
        using HttpResponseMessage repeatedResponse = await Search(
            client,
            "parallel parallel search parallel");

        Assert.Equal(HttpStatusCode.OK, uniqueResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeatedResponse.StatusCode);
        JsonElement uniqueJson = await ReadJson(uniqueResponse);
        JsonElement repeatedJson = await ReadJson(repeatedResponse);

        Assert.Equal(
            GetDocumentIdsAndScores(uniqueJson),
            GetDocumentIdsAndScores(repeatedJson));
    }

    [Fact]
    public async Task PostSearch_WithUnsupportedMode_ReturnsBadRequest()
    {
        using var corpus = new TemporaryCorpus();
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/search",
            new
            {
                query = "parallel",
                mode = "semantic"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await ReadJson(response);
        Assert.False(string.IsNullOrWhiteSpace(
            json.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task PostSearch_SnippetIsBoundedAndSurvivesFailedRebuild()
    {
        const string expectedPrefix = "persistent searchable document text";
        string documentText = expectedPrefix + new string('x', 250);
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("persistent.txt", documentText);
        using WebApplicationFactory<Program> factory = CreateFactory(corpus.DirectoryPath);
        using HttpClient client = factory.CreateClient();
        await IndexCorpus(client);

        using HttpResponseMessage firstSearchResponse = await Search(client, "persistent");
        Assert.Equal(HttpStatusCode.OK, firstSearchResponse.StatusCode);
        JsonElement firstSearchJson = await ReadJson(firstSearchResponse);
        JsonElement firstResult = Assert.Single(
            firstSearchJson.GetProperty("results").EnumerateArray());
        string snippet = firstResult.GetProperty("snippet").GetString()!;
        Assert.Contains(expectedPrefix, snippet, StringComparison.Ordinal);
        Assert.True(snippet.Length <= 200);

        corpus.Delete();

        using HttpResponseMessage rebuildResponse = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });
        Assert.Equal(HttpStatusCode.InternalServerError, rebuildResponse.StatusCode);

        using HttpResponseMessage secondSearchResponse = await Search(client, "persistent");
        Assert.Equal(HttpStatusCode.OK, secondSearchResponse.StatusCode);
        JsonElement secondSearchJson = await ReadJson(secondSearchResponse);
        JsonElement secondResult = Assert.Single(
            secondSearchJson.GetProperty("results").EnumerateArray());
        Assert.Equal(snippet, secondResult.GetProperty("snippet").GetString());
    }

    private WebApplicationFactory<Program> CreateFactory(string corpusDirectoryPath)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                CorpusDirectoryConfigurationKey,
                corpusDirectoryPath);
        });
    }

    private static async Task IndexCorpus(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Search(HttpClient client, string query)
    {
        return client.PostAsJsonAsync(
            "/api/search",
            new
            {
                query,
                mode = "keyword"
            });
    }

    private static string[] GetOrderedDocumentIds(JsonElement response)
    {
        return response
            .GetProperty("results")
            .EnumerateArray()
            .Select(result => result.GetProperty("doc_id").GetString()!)
            .OrderBy(documentId => documentId, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string DocumentId, double Score)[] GetDocumentIdsAndScores(
        JsonElement response)
    {
        return response
            .GetProperty("results")
            .EnumerateArray()
            .Select(result => (
                result.GetProperty("doc_id").GetString()!,
                result.GetProperty("score").GetDouble()))
            .ToArray();
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
                "ParallelSearch.Search.Api.Tests",
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

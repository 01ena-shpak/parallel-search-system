using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ParallelSearch.Api;

namespace ParallelSearch.Tests;

public sealed class WebSocketSearchTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CorpusDirectoryConfigurationKey = "Corpus:DirectoryPath";
    private readonly WebApplicationFactory<Program> _factory;

    public WebSocketSearchTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task WebSocketSearch_AcceptsConnection()
    {
        using var corpus = new TemporaryCorpus();
        using var handler = CreateOllamaHandler("concurrency");
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            handler);
        WebSocketClient webSocketClient = factory.Server.CreateWebSocketClient();
        using WebSocket socket = await Connect(webSocketClient);

        Assert.Equal(WebSocketState.Open, socket.State);

        await SendText(socket, "{\"query\":\"\"}");
        await ReceiveJson(socket);
        await ReceiveClose(socket);
    }

    [Fact]
    public async Task WebSocketSearch_SendsStagesInRequiredOrder()
    {
        using var corpus = new TemporaryCorpus();
        using var handler = CreateOllamaHandler("concurrency, threading");
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            handler);
        WebSocketClient webSocketClient = factory.Server.CreateWebSocketClient();
        using WebSocket socket = await Connect(webSocketClient);

        await SendQuery(socket, "parallel computing");

        JsonElement expansion = await ReceiveJson(socket);
        JsonElement search = await ReceiveJson(socket);
        JsonElement done = await ReceiveJson(socket);

        Assert.Equal("query_expansion", expansion.GetProperty("stage").GetString());
        Assert.Equal("keyword_search", search.GetProperty("stage").GetString());
        Assert.Equal("done", done.GetProperty("stage").GetString());
        Assert.Single(done.EnumerateObject());
        Assert.Equal(1, handler.CallCount);
        await ReceiveClose(socket, WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task WebSocketSearch_UsesExpandedTermsForKeywordSearch()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("expanded.txt", "distributed systems architecture");
        using var handler = CreateOllamaHandler("distributed");
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            handler);
        using HttpClient httpClient = factory.CreateClient();
        await IndexCorpus(httpClient);
        WebSocketClient webSocketClient = factory.Server.CreateWebSocketClient();
        using WebSocket socket = await Connect(webSocketClient);

        await SendQuery(socket, "parallel computing");

        JsonElement expansion = await ReceiveJson(socket);
        JsonElement search = await ReceiveJson(socket);
        await ReceiveJson(socket);

        Assert.Equal(
            ["distributed"],
            expansion.GetProperty("data").GetProperty("expanded_terms")
                .EnumerateArray()
                .Select(term => term.GetString()!)
                .ToArray());
        Assert.Equal(
            ["expanded.txt"],
            GetDocumentIds(search));
    }

    [Fact]
    public async Task WebSocketSearch_ReturnsResultsFromCurrentIndex()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("current.txt", "parallel algorithms");
        corpus.CreateTextFile("other.txt", "sequential processing");
        using var handler = CreateOllamaHandler("unrelated");
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            handler);
        using HttpClient httpClient = factory.CreateClient();
        await IndexCorpus(httpClient);
        WebSocketClient webSocketClient = factory.Server.CreateWebSocketClient();
        using WebSocket socket = await Connect(webSocketClient);

        await SendQuery(socket, "parallel");

        await ReceiveJson(socket);
        JsonElement search = await ReceiveJson(socket);
        await ReceiveJson(socket);

        Assert.Contains("current.txt", GetDocumentIds(search));
    }

    [Fact]
    public async Task WebSocketSearch_WithEmptyOrMalformedRequest_SendsErrorAndCloses()
    {
        using var corpus = new TemporaryCorpus();
        using var handler = CreateOllamaHandler("unused");
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            handler);
        WebSocketClient webSocketClient = factory.Server.CreateWebSocketClient();

        foreach (string request in new[] { "{\"query\":\"  \"}", "not-json" })
        {
            using WebSocket socket = await Connect(webSocketClient);
            await SendText(socket, request);

            JsonElement error = await ReceiveJson(socket);

            Assert.Equal("error", error.GetProperty("stage").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                error.GetProperty("error").GetString()));
            await ReceiveClose(socket, WebSocketCloseStatus.InvalidPayloadData);
        }

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task WebSocketSearch_WhenOllamaIsUnavailable_FallsBackToOriginalQuery()
    {
        using var corpus = new TemporaryCorpus();
        corpus.CreateTextFile("fallback.txt", "parallel computing basics");
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable)));
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            handler);
        using HttpClient httpClient = factory.CreateClient();
        await IndexCorpus(httpClient);
        WebSocketClient webSocketClient = factory.Server.CreateWebSocketClient();
        using WebSocket socket = await Connect(webSocketClient);

        await SendQuery(socket, "parallel computing");

        JsonElement expansion = await ReceiveJson(socket);
        JsonElement search = await ReceiveJson(socket);
        JsonElement done = await ReceiveJson(socket);

        JsonElement expansionData = expansion.GetProperty("data");
        Assert.Equal("parallel computing", expansionData.GetProperty("original").GetString());
        Assert.Empty(expansionData.GetProperty("expanded_terms").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(
            expansionData.GetProperty("error").GetString()));
        Assert.Equal(["fallback.txt"], GetDocumentIds(search));
        Assert.Equal("done", done.GetProperty("stage").GetString());
        Assert.Equal(1, handler.CallCount);
        await ReceiveClose(socket, WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task WebSocketSearch_WhenClientCloses_AcknowledgesCloseWithoutCallingOllama()
    {
        using var corpus = new TemporaryCorpus();
        using var handler = CreateOllamaHandler("unused");
        using WebApplicationFactory<Program> factory = CreateFactory(
            corpus.DirectoryPath,
            handler);
        WebSocketClient webSocketClient = factory.Server.CreateWebSocketClient();
        using WebSocket socket = await Connect(webSocketClient);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Client completed.",
            timeout.Token);

        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Equal(0, handler.CallCount);
    }

    private WebApplicationFactory<Program> CreateFactory(
        string corpusDirectoryPath,
        HttpMessageHandler handler)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                CorpusDirectoryConfigurationKey,
                corpusDirectoryPath);
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddHttpClient<OllamaQueryExpander>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
    }

    private static StubHttpMessageHandler CreateOllamaHandler(string response)
    {
        return new StubHttpMessageHandler((_, _) =>
        {
            string json = JsonSerializer.Serialize(new { response });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });
    }

    private static async Task IndexCorpus(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/index",
            new { threads = 2 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<WebSocket> Connect(WebSocketClient client)
    {
        return client.ConnectAsync(
            new Uri("ws://localhost/ws/search"),
            CancellationToken.None);
    }

    private static Task SendQuery(WebSocket socket, string query)
    {
        return SendText(socket, JsonSerializer.Serialize(new { query }));
    }

    private static async Task SendText(WebSocket socket, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] payload = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token);
    }

    private static async Task<JsonElement> ReceiveJson(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var content = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            content.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        content.Position = 0;
        using JsonDocument json = await JsonDocument.ParseAsync(
            content,
            cancellationToken: timeout.Token);
        return json.RootElement.Clone();
    }

    private static async Task ReceiveClose(
        WebSocket socket,
        WebSocketCloseStatus expectedStatus = WebSocketCloseStatus.InvalidPayloadData)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[256];
        WebSocketReceiveResult result = await socket.ReceiveAsync(
            buffer,
            timeout.Token);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(expectedStatus, result.CloseStatus);
    }

    private static string[] GetDocumentIds(JsonElement keywordSearch)
    {
        return keywordSearch
            .GetProperty("data")
            .GetProperty("results")
            .EnumerateArray()
            .Select(result => result.GetProperty("doc_id").GetString()!)
            .ToArray();
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class TemporaryCorpus : IDisposable
    {
        public TemporaryCorpus()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "ParallelSearch.WebSocket.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void CreateTextFile(string fileName, string content)
        {
            File.WriteAllText(Path.Combine(DirectoryPath, fileName), content);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using ParallelSearch.Api;

namespace ParallelSearch.Tests;

public sealed class OllamaQueryExpanderTests
{
    [Fact]
    public async Task ExpandAsync_SendsExpectedGenerateRequest()
    {
        using var handler = new StubHttpMessageHandler(async (
            request,
            cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/generate", request.RequestUri!.AbsolutePath);

            string body = await request.Content!.ReadAsStringAsync(
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement json = document.RootElement;

            Assert.Equal("qwen2.5:0.5b", json.GetProperty("model").GetString());
            Assert.False(json.GetProperty("stream").GetBoolean());

            string prompt = json.GetProperty("prompt").GetString()!;
            Assert.Contains("parallel search", prompt, StringComparison.Ordinal);
            Assert.Contains("up to 5", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "relevant English search terms",
                prompt,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("comma", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "without explanations",
                prompt,
                StringComparison.OrdinalIgnoreCase);

            return CreateJsonResponse("indexing, concurrency");
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://ollama:11434")
        };
        var expander = new OllamaQueryExpander(client);

        await expander.ExpandAsync("parallel search", CancellationToken.None);
    }

    [Fact]
    public async Task ExpandAsync_WithSuccessfulResponse_ReturnsResponseTerms()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse("concurrency, indexing")));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://ollama:11434")
        };
        var expander = new OllamaQueryExpander(client);

        IReadOnlyList<string> terms = await expander.ExpandAsync(
            "parallel search",
            CancellationToken.None);

        Assert.Equal(["concurrency", "indexing"], terms);
    }

    [Fact]
    public async Task ExpandAsync_NormalizesAndFiltersTerms()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(
                " Parallel, , INDEXING, indexing, Threads, SEARCH ")));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://ollama:11434")
        };
        var expander = new OllamaQueryExpander(client);

        IReadOnlyList<string> terms = await expander.ExpandAsync(
            "parallel search",
            CancellationToken.None);

        Assert.Equal(["indexing", "threads"], terms);
    }

    [Fact]
    public async Task ExpandAsync_WhenOllamaReturnsHttpError_PropagatesFailure()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://ollama:11434")
        };
        var expander = new OllamaQueryExpander(client);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => expander.ExpandAsync("parallel search", CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task ExpandAsync_WhenResponseHasMoreThanFiveTerms_ReturnsFirstFive()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(
                "one, two, three, four, five, six, seven")));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://ollama:11434")
        };
        var expander = new OllamaQueryExpander(client);

        IReadOnlyList<string> terms = await expander.ExpandAsync(
            "parallel search",
            CancellationToken.None);

        Assert.Equal(["one", "two", "three", "four", "five"], terms);
    }

    [Fact]
    public async Task ExpandAsync_WhenCancelled_CancelsHttpRequest()
    {
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (
            _,
            cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateJsonResponse("unreachable");
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://ollama:11434")
        };
        var expander = new OllamaQueryExpander(client);
        using var cancellationSource = new CancellationTokenSource();

        Task<IReadOnlyList<string>> expansion = expander.ExpandAsync(
            "parallel search",
            cancellationSource.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await expansion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static HttpResponseMessage CreateJsonResponse(string response)
    {
        string json = JsonSerializer.Serialize(new { response });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}

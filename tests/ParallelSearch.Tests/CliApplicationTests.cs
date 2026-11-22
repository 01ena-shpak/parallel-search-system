using System.Net;
using System.Text;
using System.Text.Json;
using ParallelSearch.Cli;

namespace ParallelSearch.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Health_PrintsServerAndOllamaStatus()
    {
        using var handler = CreateJsonHandler(
            HttpStatusCode.OK,
            """{"status":"ok","ollama":true}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = CreateApplication(handler, output, error);

        int exitCode = await application.RunAsync(["health"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("ok", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ollama", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("available", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(HttpMethod.Get, handler.RequestMethod);
        Assert.Equal("/api/health", handler.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Index_WithThreads_SendsIndexRequestAndPrintsSummary()
    {
        using var handler = CreateJsonHandler(
            HttpStatusCode.OK,
            """{"status":"ok","documents_indexed":12,"time_ms":34.5}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = CreateApplication(handler, output, error);

        int exitCode = await application.RunAsync(["index", "--threads", "4"]);

        Assert.Equal(0, exitCode);
        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(4, request.RootElement.GetProperty("threads").GetInt32());
        Assert.Contains("12", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("34.5", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("/api/index", handler.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Search_WithQuery_SendsKeywordRequestAndPrintsResults()
    {
        using var handler = CreateJsonHandler(
            HttpStatusCode.OK,
            """
            {
              "results": [
                {"doc_id":"doc-1.txt","score":1.0,"snippet":"parallel text"}
              ],
              "time_ms":2.5
            }
            """);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = CreateApplication(handler, output, error);

        int exitCode = await application.RunAsync(
            ["search", "parallel", "computing"]);

        Assert.Equal(0, exitCode);
        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(
            "parallel computing",
            request.RootElement.GetProperty("query").GetString());
        Assert.Equal("keyword", request.RootElement.GetProperty("mode").GetString());
        Assert.Contains("doc-1.txt", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("parallel text", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("/api/search", handler.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ApiUrlOption_OverridesDefaultServerAddress()
    {
        using var handler = CreateJsonHandler(
            HttpStatusCode.OK,
            """{"documents":0,"index_build_time_ms":0,"threads_used":0}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = CreateApplication(handler, output, error);

        int exitCode = await application.RunAsync(
            ["--api-url", "http://api.example:9090", "stats"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("api.example", handler.RequestUri?.Host);
        Assert.Equal(9090, handler.RequestUri?.Port);
        Assert.Equal("/api/stats", handler.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Health_WhenRequestTimesOut_PrintsClearError()
    {
        using var handler = new StubHttpMessageHandler(
            (_, _) => throw new TaskCanceledException("Request timed out."));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = CreateApplication(handler, output, error);

        int exitCode = await application.RunAsync(["health"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("timed out", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static CliApplication CreateApplication(
        StubHttpMessageHandler handler,
        TextWriter output,
        TextWriter error)
    {
        return new CliApplication(
            baseAddress => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = baseAddress
            },
            output,
            error);
    }

    private static StubHttpMessageHandler CreateJsonHandler(
        HttpStatusCode statusCode,
        string json)
    {
        return new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public HttpMethod? RequestMethod { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await responseFactory(request, cancellationToken);
        }
    }
}

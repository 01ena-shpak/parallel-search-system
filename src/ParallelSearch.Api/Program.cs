using ParallelSearch.Api;
using ParallelSearch.Core;

var builder = WebApplication.CreateBuilder(args);

string ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"]
    ?? "http://localhost:11434";

builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri(ollamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddSingleton<ParallelIndexBuilder>();
builder.Services.AddSingleton<IndexStateService>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.MapPost("/api/index", async (
    IndexRequest request,
    IndexStateService indexState,
    CancellationToken cancellationToken) =>
{
    if (request.Threads <= 0)
    {
        return Results.BadRequest(new
        {
            error = "threads must be greater than zero."
        });
    }

    try
    {
        ParallelIndexBuildResult result = await indexState.RebuildAsync(
            request.Threads,
            cancellationToken);

        return Results.Ok(new
        {
            status = "ok",
            documents_indexed = result.DocumentsIndexed,
            time_ms = result.TimeMs
        });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Json(
            new
            {
                error = exception.GetBaseException().Message
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/stats", (IndexStateService indexState) =>
{
    IndexSnapshot snapshot = indexState.GetSnapshot();

    return Results.Ok(new
    {
        documents = snapshot.Documents,
        index_build_time_ms = snapshot.IndexBuildTimeMs,
        threads_used = snapshot.ThreadsUsed
    });
});

app.MapPost("/api/search", (
    SearchRequest request,
    IndexStateService indexState) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest(new
        {
            error = "query must not be empty."
        });
    }

    if (!string.Equals(request.Mode, "keyword", StringComparison.Ordinal))
    {
        return Results.BadRequest(new
        {
            error = "mode must be 'keyword'."
        });
    }

    SearchExecutionResult searchResult = indexState.Search(request.Query);

    return Results.Ok(new
    {
        results = searchResult.Results.Select(result => new
        {
            doc_id = result.DocumentId,
            score = result.Score,
            snippet = result.Snippet
        }),
        time_ms = searchResult.TimeMs
    });
});

app.MapGet("/api/health", async (
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    bool ollamaAvailable;

    try
    {
        HttpClient ollamaClient = httpClientFactory.CreateClient("Ollama");
        using HttpResponseMessage response = await ollamaClient.GetAsync(
            "/api/tags",
            cancellationToken);
        ollamaAvailable = response.IsSuccessStatusCode;
    }
    catch (HttpRequestException)
    {
        ollamaAvailable = false;
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        ollamaAvailable = false;
    }

    return Results.Ok(new
    {
        status = "ok",
        ollama = ollamaAvailable
    });
});

app.Run();

public sealed record IndexRequest(int Threads);

public sealed record SearchRequest(string? Query, string? Mode);

public partial class Program;

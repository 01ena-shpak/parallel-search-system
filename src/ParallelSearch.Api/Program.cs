using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
builder.Services.AddHttpClient<OllamaQueryExpander>(client =>
{
    client.BaseAddress = new Uri(ollamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddSingleton<ParallelIndexBuilder>();
builder.Services.AddSingleton<IndexStateService>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => "Hello World!");

app.MapGet("/ws/search", async (
    HttpContext context,
    OllamaQueryExpander queryExpander,
    IndexStateService indexState) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

    try
    {
        await HandleWebSocketSearch(
            socket,
            queryExpander,
            indexState,
            context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (WebSocketException)
    {
    }
});

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

static async Task HandleWebSocketSearch(
    WebSocket socket,
    OllamaQueryExpander queryExpander,
    IndexStateService indexState,
    CancellationToken cancellationToken)
{
    string? requestJson = await ReceiveTextMessage(socket, cancellationToken);

    if (requestJson is null)
    {
        return;
    }

    WebSocketSearchRequest? request;

    try
    {
        request = JsonSerializer.Deserialize<WebSocketSearchRequest>(
            requestJson,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
    }
    catch (JsonException)
    {
        await SendWebSocketError(
            socket,
            "Request must be valid JSON with a non-empty query.",
            cancellationToken);
        return;
    }

    if (string.IsNullOrWhiteSpace(request?.Query))
    {
        await SendWebSocketError(
            socket,
            "query must not be empty.",
            cancellationToken);
        return;
    }

    string query = request.Query;
    IReadOnlyList<string> expandedTerms = Array.Empty<string>();
    string? expansionError = null;

    try
    {
        expandedTerms = await queryExpander.ExpandAsync(query, cancellationToken);
    }
    catch (Exception exception) when (
        exception is HttpRequestException
        or JsonException
        or KeyNotFoundException
        || exception is OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
    {
        expansionError = exception.GetBaseException().Message;
    }

    if (expansionError is null)
    {
        await SendJson(socket, new
        {
            stage = "query_expansion",
            data = new
            {
                original = query,
                expanded_terms = expandedTerms
            }
        }, cancellationToken);
    }
    else
    {
        await SendJson(socket, new
        {
            stage = "query_expansion",
            data = new
            {
                original = query,
                expanded_terms = Array.Empty<string>(),
                error = expansionError
            }
        }, cancellationToken);
    }

    string expandedQuery = expandedTerms.Count == 0
        ? query
        : $"{query} {string.Join(' ', expandedTerms)}";
    SearchExecutionResult searchResult = indexState.Search(expandedQuery);

    await SendJson(socket, new
    {
        stage = "keyword_search",
        data = new
        {
            results = searchResult.Results.Select(result => new
            {
                doc_id = result.DocumentId,
                score = result.Score,
                snippet = result.Snippet
            }),
            time_ms = searchResult.TimeMs
        }
    }, cancellationToken);

    await SendJson(socket, new
    {
        stage = "done"
    }, cancellationToken);

    if (socket.State == WebSocketState.Open)
    {
        await socket.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "Search completed.",
            cancellationToken);
    }
}

static async Task<string?> ReceiveTextMessage(
    WebSocket socket,
    CancellationToken cancellationToken)
{
    using var content = new MemoryStream();
    var buffer = new byte[4096];
    WebSocketReceiveResult result;

    do
    {
        result = await socket.ReceiveAsync(buffer, cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            if (socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                    result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    result.CloseStatusDescription,
                    cancellationToken);
            }

            return null;
        }

        if (result.MessageType != WebSocketMessageType.Text)
        {
            await SendWebSocketError(
                socket,
                "Request must be a JSON text message.",
                cancellationToken);
            return null;
        }

        content.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    return Encoding.UTF8.GetString(content.ToArray());
}

static async Task SendWebSocketError(
    WebSocket socket,
    string error,
    CancellationToken cancellationToken)
{
    await SendJson(socket, new
    {
        stage = "error",
        error
    }, cancellationToken);

    if (socket.State == WebSocketState.Open)
    {
        await socket.CloseOutputAsync(
            WebSocketCloseStatus.InvalidPayloadData,
            error,
            cancellationToken);
    }
}

static Task SendJson(
    WebSocket socket,
    object message,
    CancellationToken cancellationToken)
{
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);

    return socket.SendAsync(
        payload,
        WebSocketMessageType.Text,
        endOfMessage: true,
        cancellationToken);
}

public sealed record IndexRequest(int Threads);

public sealed record SearchRequest(string? Query, string? Mode);

public sealed record WebSocketSearchRequest(string? Query);

public partial class Program;

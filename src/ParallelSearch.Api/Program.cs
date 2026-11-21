var builder = WebApplication.CreateBuilder(args);

string ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"]
    ?? "http://localhost:11434";

builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri(ollamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(2);
});

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

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

public partial class Program;

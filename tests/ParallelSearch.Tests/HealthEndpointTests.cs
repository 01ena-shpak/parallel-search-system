using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ParallelSearch.Tests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHealth_WhenOllamaIsAvailable_ReturnsTrue()
    {
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using WebApplicationFactory<Program> factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/health");

        await AssertHealthResponse(response, expectedOllama: true);
    }

    [Fact]
    public async Task GetHealth_WhenOllamaIsUnavailable_ReturnsFalse()
    {
        using var handler = new StubHttpMessageHandler(
            (_, _) => throw new HttpRequestException("Ollama is unavailable."));
        using WebApplicationFactory<Program> factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/health");

        await AssertHealthResponse(response, expectedOllama: false);
    }

    [Fact]
    public async Task GetHealth_WhenOllamaReturnsUnsuccessfulStatus_ReturnsFalse()
    {
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using WebApplicationFactory<Program> factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/health");

        await AssertHealthResponse(response, expectedOllama: false);
    }

    [Fact]
    public async Task GetHealth_WhenOllamaRequestTimesOut_ReturnsFalse()
    {
        using var handler = new StubHttpMessageHandler(
            (_, _) => throw new TaskCanceledException("Ollama request timed out."));
        using WebApplicationFactory<Program> factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/health");

        await AssertHealthResponse(response, expectedOllama: false);
    }

    private WebApplicationFactory<Program> CreateFactory(HttpMessageHandler handler)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddHttpClient("Ollama")
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
    }

    private static async Task AssertHealthResponse(
        HttpResponseMessage response,
        bool expectedOllama)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using Stream content = await response.Content.ReadAsStreamAsync();
        using JsonDocument json = await JsonDocument.ParseAsync(content);

        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(expectedOllama, json.RootElement.GetProperty("ollama").GetBoolean());
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responseFactory(request, cancellationToken);
        }
    }
}

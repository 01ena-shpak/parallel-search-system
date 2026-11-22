using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ParallelSearch.Tests;

public sealed class UserInterfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public UserInterfaceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetRoot_ReturnsWebInterface()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/");
        string content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("health-button", content, StringComparison.Ordinal);
        Assert.Contains("index-form", content, StringComparison.Ordinal);
        Assert.Contains("search-form", content, StringComparison.Ordinal);
        Assert.Contains("/app.js", content, StringComparison.Ordinal);

        using HttpResponseMessage scriptResponse = await client.GetAsync("/app.js");
        using HttpResponseMessage styleResponse = await client.GetAsync("/styles.css");
        string script = await scriptResponse.Content.ReadAsStringAsync();
        string styles = await styleResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, scriptResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, styleResponse.StatusCode);
        Assert.Contains("new WebSocket", script, StringComparison.Ordinal);
        Assert.Contains("@media", styles, StringComparison.Ordinal);
    }
}

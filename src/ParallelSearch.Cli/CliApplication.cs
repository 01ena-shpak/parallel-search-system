using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace ParallelSearch.Cli;

public sealed class CliApplication
{
    private const string DefaultApiUrl = "http://localhost:8080";

    private readonly Func<Uri, HttpClient> _httpClientFactory;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public CliApplication(
        Func<Uri, HttpClient> httpClientFactory,
        TextWriter output,
        TextWriter error)
    {
        _httpClientFactory = httpClientFactory
            ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<string> arguments = args.ToList();
        string apiUrl = Environment.GetEnvironmentVariable(
            "PARALLEL_SEARCH_API_URL") ?? DefaultApiUrl;

        if (arguments.Count >= 1
            && string.Equals(arguments[0], "--api-url", StringComparison.Ordinal))
        {
            if (arguments.Count < 2)
            {
                return WriteUsageError("--api-url requires an address.");
            }

            apiUrl = arguments[1];
            arguments.RemoveRange(0, 2);
        }

        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out Uri? baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            return WriteUsageError("API address must be an absolute HTTP or HTTPS URL.");
        }

        if (arguments.Count == 0)
        {
            return WriteUsageError("A command is required.");
        }

        using HttpClient client = _httpClientFactory(baseAddress);

        try
        {
            return arguments[0] switch
            {
                "health" => await RunHealth(client, cancellationToken),
                "stats" => await RunStats(client, cancellationToken),
                "index" => await RunIndex(client, arguments, cancellationToken),
                "search" => await RunSearch(client, arguments, cancellationToken),
                _ => WriteUsageError($"Unknown command '{arguments[0]}'.")
            };
        }
        catch (HttpRequestException exception)
        {
            await _error.WriteLineAsync($"API request failed: {exception.Message}");
            return 1;
        }
        catch (JsonException exception)
        {
            await _error.WriteLineAsync($"Invalid API response: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            await _error.WriteLineAsync(cancellationToken.IsCancellationRequested
                ? "Operation cancelled."
                : "API request timed out.");
            return 1;
        }
    }

    private async Task<int> RunHealth(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "/api/health",
            cancellationToken);
        using JsonDocument? json = await ReadSuccessfulJson(response, cancellationToken);

        if (json is null)
        {
            return 1;
        }

        string status = json.RootElement.GetProperty("status").GetString() ?? "unknown";
        bool ollama = json.RootElement.GetProperty("ollama").GetBoolean();
        await _output.WriteLineAsync($"System status: {status}");
        await _output.WriteLineAsync($"Ollama: {(ollama ? "available" : "unavailable")}");
        return 0;
    }

    private async Task<int> RunStats(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "/api/stats",
            cancellationToken);
        using JsonDocument? json = await ReadSuccessfulJson(response, cancellationToken);

        if (json is null)
        {
            return 1;
        }

        JsonElement root = json.RootElement;
        await _output.WriteLineAsync(
            $"Documents: {root.GetProperty("documents").GetInt32()}");
        await _output.WriteLineAsync(
            $"Index build time: {FormatNumber(root.GetProperty("index_build_time_ms"))} ms");
        await _output.WriteLineAsync(
            $"Threads used: {root.GetProperty("threads_used").GetInt32()}");
        return 0;
    }

    private async Task<int> RunIndex(
        HttpClient client,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 3
            || !string.Equals(arguments[1], "--threads", StringComparison.Ordinal)
            || !int.TryParse(
                arguments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int threads)
            || threads <= 0)
        {
            return WriteUsageError("Usage: index --threads <positive integer>.");
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/index",
            new { threads },
            cancellationToken);
        using JsonDocument? json = await ReadSuccessfulJson(response, cancellationToken);

        if (json is null)
        {
            return 1;
        }

        JsonElement root = json.RootElement;
        await _output.WriteLineAsync(
            $"Indexed documents: {root.GetProperty("documents_indexed").GetInt32()}");
        await _output.WriteLineAsync(
            $"Build time: {FormatNumber(root.GetProperty("time_ms"))} ms");
        return 0;
    }

    private async Task<int> RunSearch(
        HttpClient client,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string query = string.Join(' ', arguments.Skip(1)).Trim();

        if (query.Length == 0)
        {
            return WriteUsageError("Usage: search <query>.");
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/search",
            new
            {
                query,
                mode = "keyword"
            },
            cancellationToken);
        using JsonDocument? json = await ReadSuccessfulJson(response, cancellationToken);

        if (json is null)
        {
            return 1;
        }

        JsonElement root = json.RootElement;
        JsonElement results = root.GetProperty("results");

        if (results.GetArrayLength() == 0)
        {
            await _output.WriteLineAsync("No matching documents.");
        }
        else
        {
            foreach (JsonElement result in results.EnumerateArray())
            {
                await _output.WriteLineAsync(
                    $"{result.GetProperty("doc_id").GetString()} " +
                    $"(score: {FormatNumber(result.GetProperty("score"))})");
                await _output.WriteLineAsync(
                    $"  {result.GetProperty("snippet").GetString()}");
            }
        }

        await _output.WriteLineAsync(
            $"Search time: {FormatNumber(root.GetProperty("time_ms"))} ms");
        return 0;
    }

    private async Task<JsonDocument?> ReadSuccessfulJson(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string message = TryGetError(body) ?? response.ReasonPhrase ?? "Unknown error";
            await _error.WriteLineAsync(
                $"API error ({(int)response.StatusCode}): {message}");
            return null;
        }

        return JsonDocument.Parse(body);
    }

    private int WriteUsageError(string message)
    {
        _error.WriteLine(message);
        _error.WriteLine(
            "Usage: parallel-search [--api-url <url>] " +
            "<health|stats|index --threads N|search query>");
        return 2;
    }

    private static string? TryGetError(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("error", out JsonElement error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatNumber(JsonElement number)
    {
        return number.GetDouble().ToString("0.###", CultureInfo.InvariantCulture);
    }
}

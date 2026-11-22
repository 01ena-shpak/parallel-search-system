using System.Net.Http.Json;
using System.Text.Json;

namespace ParallelSearch.Api;

public sealed class OllamaQueryExpander
{
    private const string Model = "qwen2.5:0.5b";
    private const int MaximumAdditionalTerms = 5;

    private readonly HttpClient _httpClient;

    public OllamaQueryExpander(HttpClient httpClient)
    {
        _httpClient = httpClient
            ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<IReadOnlyList<string>> ExpandAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        string prompt =
            "Return up to 5 relevant English search terms for the following " +
            "query, separated by comma, without explanations. " +
            $"Query: {query}";

        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            "/api/generate",
            new
            {
                model = Model,
                prompt,
                stream = false
            },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using JsonDocument responseJson = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken);
        string generatedText = responseJson.RootElement
            .GetProperty("response")
            .GetString()
            ?? string.Empty;

        HashSet<string> originalTerms = GetQueryTerms(query);
        var additionalTerms = new List<string>(MaximumAdditionalTerms);
        var seenTerms = new HashSet<string>(StringComparer.Ordinal);
        string[] generatedTerms = generatedText.Split(',');

        for (int index = 0;
             index < generatedTerms.Length
             && additionalTerms.Count < MaximumAdditionalTerms;
             index++)
        {
            string term = generatedTerms[index].Trim().ToLowerInvariant();

            if (term.Length == 0
                || originalTerms.Contains(term)
                || !seenTerms.Add(term))
            {
                continue;
            }

            additionalTerms.Add(term);
        }

        return additionalTerms;
    }

    private static HashSet<string> GetQueryTerms(string query)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        int wordStart = -1;

        for (int index = 0; index <= query.Length; index++)
        {
            bool isWordCharacter = index < query.Length
                && char.IsLetterOrDigit(query[index]);

            if (isWordCharacter)
            {
                if (wordStart < 0)
                {
                    wordStart = index;
                }

                continue;
            }

            if (wordStart >= 0)
            {
                terms.Add(query[wordStart..index].ToLowerInvariant());
                wordStart = -1;
            }
        }

        return terms;
    }
}

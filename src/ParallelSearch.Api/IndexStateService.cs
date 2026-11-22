using System.Diagnostics;
using ParallelSearch.Core;

namespace ParallelSearch.Api;

public sealed record IndexSnapshot(
    InvertedIndex Index,
    int Documents,
    double IndexBuildTimeMs,
    int ThreadsUsed)
{
    public IReadOnlyDictionary<string, string> Snippets { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record SearchHit(
    string DocumentId,
    double Score,
    string Snippet);

public sealed record SearchExecutionResult(
    IReadOnlyList<SearchHit> Results,
    double TimeMs);

public sealed class IndexStateService
{
    private const string CorpusDirectoryConfigurationKey = "Corpus:DirectoryPath";

    private readonly ParallelIndexBuilder _indexBuilder;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private readonly object _snapshotLock = new();
    private IndexSnapshot _snapshot = new(
        new InvertedIndex(),
        Documents: 0,
        IndexBuildTimeMs: 0,
        ThreadsUsed: 0);

    public IndexStateService(
        ParallelIndexBuilder indexBuilder,
        IConfiguration configuration)
    {
        _indexBuilder = indexBuilder;
        _configuration = configuration;
    }

    public async Task<ParallelIndexBuildResult> RebuildAsync(
        int threads,
        CancellationToken cancellationToken)
    {
        await _rebuildLock.WaitAsync(cancellationToken);

        try
        {
            TextDocument[] documents = GetConfiguredDocuments();
            ParallelIndexBuildResult result = _indexBuilder.Build(documents, threads);
            var snapshot = new IndexSnapshot(
                result.Index,
                result.DocumentsIndexed,
                result.TimeMs,
                result.ThreadsUsed)
            {
                Snippets = result.Snippets.ToDictionary(
                    snippet => snippet.DocumentId,
                    snippet => snippet.Text,
                    StringComparer.Ordinal)
            };

            lock (_snapshotLock)
            {
                _snapshot = snapshot;
            }

            return result;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    public IndexSnapshot GetSnapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    public SearchExecutionResult Search(string query)
    {
        var stopwatch = Stopwatch.StartNew();
        string[] queryTerms = GetUniqueTerms(query);
        IndexSnapshot snapshot = GetSnapshot();
        var matchedTermCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int termIndex = 0; termIndex < queryTerms.Length; termIndex++)
        {
            IReadOnlyList<string> documentIds = snapshot.Index.Search(
                queryTerms[termIndex]);

            for (int documentIndex = 0;
                 documentIndex < documentIds.Count;
                 documentIndex++)
            {
                string documentId = documentIds[documentIndex];

                if (matchedTermCounts.TryGetValue(documentId, out int count))
                {
                    matchedTermCounts[documentId] = count + 1;
                }
                else
                {
                    matchedTermCounts[documentId] = 1;
                }
            }
        }

        SearchHit[] results = matchedTermCounts
            .Select(match => new SearchHit(
                match.Key,
                (double)match.Value / queryTerms.Length,
                snapshot.Snippets.TryGetValue(match.Key, out string? snippet)
                    ? snippet
                    : string.Empty))
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.DocumentId, StringComparer.Ordinal)
            .ToArray();

        stopwatch.Stop();
        return new SearchExecutionResult(
            results,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private TextDocument[] GetConfiguredDocuments()
    {
        string? configuredPath = _configuration[CorpusDirectoryConfigurationKey];

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                $"Configuration '{CorpusDirectoryConfigurationKey}' is missing.");
        }

        string corpusDirectoryPath = Path.GetFullPath(configuredPath);

        if (!Directory.Exists(corpusDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Corpus directory '{corpusDirectoryPath}' does not exist.");
        }

        return Directory
            .EnumerateFiles(
                corpusDirectoryPath,
                "*.txt",
                SearchOption.AllDirectories)
            .Select(filePath => new TextDocument(
                CreateDocumentId(corpusDirectoryPath, filePath),
                filePath))
            .OrderBy(document => document.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string CreateDocumentId(
        string corpusDirectoryPath,
        string filePath)
    {
        string relativePath = Path.GetRelativePath(corpusDirectoryPath, filePath);

        return relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string[] GetUniqueTerms(string query)
    {
        var terms = new List<string>();
        var uniqueTerms = new HashSet<string>(StringComparer.Ordinal);
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
                string term = query[wordStart..index].ToLowerInvariant();

                if (uniqueTerms.Add(term))
                {
                    terms.Add(term);
                }

                wordStart = -1;
            }
        }

        return terms.ToArray();
    }
}

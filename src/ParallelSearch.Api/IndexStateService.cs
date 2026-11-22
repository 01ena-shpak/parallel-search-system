using ParallelSearch.Core;

namespace ParallelSearch.Api;

public sealed record IndexSnapshot(
    InvertedIndex Index,
    int Documents,
    double IndexBuildTimeMs,
    int ThreadsUsed);

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
                result.ThreadsUsed);

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
}

using System.Diagnostics;
using System.Threading;

namespace ParallelSearch.Core;

public sealed record TextDocument(string Id, string FilePath);

public sealed record DocumentSnippet(string DocumentId, string Text);

public sealed record ParallelIndexBuildResult(
    InvertedIndex Index,
    int DocumentsIndexed,
    double TimeMs,
    int ThreadsUsed)
{
    public IReadOnlyList<DocumentSnippet> Snippets { get; init; } =
        Array.Empty<DocumentSnippet>();
}

public sealed class ParallelIndexBuilder
{
    public ParallelIndexBuildResult Build(
        IReadOnlyList<TextDocument> documents,
        int threads)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (threads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threads));
        }

        var index = new InvertedIndex();
        var snippets = new DocumentSnippet?[documents.Count];
        int documentsIndexed = 0;
        var stopwatch = Stopwatch.StartNew();

        using (var threadPool = new CustomThreadPool(threads))
        {
            for (int documentIndex = 0;
                 documentIndex < documents.Count;
                 documentIndex++)
            {
                TextDocument document = documents[documentIndex];
                int snippetIndex = documentIndex;

                threadPool.Enqueue(() =>
                {
                    string content = File.ReadAllText(document.FilePath);
                    index.AddDocument(document.Id, content);
                    snippets[snippetIndex] = new DocumentSnippet(
                        document.Id,
                        CreateSnippet(content));
                    Interlocked.Increment(ref documentsIndexed);
                });
            }

            threadPool.WaitForAll();
        }

        stopwatch.Stop();

        var indexedSnippets = new List<DocumentSnippet>(documentsIndexed);

        for (int snippetIndex = 0; snippetIndex < snippets.Length; snippetIndex++)
        {
            if (snippets[snippetIndex] is not null)
            {
                indexedSnippets.Add(snippets[snippetIndex]!);
            }
        }

        return new ParallelIndexBuildResult(
            index,
            documentsIndexed,
            stopwatch.Elapsed.TotalMilliseconds,
            threads)
        {
            Snippets = indexedSnippets
        };
    }

    private static string CreateSnippet(string content)
    {
        const int maximumLength = 200;

        return content.Length <= maximumLength
            ? content
            : content[..maximumLength];
    }
}

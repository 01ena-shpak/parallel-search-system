using System.Diagnostics;
using System.Threading;

namespace ParallelSearch.Core;

public sealed record TextDocument(string Id, string FilePath);

public sealed record ParallelIndexBuildResult(
    InvertedIndex Index,
    int DocumentsIndexed,
    double TimeMs,
    int ThreadsUsed);

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
        int documentsIndexed = 0;
        var stopwatch = Stopwatch.StartNew();

        using (var threadPool = new CustomThreadPool(threads))
        {
            for (int documentIndex = 0;
                 documentIndex < documents.Count;
                 documentIndex++)
            {
                TextDocument document = documents[documentIndex];

                threadPool.Enqueue(() =>
                {
                    string content = File.ReadAllText(document.FilePath);
                    index.AddDocument(document.Id, content);
                    Interlocked.Increment(ref documentsIndexed);
                });
            }

            threadPool.WaitForAll();
        }

        stopwatch.Stop();

        return new ParallelIndexBuildResult(
            index,
            documentsIndexed,
            stopwatch.Elapsed.TotalMilliseconds,
            threads);
    }
}

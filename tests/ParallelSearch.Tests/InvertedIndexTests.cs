using ParallelSearch.Core;

namespace ParallelSearch.Tests;

public sealed class InvertedIndexTests
{
    [Fact]
    public void AddDocument_MakesItsWordsSearchable()
    {
        var index = new InvertedIndex();

        index.AddDocument("doc-1", "parallel text search");

        IReadOnlyList<string> results = index.Search("parallel");

        Assert.Equal(["doc-1"], results);
    }

    [Fact]
    public void Search_WhenWordOccursInMultipleDocuments_ReturnsAllDocumentIds()
    {
        var index = new InvertedIndex();
        index.AddDocument("doc-1", "parallel algorithms");
        index.AddDocument("doc-2", "parallel text processing");

        IReadOnlyList<string> results = index.Search("parallel");

        Assert.Equal(2, results.Count);
        Assert.Contains("doc-1", results);
        Assert.Contains("doc-2", results);
    }

    [Fact]
    public void Search_WhenWordIsUnknown_ReturnsNoResults()
    {
        var index = new InvertedIndex();
        index.AddDocument("doc-1", "parallel text search");

        IReadOnlyList<string> results = index.Search("missing");

        Assert.Empty(results);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var index = new InvertedIndex();
        index.AddDocument("doc-1", "Parallel Search");

        IReadOnlyList<string> results = index.Search("PARALLEL");

        Assert.Equal(["doc-1"], results);
    }

    [Fact]
    public void Search_WhenWordOccursSeveralTimes_ReturnsDocumentIdOnce()
    {
        var index = new InvertedIndex();
        index.AddDocument("doc-1", "search search search");

        IReadOnlyList<string> results = index.Search("search");

        Assert.Equal(["doc-1"], results);
    }

    [Fact]
    public void Search_WhenWordsHaveHashCollision_ReturnsCorrectDocuments()
    {
        var index = new InvertedIndex();
        index.AddDocument("doc-1", "collision9");
        index.AddDocument("doc-2", "collision94");

        IReadOnlyList<string> firstResults = index.Search("collision9");
        IReadOnlyList<string> secondResults = index.Search("collision94");

        Assert.Equal(["doc-1"], firstResults);
        Assert.Equal(["doc-2"], secondResults);
    }

    [Fact]
    public void AddDocument_SplitsWordsAtPunctuationMarks()
    {
        var index = new InvertedIndex();

        index.AddDocument(
            "doc-1",
            "Parallel, search! Text-processing; index.");

        Assert.Equal(["doc-1"], index.Search("parallel"));
        Assert.Equal(["doc-1"], index.Search("search"));
        Assert.Equal(["doc-1"], index.Search("text"));
        Assert.Equal(["doc-1"], index.Search("processing"));
        Assert.Equal(["doc-1"], index.Search("index"));
    }

    [Fact]
    public void AddDocument_WhenSameDocumentIsAddedAgain_DoesNotDuplicateItsId()
    {
        var index = new InvertedIndex();

        index.AddDocument("doc-1", "parallel search");
        index.AddDocument("doc-1", "parallel indexing");

        Assert.Equal(["doc-1"], index.Search("parallel"));
        Assert.Equal(["doc-1"], index.Search("indexing"));
    }

    [Fact]
    public void AddDocument_WhenSameDocumentIsAddedManyTimes_KeepsOnePosting()
    {
        const int repetitions = 5000;
        var index = new InvertedIndex();

        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            index.AddDocument("repeated-doc", "common common");
        }

        Assert.Equal(["repeated-doc"], index.Search("common"));
    }

    [Fact]
    public void Search_WhenManyDocumentsContainOneWord_ReturnsEveryIdOnce()
    {
        const int documentCount = 10000;
        var index = new InvertedIndex();

        for (int documentIndex = 0;
             documentIndex < documentCount;
             documentIndex++)
        {
            index.AddDocument($"common-{documentIndex}", "common");
        }

        IReadOnlyList<string> results = index.Search("common");

        Assert.Equal(documentCount, results.Count);
        Assert.Equal(
            documentCount,
            results.Distinct(StringComparer.Ordinal).Count());

        for (int documentIndex = 0;
             documentIndex < documentCount;
             documentIndex++)
        {
            Assert.Equal($"common-{documentIndex}", results[documentIndex]);
        }
    }

    [Fact]
    public void AddDocument_WithManyDifferentWords_KeepsEveryWordSearchable()
    {
        const int wordCount = 5000;
        var index = new InvertedIndex();
        string content = string.Join(
            ' ',
            Enumerable.Range(0, wordCount).Select(number => $"word{number}"));

        index.AddDocument("large-doc", content);

        for (int number = 0; number < wordCount; number++)
        {
            Assert.Equal(["large-doc"], index.Search($"word{number}"));
        }
    }

    [Fact]
    public async Task AddDocument_FromMultipleThreads_KeepsEveryDocumentSearchable()
    {
        const int producerCount = 8;
        const int documentsPerProducer = 500;
        var index = new InvertedIndex();

        await RunConcurrently(producerCount, producerIndex =>
        {
            for (int documentIndex = 0;
                 documentIndex < documentsPerProducer;
                 documentIndex++)
            {
                string documentId = $"doc-{producerIndex}-{documentIndex}";
                string word = $"term{producerIndex}x{documentIndex}";
                index.AddDocument(documentId, word);
            }
        });

        for (int producerIndex = 0; producerIndex < producerCount; producerIndex++)
        {
            for (int documentIndex = 0;
                 documentIndex < documentsPerProducer;
                 documentIndex++)
            {
                string documentId = $"doc-{producerIndex}-{documentIndex}";
                string word = $"term{producerIndex}x{documentIndex}";
                Assert.Equal([documentId], index.Search(word));
            }
        }
    }

    [Fact]
    public async Task AddDocument_FromMultipleThreadsWithSameWord_KeepsUniqueDocumentIds()
    {
        const int producerCount = 8;
        const int documentsPerProducer = 250;
        var index = new InvertedIndex();
        index.AddDocument("seed-1", "shared");
        index.AddDocument("seed-2", "shared");

        await RunConcurrently(producerCount, producerIndex =>
        {
            for (int documentIndex = 0;
                 documentIndex < documentsPerProducer;
                 documentIndex++)
            {
                string documentId = $"shared-{producerIndex}-{documentIndex}";
                index.AddDocument(documentId, "shared shared");
            }
        });

        IReadOnlyList<string> results = index.Search("shared");
        int expectedCount = 2 + producerCount * documentsPerProducer;

        Assert.Equal(expectedCount, results.Count);
        Assert.Equal(
            results.Count,
            results.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("seed-1", results);
        Assert.Contains("seed-2", results);

        for (int producerIndex = 0; producerIndex < producerCount; producerIndex++)
        {
            for (int documentIndex = 0;
                 documentIndex < documentsPerProducer;
                 documentIndex++)
            {
                Assert.Contains(
                    $"shared-{producerIndex}-{documentIndex}",
                    results);
            }
        }
    }

    [Fact]
    public async Task AddDocument_FromMultipleThreadsWithSameDocumentId_KeepsOnePosting()
    {
        const int producerCount = 8;
        const int additionsPerProducer = 500;
        var index = new InvertedIndex();

        await RunConcurrently(producerCount, _ =>
        {
            for (int addition = 0;
                 addition < additionsPerProducer;
                 addition++)
            {
                index.AddDocument("shared-doc", "shared common");
            }
        });

        Assert.Equal(["shared-doc"], index.Search("shared"));
        Assert.Equal(["shared-doc"], index.Search("common"));
    }

    [Fact]
    public void Search_AfterMixedDocuments_PreservesExpectedResultSets()
    {
        var index = new InvertedIndex();
        index.AddDocument("doc-c", "Parallel, indexing and search.");
        index.AddDocument("doc-a", "SEARCH systems");
        index.AddDocument("doc-b", "parallel algorithms parallel");
        index.AddDocument("doc-a", "indexing");

        Assert.Equal(
            ["doc-b", "doc-c"],
            index.Search("parallel").Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["doc-a", "doc-c"],
            index.Search("search").Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["doc-a", "doc-c"],
            index.Search("indexing").Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(index.Search("missing"));
    }

    [Fact]
    public async Task Search_WhileDocumentsAreAdded_ReturnsValidSnapshotsAndFinalResults()
    {
        const int writerCount = 4;
        const int readerCount = 4;
        const int documentsPerWriter = 500;
        var index = new InvertedIndex();
        using var readersReady = new CountdownEvent(readerCount);
        using var writersCompleted = new CountdownEvent(writerCount);
        var observedExceptions = new List<Exception>();
        var exceptionsSync = new object();
        int completedSearches = 0;

        index.AddDocument("seed-1", "live");
        index.AddDocument("seed-2", "live");

        await RunConcurrently(writerCount + readerCount, workerIndex =>
        {
            if (workerIndex < writerCount)
            {
                try
                {
                    if (!readersReady.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Readers did not start in time.");
                    }

                    for (int documentIndex = 0;
                         documentIndex < documentsPerWriter;
                         documentIndex++)
                    {
                        index.AddDocument(
                            $"live-{workerIndex}-{documentIndex}",
                            "live");
                    }
                }
                catch (Exception exception)
                {
                    lock (exceptionsSync)
                    {
                        observedExceptions.Add(exception);
                    }
                }
                finally
                {
                    writersCompleted.Signal();
                }

                return;
            }

            try
            {
                ValidateSearchSnapshot(index.Search("live"));
                Interlocked.Increment(ref completedSearches);
            }
            catch (Exception exception)
            {
                lock (exceptionsSync)
                {
                    observedExceptions.Add(exception);
                }
            }
            finally
            {
                readersReady.Signal();
            }

            while (!writersCompleted.IsSet)
            {
                try
                {
                    ValidateSearchSnapshot(index.Search("live"));
                    Interlocked.Increment(ref completedSearches);
                }
                catch (Exception exception)
                {
                    lock (exceptionsSync)
                    {
                        observedExceptions.Add(exception);
                    }
                }
            }
        });

        Assert.Empty(observedExceptions);
        Assert.True(completedSearches >= readerCount);

        IReadOnlyList<string> finalResults = index.Search("live");
        int expectedCount = 2 + writerCount * documentsPerWriter;

        Assert.Equal(expectedCount, finalResults.Count);
        Assert.Equal(
            finalResults.Count,
            finalResults.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("seed-1", finalResults);
        Assert.Contains("seed-2", finalResults);

        for (int writerIndex = 0; writerIndex < writerCount; writerIndex++)
        {
            for (int documentIndex = 0;
                 documentIndex < documentsPerWriter;
                 documentIndex++)
            {
                Assert.Contains(
                    $"live-{writerIndex}-{documentIndex}",
                    finalResults);
            }
        }
    }

    private static async Task RunConcurrently(
        int workerCount,
        Action<int> work)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        using var workersReady = new CountdownEvent(workerCount);
        using var startWorkers = new ManualResetEventSlim(false);

        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(workerIndex => Task.Factory.StartNew(
                () =>
                {
                    workersReady.Signal();
                    startWorkers.Wait();
                    work(workerIndex);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        bool allWorkersReady = workersReady.Wait(timeout);
        startWorkers.Set();

        Assert.True(allWorkersReady, "Workers did not become ready in time.");
        await Task.WhenAll(workers).WaitAsync(timeout);
    }

    private static void ValidateSearchSnapshot(IReadOnlyList<string> results)
    {
        if (results.Count != results.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException(
                "A search snapshot contains duplicate document IDs.");
        }

        for (int index = 0; index < results.Count; index++)
        {
            string documentId = results[index];

            if (documentId != "seed-1"
                && documentId != "seed-2"
                && !documentId.StartsWith("live-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A search snapshot contains an invalid document ID.");
            }
        }
    }
}

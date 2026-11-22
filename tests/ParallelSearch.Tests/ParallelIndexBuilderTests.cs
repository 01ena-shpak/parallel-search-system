using ParallelSearch.Core;

namespace ParallelSearch.Tests;

public sealed class ParallelIndexBuilderTests
{
    [Fact]
    public void Build_WithSeveralTextFiles_CreatesSearchableIndex()
    {
        using var files = new TemporaryTextFiles();
        TextDocument[] documents =
        [
            files.Create("doc-1", "parallel text search"),
            files.Create("doc-2", "parallel algorithms"),
            files.Create("doc-3", "sequential processing")
        ];
        var builder = new ParallelIndexBuilder();

        ParallelIndexBuildResult result = builder.Build(documents, threads: 2);

        IReadOnlyList<string> parallelResults = result.Index.Search("parallel");
        Assert.Equal(2, parallelResults.Count);
        Assert.Contains("doc-1", parallelResults);
        Assert.Contains("doc-2", parallelResults);
        Assert.Equal(["doc-3"], result.Index.Search("sequential"));
    }

    [Fact]
    public void Build_WithOneAndFourThreads_ProducesEquivalentSearchResults()
    {
        using var files = new TemporaryTextFiles();
        TextDocument[] documents =
        [
            files.Create("doc-1", "parallel search index"),
            files.Create("doc-2", "parallel processing"),
            files.Create("doc-3", "search algorithms"),
            files.Create("doc-4", "index processing")
        ];
        var builder = new ParallelIndexBuilder();

        ParallelIndexBuildResult singleThread = builder.Build(documents, threads: 1);
        ParallelIndexBuildResult fourThreads = builder.Build(documents, threads: 4);

        string[] words = ["parallel", "search", "index", "processing", "missing"];

        foreach (string word in words)
        {
            string[] singleThreadIds = singleThread.Index.Search(word)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            string[] fourThreadIds = fourThreads.Index.Search(word)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(singleThreadIds, fourThreadIds);
        }
    }

    [Fact]
    public void Build_ReturnsIndexedDocumentCountAndBuildStatistics()
    {
        using var files = new TemporaryTextFiles();
        TextDocument[] documents =
        [
            files.Create("doc-1", "first document"),
            files.Create("doc-2", "second document"),
            files.Create("doc-3", "third document")
        ];
        var builder = new ParallelIndexBuilder();

        ParallelIndexBuildResult result = builder.Build(documents, threads: 2);

        Assert.Equal(3, result.DocumentsIndexed);
        Assert.Equal(2, result.ThreadsUsed);
        Assert.True(result.TimeMs >= 0);
    }

    [Fact]
    public void Build_WithNoDocuments_ReturnsEmptyIndex()
    {
        var builder = new ParallelIndexBuilder();

        ParallelIndexBuildResult result = builder.Build(
            Array.Empty<TextDocument>(),
            threads: 3);

        Assert.Equal(0, result.DocumentsIndexed);
        Assert.Equal(3, result.ThreadsUsed);
        Assert.True(result.TimeMs >= 0);
        Assert.Empty(result.Index.Search("anything"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_WithInvalidThreadCount_Throws(int threads)
    {
        var builder = new ParallelIndexBuilder();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.Build(Array.Empty<TextDocument>(), threads));

        Assert.Equal("threads", exception.ParamName);
    }

    [Fact]
    public async Task Build_WhenFileIsMissing_ReportsReadErrorWithoutHanging()
    {
        using var files = new TemporaryTextFiles();
        TextDocument[] documents =
        [
            new TextDocument("missing", files.GetPath("missing.txt"))
        ];
        var builder = new ParallelIndexBuilder();

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            async () => await Task.Run(() => builder.Build(documents, threads: 2))
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains(
            exception.InnerExceptions,
            innerException => innerException is FileNotFoundException);
    }

    [Fact]
    public void Build_WithManySmallFiles_IndexesSharedAndUniqueWords()
    {
        const int documentCount = 150;
        const int groupCount = 5;
        using var files = new TemporaryTextFiles();
        var documents = new TextDocument[documentCount];

        for (int index = 0; index < documentCount; index++)
        {
            documents[index] = files.Create(
                $"doc-{index}",
                $"shared shared group{index % groupCount} unique{index}");
        }

        var builder = new ParallelIndexBuilder();

        ParallelIndexBuildResult result = builder.Build(documents, threads: 8);

        Assert.Equal(documentCount, result.DocumentsIndexed);
        AssertContainsExactly(
            result.Index.Search("shared"),
            Enumerable.Range(0, documentCount).Select(index => $"doc-{index}"));

        for (int group = 0; group < groupCount; group++)
        {
            AssertContainsExactly(
                result.Index.Search($"group{group}"),
                Enumerable.Range(0, documentCount)
                    .Where(index => index % groupCount == group)
                    .Select(index => $"doc-{index}"));
        }

        for (int index = 0; index < documentCount; index++)
        {
            Assert.Equal([ $"doc-{index}" ], result.Index.Search($"unique{index}"));
        }
    }

    [Fact]
    public void Build_WithDifferentThreadCounts_ProducesTheSameResultSets()
    {
        const int documentCount = 120;
        const int categoryCount = 6;
        using var files = new TemporaryTextFiles();
        var documents = new TextDocument[documentCount];

        for (int index = 0; index < documentCount; index++)
        {
            documents[index] = files.Create(
                $"doc-{index}",
                $"common category{index % categoryCount} token{index}");
        }

        var builder = new ParallelIndexBuilder();
        int[] threadCounts = [1, 2, 4, 8];
        ParallelIndexBuildResult[] results = threadCounts
            .Select(threads => builder.Build(documents, threads))
            .ToArray();
        string[] queries =
        [
            "common",
            "category0",
            "category1",
            "category2",
            "category3",
            "category4",
            "category5",
            "token0",
            "token59",
            "token119",
            "missing"
        ];

        for (int resultIndex = 0; resultIndex < results.Length; resultIndex++)
        {
            Assert.Equal(documentCount, results[resultIndex].DocumentsIndexed);
            Assert.Equal(threadCounts[resultIndex], results[resultIndex].ThreadsUsed);
        }

        foreach (string query in queries)
        {
            string[] expectedIds = results[0].Index.Search(query)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            for (int resultIndex = 1; resultIndex < results.Length; resultIndex++)
            {
                string[] actualIds = results[resultIndex].Index.Search(query)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

                Assert.Equal(expectedIds, actualIds);
            }
        }
    }

    [Fact]
    public void Build_InParallel_DoesNotLoseOrDuplicateDocumentIds()
    {
        const int documentCount = 200;
        using var files = new TemporaryTextFiles();
        var documents = new TextDocument[documentCount];

        for (int index = 0; index < documentCount; index++)
        {
            documents[index] = files.Create(
                $"doc-{index}",
                $"repeated repeated repeated marker{index}");
        }

        var builder = new ParallelIndexBuilder();

        ParallelIndexBuildResult result = builder.Build(documents, threads: 8);

        Assert.Equal(documentCount, result.DocumentsIndexed);
        AssertContainsExactly(
            result.Index.Search("repeated"),
            Enumerable.Range(0, documentCount).Select(index => $"doc-{index}"));

        for (int index = 0; index < documentCount; index++)
        {
            Assert.Equal([ $"doc-{index}" ], result.Index.Search($"marker{index}"));
        }
    }

    [Fact]
    public async Task Build_WithSeveralMissingFiles_ReportsEveryReadErrorWithoutHanging()
    {
        using var files = new TemporaryTextFiles();
        var documents = new List<TextDocument>();

        for (int index = 0; index < 20; index++)
        {
            documents.Add(files.Create($"valid-{index}", $"valid content {index}"));
        }

        string[] missingPaths =
        [
            files.GetPath("missing-1.txt"),
            files.GetPath("missing-2.txt"),
            files.GetPath("missing-3.txt")
        ];

        documents.Insert(3, new TextDocument("missing-1", missingPaths[0]));
        documents.Insert(11, new TextDocument("missing-2", missingPaths[1]));
        documents.Add(new TextDocument("missing-3", missingPaths[2]));

        var builder = new ParallelIndexBuilder();

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            async () => await Task.Run(() => builder.Build(documents, threads: 4))
                .WaitAsync(TimeSpan.FromSeconds(10)));

        FileNotFoundException[] fileErrors = exception.InnerExceptions
            .OfType<FileNotFoundException>()
            .ToArray();

        Assert.Equal(missingPaths.Length, exception.InnerExceptions.Count);
        Assert.Equal(missingPaths.Length, fileErrors.Length);

        foreach (string missingPath in missingPaths)
        {
            Assert.Contains(
                fileErrors,
                fileError => fileError.FileName == missingPath);
        }
    }

    private static void AssertContainsExactly(
        IReadOnlyList<string> actualIds,
        IEnumerable<string> expectedIds)
    {
        string[] orderedActual = actualIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        string[] orderedExpected = expectedIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(orderedExpected, orderedActual);
    }

    private sealed class TemporaryTextFiles : IDisposable
    {
        private readonly string _directoryPath = Path.Combine(
            Path.GetTempPath(),
            "ParallelSearch.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryTextFiles()
        {
            Directory.CreateDirectory(_directoryPath);
        }

        public TextDocument Create(string id, string content)
        {
            string filePath = GetPath($"{id}.txt");
            File.WriteAllText(filePath, content);
            return new TextDocument(id, filePath);
        }

        public string GetPath(string fileName)
        {
            return Path.Combine(_directoryPath, fileName);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
            {
                Directory.Delete(_directoryPath, recursive: true);
            }
        }
    }
}

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
}

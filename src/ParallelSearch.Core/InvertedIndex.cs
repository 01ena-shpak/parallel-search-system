namespace ParallelSearch.Core;

public sealed class InvertedIndex
{
    private const int InitialBucketCount = 1024;
    private Entry?[] _buckets = new Entry[InitialBucketCount];
    private int _wordCount;

    public void AddDocument(string documentId, string content)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(content);

        var uniqueWords = new StringSet();
        int wordStart = -1;

        for (int index = 0; index <= content.Length; index++)
        {
            bool isWordCharacter = index < content.Length
                && char.IsLetterOrDigit(content[index]);

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
                string word = content[wordStart..index].ToLowerInvariant();

                if (uniqueWords.Add(word))
                {
                    AddWord(word, documentId);
                }

                wordStart = -1;
            }
        }
    }

    public IReadOnlyList<string> Search(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        string normalizedWord = word.Trim().ToLowerInvariant();
        if (normalizedWord.Length == 0)
        {
            return Array.Empty<string>();
        }

        int bucketIndex = CalculateBucketIndex(normalizedWord, _buckets.Length);
        Entry? current = _buckets[bucketIndex];

        while (current is not null)
        {
            if (current.Word == normalizedWord)
            {
                return current.DocumentIds.CopyValues();
            }

            current = current.Next;
        }

        return Array.Empty<string>();
    }

    private void AddWord(string word, string documentId)
    {
        int bucketIndex = CalculateBucketIndex(word, _buckets.Length);
        Entry? current = _buckets[bucketIndex];

        while (current is not null)
        {
            if (current.Word == word)
            {
                current.DocumentIds.Add(documentId);
                return;
            }

            current = current.Next;
        }

        EnsureWordCapacity();
        bucketIndex = CalculateBucketIndex(word, _buckets.Length);
        _buckets[bucketIndex] = new Entry(
            word,
            documentId,
            _buckets[bucketIndex]);
        _wordCount++;
    }

    private void EnsureWordCapacity()
    {
        if ((long)(_wordCount + 1) * 4 <= (long)_buckets.Length * 3)
        {
            return;
        }

        var resizedBuckets = new Entry?[_buckets.Length * 2];

        for (int bucketIndex = 0; bucketIndex < _buckets.Length; bucketIndex++)
        {
            Entry? current = _buckets[bucketIndex];

            while (current is not null)
            {
                Entry? next = current.Next;
                int resizedBucketIndex = CalculateBucketIndex(
                    current.Word,
                    resizedBuckets.Length);

                current.Next = resizedBuckets[resizedBucketIndex];
                resizedBuckets[resizedBucketIndex] = current;
                current = next;
            }
        }

        _buckets = resizedBuckets;
    }

    private static int CalculateBucketIndex(string value, int bucketCount)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        uint hash = offsetBasis;

        for (int index = 0; index < value.Length; index++)
        {
            hash = unchecked((hash ^ value[index]) * prime);
        }

        return (int)(hash % (uint)bucketCount);
    }

    private sealed class Entry
    {
        public Entry(string word, string documentId, Entry? next)
        {
            Word = word;
            DocumentIds = new DocumentIdSet(documentId);
            Next = next;
        }

        public string Word { get; }

        public DocumentIdSet DocumentIds { get; }

        public Entry? Next { get; set; }
    }

    private sealed class DocumentIdSet
    {
        private readonly string _firstDocumentId;
        private StringSet? _documentIds;

        public DocumentIdSet(string firstDocumentId)
        {
            _firstDocumentId = firstDocumentId;
        }

        public bool Add(string documentId)
        {
            if (_documentIds is null)
            {
                if (_firstDocumentId == documentId)
                {
                    return false;
                }

                _documentIds = new StringSet();
                _documentIds.Add(_firstDocumentId);
            }

            return _documentIds.Add(documentId);
        }

        public string[] CopyValues()
        {
            return _documentIds is null
                ? [_firstDocumentId]
                : _documentIds.CopyValues();
        }
    }

    private sealed class StringSet
    {
        private const int InitialBucketCount = 8;

        private StringNode?[] _buckets = new StringNode?[InitialBucketCount];
        private readonly List<string> _values = [];
        private int _count;

        public bool Add(string value)
        {
            int bucketIndex = CalculateBucketIndex(value, _buckets.Length);
            StringNode? current = _buckets[bucketIndex];

            while (current is not null)
            {
                if (current.Value == value)
                {
                    return false;
                }

                current = current.Next;
            }

            EnsureCapacity();
            bucketIndex = CalculateBucketIndex(value, _buckets.Length);
            _buckets[bucketIndex] = new StringNode(
                value,
                _buckets[bucketIndex]);
            _values.Add(value);
            _count++;

            return true;
        }

        public string[] CopyValues()
        {
            return _values.ToArray();
        }

        private void EnsureCapacity()
        {
            if ((long)(_count + 1) * 4 <= (long)_buckets.Length * 3)
            {
                return;
            }

            var resizedBuckets = new StringNode?[_buckets.Length * 2];

            for (int bucketIndex = 0; bucketIndex < _buckets.Length; bucketIndex++)
            {
                StringNode? current = _buckets[bucketIndex];

                while (current is not null)
                {
                    StringNode? next = current.Next;
                    int resizedBucketIndex = CalculateBucketIndex(
                        current.Value,
                        resizedBuckets.Length);

                    current.Next = resizedBuckets[resizedBucketIndex];
                    resizedBuckets[resizedBucketIndex] = current;
                    current = next;
                }
            }

            _buckets = resizedBuckets;
        }
    }

    private sealed class StringNode
    {
        public StringNode(string value, StringNode? next)
        {
            Value = value;
            Next = next;
        }

        public string Value { get; }

        public StringNode? Next { get; set; }
    }
}

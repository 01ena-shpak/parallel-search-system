namespace ParallelSearch.Core;

public sealed class InvertedIndex
{
    private const int SectionCount = 16;
    private const int InitialBucketsPerSection = 64;
    private readonly Section[] _sections;
    private readonly DocumentIdRegistry _documentIds = new();

    public InvertedIndex()
    {
        _sections = new Section[SectionCount];

        for (int index = 0; index < _sections.Length; index++)
        {
            _sections[index] = new Section();
        }
    }

    public void AddDocument(string documentId, string content)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(content);

        int numericDocumentId = _documentIds.GetOrAdd(documentId);
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
                    AddWord(word, numericDocumentId);
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

        uint hash = CalculateHash(normalizedWord);
        int sectionIndex = CalculateSectionIndex(hash);

        int[] numericDocumentIds =
            _sections[sectionIndex].Search(normalizedWord, hash);

        return _documentIds.Resolve(numericDocumentIds);
    }

    private void AddWord(string word, int documentId)
    {
        uint hash = CalculateHash(word);
        int sectionIndex = CalculateSectionIndex(hash);

        _sections[sectionIndex].Add(word, documentId, hash);
    }

    private static uint CalculateHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        uint hash = offsetBasis;

        for (int index = 0; index < value.Length; index++)
        {
            hash = unchecked((hash ^ value[index]) * prime);
        }

        return hash;
    }

    private static int CalculateSectionIndex(uint hash)
    {
        return (int)(hash % SectionCount);
    }

    private static int CalculateBucketIndex(uint hash, int bucketCount)
    {
        return (int)(hash % (uint)bucketCount);
    }

    private static int CalculateSectionBucketIndex(uint hash, int bucketCount)
    {
        uint sectionHash = hash / SectionCount;
        return CalculateBucketIndex(sectionHash, bucketCount);
    }

    private sealed class Section
    {
        private readonly object _sync = new();
        private Entry?[] _buckets = new Entry?[InitialBucketsPerSection];
        private int _wordCount;

        public void Add(string word, int documentId, uint hash)
        {
            lock (_sync)
            {
                int bucketIndex = CalculateSectionBucketIndex(
                    hash,
                    _buckets.Length);
                Entry? current = _buckets[bucketIndex];

                while (current is not null)
                {
                    if (current.Word == word)
                    {
                        current.AddDocumentId(documentId);
                        return;
                    }

                    current = current.Next;
                }

                EnsureCapacity();
                bucketIndex = CalculateSectionBucketIndex(hash, _buckets.Length);
                _buckets[bucketIndex] = new Entry(
                    word,
                    documentId,
                    _buckets[bucketIndex]);
                _wordCount++;
            }
        }

        public int[] Search(string word, uint hash)
        {
            lock (_sync)
            {
                int bucketIndex = CalculateSectionBucketIndex(
                    hash,
                    _buckets.Length);
                Entry? current = _buckets[bucketIndex];

                while (current is not null)
                {
                    if (current.Word == word)
                    {
                        return current.CopyDocumentIds();
                    }

                    current = current.Next;
                }

                return Array.Empty<int>();
            }
        }

        private void EnsureCapacity()
        {
            if ((long)(_wordCount + 1) * 4 <= (long)_buckets.Length * 3)
            {
                return;
            }

            var resizedBuckets = new Entry?[_buckets.Length * 2];

            for (int bucketIndex = 0;
                 bucketIndex < _buckets.Length;
                 bucketIndex++)
            {
                Entry? current = _buckets[bucketIndex];

                while (current is not null)
                {
                    Entry? next = current.Next;
                    uint hash = CalculateHash(current.Word);
                    int resizedBucketIndex = CalculateSectionBucketIndex(
                        hash,
                        resizedBuckets.Length);

                    current.Next = resizedBuckets[resizedBucketIndex];
                    resizedBuckets[resizedBucketIndex] = current;
                    current = next;
                }
            }

            _buckets = resizedBuckets;
        }
    }

    private sealed class Entry
    {
        private DocumentIdSet _documentIds;

        public Entry(string word, int documentId, Entry? next)
        {
            Word = word;
            _documentIds = new DocumentIdSet(documentId);
            Next = next;
        }

        public string Word { get; }

        public Entry? Next { get; set; }

        public void AddDocumentId(int documentId)
        {
            _documentIds.Add(documentId);
        }

        public int[] CopyDocumentIds()
        {
            return _documentIds.CopyValues();
        }
    }

    private struct DocumentIdSet
    {
        private const int LinearSearchLimit = 8;
        private const int InitialValueCapacity = 4;
        private const int InitialLookupCapacity = 16;

        private readonly int _firstDocumentId;
        private int[]? _values;
        private int[]? _lookupSlots;
        private int _count;

        public DocumentIdSet(int firstDocumentId)
        {
            _firstDocumentId = firstDocumentId;
            _values = null;
            _lookupSlots = null;
            _count = 1;
        }

        public bool Add(int documentId)
        {
            if (_lookupSlots is null)
            {
                if (ContainsInSmallList(documentId))
                {
                    return false;
                }

                Append(documentId);

                if (_count > LinearSearchLimit)
                {
                    BuildLookup();
                }

                return true;
            }

            if (ContainsInLookup(documentId, _lookupSlots))
            {
                return false;
            }

            EnsureLookupCapacity(_count + 1);
            Append(documentId);
            InsertIntoLookup(documentId, _lookupSlots!);

            return true;
        }

        public int[] CopyValues()
        {
            if (_count == 1)
            {
                return [_firstDocumentId];
            }

            var copy = new int[_count];
            Array.Copy(_values!, copy, _count);
            return copy;
        }

        private bool ContainsInSmallList(int documentId)
        {
            if (_firstDocumentId == documentId)
            {
                return true;
            }

            if (_values is null)
            {
                return false;
            }

            for (int index = 1; index < _count; index++)
            {
                if (_values[index] == documentId)
                {
                    return true;
                }
            }

            return false;
        }

        private void Append(int documentId)
        {
            if (_values is null)
            {
                _values = new int[InitialValueCapacity];
                _values[0] = _firstDocumentId;
            }
            else if (_count == _values.Length)
            {
                var resizedValues = new int[_values.Length * 2];
                Array.Copy(_values, resizedValues, _count);
                _values = resizedValues;
            }

            _values[_count] = documentId;
            _count++;
        }

        private void BuildLookup()
        {
            int lookupCapacity = InitialLookupCapacity;

            while ((long)_count * 4 > (long)lookupCapacity * 3)
            {
                lookupCapacity *= 2;
            }

            _lookupSlots = new int[lookupCapacity];

            for (int index = 0; index < _count; index++)
            {
                InsertIntoLookup(_values![index], _lookupSlots);
            }
        }

        private void EnsureLookupCapacity(int requiredCount)
        {
            if ((long)requiredCount * 4
                <= (long)_lookupSlots!.Length * 3)
            {
                return;
            }

            var resizedLookup = new int[_lookupSlots.Length * 2];

            for (int index = 0; index < _count; index++)
            {
                InsertIntoLookup(_values![index], resizedLookup);
            }

            _lookupSlots = resizedLookup;
        }

        private static bool ContainsInLookup(
            int documentId,
            int[] lookupSlots)
        {
            int storedValue = documentId + 1;
            int slotIndex = CalculateDocumentIdSlot(
                documentId,
                lookupSlots.Length);

            while (lookupSlots[slotIndex] != 0)
            {
                if (lookupSlots[slotIndex] == storedValue)
                {
                    return true;
                }

                slotIndex = (slotIndex + 1) & (lookupSlots.Length - 1);
            }

            return false;
        }

        private static void InsertIntoLookup(
            int documentId,
            int[] lookupSlots)
        {
            int slotIndex = CalculateDocumentIdSlot(
                documentId,
                lookupSlots.Length);

            while (lookupSlots[slotIndex] != 0)
            {
                slotIndex = (slotIndex + 1) & (lookupSlots.Length - 1);
            }

            lookupSlots[slotIndex] = documentId + 1;
        }

        private static int CalculateDocumentIdSlot(
            int documentId,
            int slotCount)
        {
            uint hash = (uint)documentId;
            hash ^= hash >> 16;
            hash *= 0x7feb352d;
            hash ^= hash >> 15;
            hash *= 0x846ca68b;
            hash ^= hash >> 16;

            return (int)(hash & (uint)(slotCount - 1));
        }
    }

    private sealed class StringSet
    {
        private const int InitialSlotCount = 8;

        private string?[] _slots = new string?[InitialSlotCount];
        private int _count;

        public bool Add(string value)
        {
            uint hash = CalculateHash(value);

            if (Contains(value, hash))
            {
                return false;
            }

            EnsureCapacity();
            Insert(value, hash, _slots);
            _count++;

            return true;
        }

        private bool Contains(string value, uint hash)
        {
            int slotIndex = CalculateBucketIndex(hash, _slots.Length);

            while (_slots[slotIndex] is not null)
            {
                if (string.Equals(
                    _slots[slotIndex],
                    value,
                    StringComparison.Ordinal))
                {
                    return true;
                }

                slotIndex = (slotIndex + 1) & (_slots.Length - 1);
            }

            return false;
        }

        private void EnsureCapacity()
        {
            if ((long)(_count + 1) * 4 <= (long)_slots.Length * 3)
            {
                return;
            }

            var resizedSlots = new string?[_slots.Length * 2];

            for (int index = 0; index < _slots.Length; index++)
            {
                string? value = _slots[index];

                if (value is not null)
                {
                    Insert(value, CalculateHash(value), resizedSlots);
                }
            }

            _slots = resizedSlots;
        }

        private static void Insert(
            string value,
            uint hash,
            string?[] slots)
        {
            int slotIndex = CalculateBucketIndex(hash, slots.Length);

            while (slots[slotIndex] is not null)
            {
                slotIndex = (slotIndex + 1) & (slots.Length - 1);
            }

            slots[slotIndex] = value;
        }
    }

    private sealed class DocumentIdRegistry
    {
        private const int InitialBucketCount = 64;

        private readonly object _sync = new();
        private DocumentIdNode?[] _buckets =
            new DocumentIdNode?[InitialBucketCount];
        private string?[] _valuesById = new string?[InitialBucketCount];
        private int _count;

        public int GetOrAdd(string value)
        {
            uint hash = CalculateHash(value);

            lock (_sync)
            {
                int bucketIndex = CalculateBucketIndex(hash, _buckets.Length);
                DocumentIdNode? current = _buckets[bucketIndex];

                while (current is not null)
                {
                    if (current.Hash == hash
                        && string.Equals(
                            current.Value,
                            value,
                            StringComparison.Ordinal))
                    {
                        return current.NumericId;
                    }

                    current = current.Next;
                }

                EnsureBucketCapacity();
                EnsureValueCapacity();

                int numericId = _count;
                bucketIndex = CalculateBucketIndex(hash, _buckets.Length);
                _buckets[bucketIndex] = new DocumentIdNode(
                    value,
                    numericId,
                    hash,
                    _buckets[bucketIndex]);
                _valuesById[numericId] = value;
                _count++;

                return numericId;
            }
        }

        public string[] Resolve(int[] numericIds)
        {
            if (numericIds.Length == 0)
            {
                return Array.Empty<string>();
            }

            lock (_sync)
            {
                var values = new string[numericIds.Length];

                for (int index = 0; index < numericIds.Length; index++)
                {
                    values[index] = _valuesById[numericIds[index]]!;
                }

                return values;
            }
        }

        private void EnsureBucketCapacity()
        {
            if ((long)(_count + 1) * 4 <= (long)_buckets.Length * 3)
            {
                return;
            }

            var resizedBuckets = new DocumentIdNode?[_buckets.Length * 2];

            for (int bucketIndex = 0;
                 bucketIndex < _buckets.Length;
                 bucketIndex++)
            {
                DocumentIdNode? current = _buckets[bucketIndex];

                while (current is not null)
                {
                    DocumentIdNode? next = current.Next;
                    int resizedBucketIndex = CalculateBucketIndex(
                        current.Hash,
                        resizedBuckets.Length);

                    current.Next = resizedBuckets[resizedBucketIndex];
                    resizedBuckets[resizedBucketIndex] = current;
                    current = next;
                }
            }

            _buckets = resizedBuckets;
        }

        private void EnsureValueCapacity()
        {
            if (_count < _valuesById.Length)
            {
                return;
            }

            var resizedValues = new string?[_valuesById.Length * 2];
            Array.Copy(_valuesById, resizedValues, _count);
            _valuesById = resizedValues;
        }
    }

    private sealed class DocumentIdNode
    {
        public DocumentIdNode(
            string value,
            int numericId,
            uint hash,
            DocumentIdNode? next)
        {
            Value = value;
            NumericId = numericId;
            Hash = hash;
            Next = next;
        }

        public string Value { get; }

        public int NumericId { get; }

        public uint Hash { get; }

        public DocumentIdNode? Next { get; set; }
    }
}

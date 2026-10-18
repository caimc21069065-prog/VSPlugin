using System.Collections.Generic;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Matching
{
    /// <summary>A ranked search hit while it is still inside the bounded top-K heap.</summary>
    internal struct SearchCandidate
    {
        internal IndexedItem Entry;
        internal int Score;
        internal MatchKind Kind;
    }

    /// <summary>
    /// Total ordering of search hits, from best to worst:
    /// score (higher first), then shorter simple name, then ordinal simple name, then identity
    /// (the identity tie-breaker only exists to make the result order fully deterministic).
    /// </summary>
    internal static class SearchCandidateRanking
    {
        /// <summary>Positive when <paramref name="a"/> ranks better than <paramref name="b"/>.</summary>
        internal static int Compare(in SearchCandidate a, in SearchCandidate b)
        {
            if (a.Score != b.Score)
            {
                return a.Score > b.Score ? 1 : -1;
            }

            int lengthA = a.Entry.NameLength;
            int lengthB = b.Entry.NameLength;
            if (lengthA != lengthB)
            {
                return lengthA < lengthB ? 1 : -1;
            }

            int ordinal = string.CompareOrdinal(a.Entry.Name, b.Entry.Name);
            if (ordinal != 0)
            {
                return ordinal < 0 ? 1 : -1;
            }

            ordinal = string.CompareOrdinal(a.Entry.Identity, b.Entry.Identity);
            if (ordinal != 0)
            {
                return ordinal < 0 ? 1 : -1;
            }

            return 0;
        }

        internal static readonly IComparer<SearchCandidate> Descending = new DescendingComparer();

        private sealed class DescendingComparer : IComparer<SearchCandidate>
        {
            public int Compare(SearchCandidate x, SearchCandidate y)
            {
                return SearchCandidateRanking.Compare(y, x);
            }
        }
    }

    /// <summary>
    /// Fixed-capacity min-heap of the best <c>capacity</c> candidates seen so far. Root is the worst kept
    /// candidate, so a full heap accepts a new candidate only when it ranks better than the root.
    /// This keeps search allocations and sorting cost proportional to MaxResults instead of to the number
    /// of matching items in the index.
    /// </summary>
    internal sealed class CandidateHeap
    {
        private readonly SearchCandidate[] _heap;
        private readonly int _capacity;
        private int _count;

        internal CandidateHeap(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _heap = new SearchCandidate[_capacity];
            _count = 0;
        }

        internal int Count
        {
            get { return _count; }
        }

        internal void Add(in SearchCandidate candidate)
        {
            if (_count < _capacity)
            {
                int index = _count;
                _count++;
                _heap[index] = candidate;
                SiftUp(index);
                return;
            }

            if (SearchCandidateRanking.Compare(candidate, _heap[0]) <= 0)
            {
                return; // not better than the worst kept hit
            }

            _heap[0] = candidate;
            SiftDown(0);
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (SearchCandidateRanking.Compare(_heap[index], _heap[parent]) <= 0)
                {
                    return;
                }

                SearchCandidate swap = _heap[index];
                _heap[index] = _heap[parent];
                _heap[parent] = swap;
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                int left = (2 * index) + 1;
                int right = left + 1;
                int worst = index;

                if (left < _count && SearchCandidateRanking.Compare(_heap[left], _heap[worst]) < 0)
                {
                    worst = left;
                }

                if (right < _count && SearchCandidateRanking.Compare(_heap[right], _heap[worst]) < 0)
                {
                    worst = right;
                }

                if (worst == index)
                {
                    return;
                }

                SearchCandidate swap = _heap[index];
                _heap[index] = _heap[worst];
                _heap[worst] = swap;
                index = worst;
            }
        }

        /// <summary>Copies the kept candidates into <paramref name="destination"/> (unordered).</summary>
        internal void CopyTo(SearchCandidate[] destination)
        {
            for (int i = 0; i < _count; i++)
            {
                destination[i] = _heap[i];
            }
        }

        internal void CopyToAndSortDescending(ref SearchCandidate[] destination)
        {
            destination = new SearchCandidate[_count];
            CopyTo(destination);
            System.Array.Sort(destination, SearchCandidateRanking.Descending);
        }
    }

    /// <summary>
    /// Total ordering of items for the empty-query ("browse") state: recently used items are added
    /// separately, then public/exported symbols (public first, then kind, then name) and finally files.
    /// </summary>
    internal static class BrowseRanking
    {
        /// <summary>Positive when <paramref name="a"/> ranks better than <paramref name="b"/>.</summary>
        internal static int CompareSymbols(IndexedItem a, IndexedItem b)
        {
            if (a.IsPublicSymbol != b.IsPublicSymbol)
            {
                return a.IsPublicSymbol ? 1 : -1;
            }

            if (a.Kind != b.Kind)
            {
                return (int)a.Kind < (int)b.Kind ? 1 : -1;
            }

            return CompareNames(a, b);
        }

        internal static int CompareFiles(IndexedItem a, IndexedItem b)
        {
            return CompareNames(a, b);
        }

        private static int CompareNames(IndexedItem a, IndexedItem b)
        {
            int ordinal = string.CompareOrdinal(a.Name, b.Name);
            if (ordinal != 0)
            {
                return ordinal < 0 ? 1 : -1;
            }

            ordinal = string.CompareOrdinal(a.Identity, b.Identity);
            if (ordinal != 0)
            {
                return ordinal < 0 ? 1 : -1;
            }

            return 0;
        }
    }

    /// <summary>
    /// Fixed-capacity selection of the best browse items. Used by the empty-query path so that it never has
    /// to sort the whole index (which may contain hundreds of thousands of items).
    /// </summary>
    internal sealed class BrowseTopK
    {
        private readonly IndexedItem[] _heap;
        private readonly int _capacity;
        private readonly System.Comparison<IndexedItem> _comparison;
        private int _count;

        internal BrowseTopK(int capacity, System.Comparison<IndexedItem> comparison)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _heap = new IndexedItem[_capacity];
            _comparison = comparison;
            _count = 0;
        }

        internal int Count
        {
            get { return _count; }
        }

        internal void Add(IndexedItem item)
        {
            if (_count < _capacity)
            {
                int index = _count;
                _count++;
                _heap[index] = item;
                SiftUp(index);
                return;
            }

            if (_comparison(item, _heap[0]) <= 0)
            {
                return;
            }

            _heap[0] = item;
            SiftDown(0);
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (_comparison(_heap[index], _heap[parent]) <= 0)
                {
                    return;
                }

                IndexedItem swap = _heap[index];
                _heap[index] = _heap[parent];
                _heap[parent] = swap;
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                int left = (2 * index) + 1;
                int right = left + 1;
                int worst = index;

                if (left < _count && _comparison(_heap[left], _heap[worst]) < 0)
                {
                    worst = left;
                }

                if (right < _count && _comparison(_heap[right], _heap[worst]) < 0)
                {
                    worst = right;
                }

                if (worst == index)
                {
                    return;
                }

                IndexedItem swap = _heap[index];
                _heap[index] = _heap[worst];
                _heap[worst] = swap;
                index = worst;
            }
        }

        /// <summary>Copies the kept items in best-first order.</summary>
        internal void CopySortedDescending(List<IndexedItem> destination)
        {
            if (_count == 0)
            {
                return;
            }

            var copy = new IndexedItem[_count];
            System.Array.Copy(_heap, copy, _count);
            var comparer = new AdapterComparer(_comparison);
            System.Array.Sort(copy, comparer);
            for (int i = 0; i < copy.Length; i++)
            {
                destination.Add(copy[i]);
            }
        }

        private sealed class AdapterComparer : IComparer<IndexedItem>
        {
            private readonly System.Comparison<IndexedItem> _comparison;

            internal AdapterComparer(System.Comparison<IndexedItem> comparison)
            {
                _comparison = comparison;
            }

            public int Compare(IndexedItem? x, IndexedItem? y)
            {
                if (x == null || y == null)
                {
                    return x == null ? (y == null ? 0 : 1) : -1;
                }

                return -_comparison(x, y);
            }
        }
    }
}

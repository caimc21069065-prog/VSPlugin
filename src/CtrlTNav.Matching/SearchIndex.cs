using System;
using System.Collections.Generic;
using System.IO;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;

namespace CtrlTNav.Matching
{
    /// <summary>
    /// In-memory search index over <see cref="NavigationItem"/> instances, implementing the ranking pipeline
    /// described in <c>docs/ARCHITECTURE.md</c> section 4.
    /// </summary>
    /// <remarks>
    /// <para>Ranking, best first:</para>
    /// <list type="number">
    /// <item><description>match class bonus (Exact &gt; Prefix &gt; Camel &gt; Subsequence, plus the
    /// qualified-name-only <see cref="MatchKind.FullName"/> class) from <see cref="SearchOptions"/>;</description></item>
    /// <item><description>within-class quality (0..299) - shorter prefixes, more word-boundary hits and more
    /// contiguous runs score higher;</description></item>
    /// <item><description>recent-usage boost, scaled from the store's 0..1000 strength by
    /// <see cref="SearchOptions.RecentUsageBoostMax"/>;</description></item>
    /// <item><description>current-project bonus when the item's project path equals
    /// <see cref="SearchOptions.CurrentProjectPath"/>;</description></item>
    /// <item><description>public/exported symbol bonus.</description></item>
    /// </list>
    /// <para>Ties are broken by shorter simple name, then ordinal name, then item identity, which makes the
    /// result order fully deterministic.</para>
    /// <para>Thread safety: mutations must come from a single writer thread (the indexer); any number of
    /// threads may call <see cref="Search"/> concurrently. Readers use an immutable snapshot of the item
    /// array, which is rebuilt lazily after mutations, so a search never observes a half-applied update and
    /// never holds a lock while scanning.</para>
    /// </remarks>
    public sealed class SearchIndex : ISearchIndex
    {
        private static readonly IndexedItem[] EmptyItems = new IndexedItem[0];
        private static readonly SearchResult[] NoResults = new SearchResult[0];

        private static readonly IReadOnlyList<SearchResult> NoResultsList = NoResults;

        private static readonly StringComparer FilePathComparer =
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private readonly ILog _log;
        private readonly object _gate = new object();
        private readonly List<IndexedItem> _items = new List<IndexedItem>();
        private readonly Dictionary<string, IndexedItem> _byIdentity = new Dictionary<string, IndexedItem>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<IndexedItem>> _byFile = new Dictionary<string, List<IndexedItem>>(FilePathComparer);

        private volatile IndexedItem[] _snapshot = EmptyItems;
        private volatile bool _snapshotDirty;
        private volatile IRecentUsageStore? _recentUsage;

        /// <summary>Creates an empty index. <paramref name="log"/> may be null; diagnostics are then discarded.</summary>
        public SearchIndex(ILog? log = null)
        {
            _log = log ?? NullLog.Instance;
        }

        /// <summary>
        /// Optional recent-usage store consulted when <see cref="SearchOptions.BoostRecentlyUsed"/> is set.
        /// Usually a <see cref="RecentUsageTracker"/>; other <see cref="IRecentUsageStore"/> implementations
        /// also work (they are just queried through the interface, item by item).
        /// </summary>
        public IRecentUsageStore? RecentUsage
        {
            get { return _recentUsage; }
            set { _recentUsage = value; }
        }

        /// <inheritdoc />
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _items.Count;
                }
            }
        }

        // -------------------------------------------------------------------------------------------
        // Mutation (single writer thread).
        // -------------------------------------------------------------------------------------------

        /// <inheritdoc />
        public void AddOrUpdate(NavigationItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            lock (_gate)
            {
                AddOrUpdateLocked(item);
                _snapshotDirty = true;
            }
        }

        /// <inheritdoc />
        public void AddRange(IEnumerable<NavigationItem> items)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            lock (_gate)
            {
                bool changed = false;
                foreach (NavigationItem item in items)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    AddOrUpdateLocked(item);
                    changed = true;
                }

                if (changed)
                {
                    _snapshotDirty = true;
                }
            }
        }

        /// <inheritdoc />
        public void ReplaceFile(string filePath, IEnumerable<NavigationItem> items)
        {
            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            lock (_gate)
            {
                RemoveFileLocked(filePath);
                foreach (NavigationItem item in items)
                {
                    if (item != null)
                    {
                        AddOrUpdateLocked(item);
                    }
                }

                _snapshotDirty = true;
            }
        }

        /// <inheritdoc />
        public void RemoveFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            lock (_gate)
            {
                if (RemoveFileLocked(filePath) > 0)
                {
                    _snapshotDirty = true;
                }
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            lock (_gate)
            {
                _items.Clear();
                _byIdentity.Clear();
                _byFile.Clear();
                _snapshotDirty = true;
            }
        }

        private void AddOrUpdateLocked(NavigationItem item)
        {
            var entry = new IndexedItem(item);
            if (_byIdentity.TryGetValue(entry.Identity, out IndexedItem? existing))
            {
                int slot = existing.ListIndex;
                entry.ListIndex = slot;
                _items[slot] = entry;
                _byIdentity[entry.Identity] = entry;
                RemoveFromFileMap(existing);
                AddToFileMap(entry);
                return;
            }

            entry.ListIndex = _items.Count;
            _items.Add(entry);
            _byIdentity.Add(entry.Identity, entry);
            AddToFileMap(entry);
        }

        private int RemoveFileLocked(string filePath)
        {
            if (!_byFile.TryGetValue(filePath, out List<IndexedItem>? entries) || entries == null || entries.Count == 0)
            {
                return 0;
            }

            // Copy first: RemoveEntryLocked mutates both the index list and the per-file list.
            var removed = new IndexedItem[entries.Count];
            entries.CopyTo(removed);
            for (int i = 0; i < removed.Length; i++)
            {
                RemoveEntryLocked(removed[i]);
            }

            return removed.Length;
        }

        private void RemoveEntryLocked(IndexedItem entry)
        {
            int index = entry.ListIndex;
            int lastIndex = _items.Count - 1;
            if (index < 0 || index > lastIndex || !ReferenceEquals(_items[index], entry))
            {
                return; // stale bookkeeping - can only happen if an item was added twice with the same identity
            }

            if (index != lastIndex)
            {
                IndexedItem moved = _items[lastIndex];
                _items[index] = moved;
                moved.ListIndex = index;
            }

            _items.RemoveAt(lastIndex);
            _byIdentity.Remove(entry.Identity);
            RemoveFromFileMap(entry);
        }

        private void AddToFileMap(IndexedItem entry)
        {
            if (entry.FilePath.Length == 0)
            {
                return;
            }

            if (!_byFile.TryGetValue(entry.FilePath, out List<IndexedItem>? entries) || entries == null)
            {
                entries = new List<IndexedItem>(4);
                _byFile[entry.FilePath] = entries;
            }

            entries.Add(entry);
        }

        private void RemoveFromFileMap(IndexedItem entry)
        {
            if (entry.FilePath.Length == 0)
            {
                return;
            }

            if (!_byFile.TryGetValue(entry.FilePath, out List<IndexedItem>? entries) || entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (ReferenceEquals(entries[i], entry))
                {
                    entries[i] = entries[entries.Count - 1];
                    entries.RemoveAt(entries.Count - 1);
                    break;
                }
            }

            if (entries.Count == 0)
            {
                _byFile.Remove(entry.FilePath);
            }
        }

        // -------------------------------------------------------------------------------------------
        // Search.
        // -------------------------------------------------------------------------------------------

        /// <inheritdoc />
        public IReadOnlyList<SearchResult> Search(SearchQuery query, SearchOptions options)
        {
            try
            {
                SearchQuery effectiveQuery = query ?? SearchQuery.Empty;
                SearchOptions effectiveOptions = (options ?? SearchOptions.CreateDefault()).Normalized();
                IndexedItem[] snapshot = GetSnapshot();
                if (snapshot.Length == 0)
                {
                    return NoResultsList;
                }

                IRecentUsageStore? usage = _recentUsage;
                ItemKindFilter filter = ResolveFilter(effectiveQuery, effectiveOptions);
                ulong kindMask = BuildKindMask(filter, effectiveOptions);

                if (effectiveQuery.Text.Length == 0)
                {
                    return SearchEmpty(effectiveQuery, effectiveOptions, kindMask, snapshot, usage);
                }

                return SearchText(effectiveQuery, effectiveOptions, kindMask, snapshot, usage);
            }
            catch (Exception exception)
            {
                _log.Error("SearchIndex.Search failed; returning no results.", exception);
                return NoResultsList;
            }
        }

        private static ItemKindFilter ResolveFilter(SearchQuery query, SearchOptions options)
        {
            if (query.HasFilterPrefix && query.Filter != ItemKindFilter.All)
            {
                return query.Filter;
            }

            return options.DefaultFilter;
        }

        /// <summary>
        /// Builds a bit mask of the <see cref="ItemKind"/> values allowed by the query filter and the
        /// <see cref="SearchOptions.IncludeFiles"/> / <see cref="SearchOptions.IncludeSymbols"/> switches.
        /// </summary>
        private static ulong BuildKindMask(ItemKindFilter filter, SearchOptions options)
        {
            ulong mask = 0;
            for (int value = (int)ItemKind.File; value <= (int)ItemKind.Template; value++)
            {
                var kind = (ItemKind)value;
                bool allowed = kind.MatchesFilter(filter);
                if (allowed && kind == ItemKind.File && !options.IncludeFiles)
                {
                    allowed = false;
                }

                if (allowed && kind != ItemKind.File && !options.IncludeSymbols)
                {
                    allowed = false;
                }

                if (allowed)
                {
                    mask |= 1UL << value;
                }
            }

            return mask;
        }

        private static bool IsKindAllowed(ItemKind kind, ulong kindMask)
        {
            return ((1UL << (int)kind) & kindMask) != 0;
        }

        private IReadOnlyList<SearchResult> SearchText(
            SearchQuery query,
            SearchOptions options,
            ulong kindMask,
            IndexedItem[] items,
            IRecentUsageStore? usage)
        {
            string lowerQuery = query.Text.ToLowerInvariant();
            int queryLength = lowerQuery.Length;
            ulong queryMask = FuzzyMatcher.ComputeQueryMask(lowerQuery, out bool maskUsable);

            var recentTracker = usage as RecentUsageTracker;
            bool boostRecent = options.BoostRecentlyUsed && usage != null;
            bool boostProject = options.BoostCurrentProject
                && options.CurrentProjectPath != null
                && options.CurrentProjectPath!.Length > 0;
            string currentProjectPath = options.CurrentProjectPath ?? string.Empty;
            int currentProjectBonus = options.CurrentProjectBonus;
            bool boostPublic = options.BoostPublicSymbols;
            int publicSymbolBonus = options.PublicSymbolBonus;
            int recentUsageBoostMax = options.RecentUsageBoostMax;

            var heap = new CandidateHeap(options.MaxResults);

            for (int i = 0; i < items.Length; i++)
            {
                IndexedItem entry = items[i];
                if ((1UL << (int)entry.Kind & kindMask) == 0)
                {
                    continue;
                }

                MatchKind matchKind = MatchKind.None;
                int quality = 0;
                bool matched = false;

                if (queryLength <= entry.NameLength)
                {
                    if (!maskUsable || ((entry.NameMask & queryMask) == queryMask))
                    {
                        matched = FuzzyMatcher.TryMatchPrepared(
                            entry.Name,
                            entry.LowerName,
                            entry.BoundaryBits,
                            lowerQuery,
                            out matchKind,
                            out quality);
                    }
                }

                if (!matched && entry.SecondaryText != null && queryLength <= entry.SecondaryLength)
                {
                    if (!maskUsable || ((entry.SecondaryMask & queryMask) == queryMask))
                    {
                        if (FuzzyMatcher.TryMatchSecondary(entry.SecondaryText, lowerQuery, out int secondaryQuality))
                        {
                            matchKind = MatchKind.FullName;
                            quality = secondaryQuality;
                            matched = true;
                        }
                    }
                }

                if (!matched)
                {
                    continue;
                }

                int score = GetClassBonus(matchKind, options) + quality;

                if (boostProject
                    && entry.ProjectPath.Length > 0
                    && string.Equals(entry.ProjectPath, currentProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    score += currentProjectBonus;
                }

                if (boostPublic && entry.IsPublicSymbol)
                {
                    score += publicSymbolBonus;
                }

                if (boostRecent)
                {
                    int strength = recentTracker != null
                        ? recentTracker.GetStrength(entry.Identity)
                        : usage!.GetBoost(entry.Item);
                    if (strength > 0 && recentUsageBoostMax > 0)
                    {
                        if (strength > 1000)
                        {
                            strength = 1000;
                        }

                        score += (int)(((long)strength * recentUsageBoostMax) / 1000L);
                    }
                }

                heap.Add(new SearchCandidate { Entry = entry, Score = score, Kind = matchKind });
            }

            int count = heap.Count;
            if (count == 0)
            {
                return NoResultsList;
            }

            var candidates = new SearchCandidate[count];
            heap.CopyTo(candidates);
            Array.Sort(candidates, SearchCandidateRanking.Descending);

            var results = new SearchResult[count];
            int[]? scratch = null;
            for (int i = 0; i < count; i++)
            {
                SearchCandidate candidate = candidates[i];
                IReadOnlyList<TextRange> ranges;
                switch (candidate.Kind)
                {
                    case MatchKind.Exact:
                    case MatchKind.Prefix:
                        int length = candidate.Entry.NameLength < queryLength ? candidate.Entry.NameLength : queryLength;
                        ranges = length <= 0
                            ? FuzzyMatcher.NoRangesList
                            : new[] { new TextRange(0, length) };
                        break;

                    case MatchKind.Camel:
                    case MatchKind.Subsequence:
                        if (scratch == null)
                        {
                            scratch = new int[queryLength];
                        }

                        ranges = FuzzyMatcher.GetHighlightRangesPrepared(
                            candidate.Entry.Name,
                            candidate.Entry.LowerName,
                            candidate.Entry.BoundaryBits,
                            lowerQuery,
                            candidate.Kind,
                            scratch);
                        break;

                    default:
                        ranges = FuzzyMatcher.NoRangesList;
                        break;
                }

                results[i] = new SearchResult(candidate.Entry.Item, candidate.Score, candidate.Kind, ranges);
            }

            return results;
        }

        private static int GetClassBonus(MatchKind kind, SearchOptions options)
        {
            switch (kind)
            {
                case MatchKind.Exact:
                    return options.ExactMatchBonus;
                case MatchKind.Prefix:
                    return options.PrefixMatchBonus;
                case MatchKind.Camel:
                    return options.CamelMatchBonus;
                case MatchKind.Subsequence:
                    return options.SubsequenceMatchBonus;
                case MatchKind.FullName:
                    return options.FullNameMatchBonus;
                default:
                    return 0;
            }
        }

        // -------------------------------------------------------------------------------------------
        // Empty query ("browse") path.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Empty query: recently used items first (newest first), then public symbols, then files.
        /// When the query carries an explicit filter prefix (for example <c>t </c>) all symbols that match
        /// the filter are listed instead of only public ones, which makes the prefix a useful "browse" mode.
        /// </summary>
        private IReadOnlyList<SearchResult> SearchEmpty(
            SearchQuery query,
            SearchOptions options,
            ulong kindMask,
            IndexedItem[] items,
            IRecentUsageStore? usage)
        {
            int max = options.MaxResults;
            var results = new List<SearchResult>(max < 32 ? 32 : max > 512 ? 512 : max);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool browseAll = !query.HasFilterPrefix;

            if (usage != null && !usage.IsEmpty)
            {
                AddRecentResults(usage, kindMask, max, results, seen);
            }

            if (results.Count < max)
            {
                int remaining = max - results.Count;
                var symbolHeap = new BrowseTopK(remaining, BrowseRanking.CompareSymbols);
                for (int i = 0; i < items.Length; i++)
                {
                    IndexedItem entry = items[i];
                    if (entry.IsFile || !IsKindAllowed(entry.Kind, kindMask))
                    {
                        continue;
                    }

                    if (browseAll && !entry.IsPublicSymbol)
                    {
                        continue;
                    }

                    if (seen.Contains(entry.Identity))
                    {
                        continue;
                    }

                    symbolHeap.Add(entry);
                }

                var symbols = new List<IndexedItem>(symbolHeap.Count);
                symbolHeap.CopySortedDescending(symbols);
                for (int i = 0; i < symbols.Count && results.Count < max; i++)
                {
                    if (seen.Add(symbols[i].Identity))
                    {
                        results.Add(CreateBrowseResult(symbols[i]));
                    }
                }
            }

            if (results.Count < max)
            {
                int remaining = max - results.Count;
                var fileHeap = new BrowseTopK(remaining, BrowseRanking.CompareFiles);
                for (int i = 0; i < items.Length; i++)
                {
                    IndexedItem entry = items[i];
                    if (!entry.IsFile || !IsKindAllowed(entry.Kind, kindMask))
                    {
                        continue;
                    }

                    if (seen.Contains(entry.Identity))
                    {
                        continue;
                    }

                    fileHeap.Add(entry);
                }

                var files = new List<IndexedItem>(fileHeap.Count);
                fileHeap.CopySortedDescending(files);
                for (int i = 0; i < files.Count && results.Count < max; i++)
                {
                    if (seen.Add(files[i].Identity))
                    {
                        results.Add(CreateBrowseResult(files[i]));
                    }
                }
            }

            return results;
        }

        private void AddRecentResults(
            IRecentUsageStore usage,
            ulong kindMask,
            int max,
            List<SearchResult> results,
            HashSet<string> seen)
        {
            if (usage is RecentUsageTracker tracker)
            {
                string[] identities = tracker.GetRecentIdentities();
                lock (_gate)
                {
                    for (int i = 0; i < identities.Length && results.Count < max; i++)
                    {
                        if (_byIdentity.TryGetValue(identities[i], out IndexedItem? entry)
                            && IsKindAllowed(entry.Kind, kindMask)
                            && seen.Add(entry.Identity))
                        {
                            results.Add(CreateBrowseResult(entry));
                        }
                    }
                }

                return;
            }

            IReadOnlyList<string> paths = usage.GetRecentFilePaths(max < 1 ? 1 : max);
            if (paths == null || paths.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                for (int i = 0; i < paths.Count && results.Count < max; i++)
                {
                    if (paths[i] == null || !_byFile.TryGetValue(paths[i], out List<IndexedItem>? entries) || entries == null)
                    {
                        continue;
                    }

                    for (int e = 0; e < entries.Count && results.Count < max; e++)
                    {
                        IndexedItem entry = entries[e];
                        if (entry.IsFile && IsKindAllowed(entry.Kind, kindMask) && seen.Add(entry.Identity))
                        {
                            results.Add(CreateBrowseResult(entry));
                        }
                    }
                }
            }
        }

        private static SearchResult CreateBrowseResult(IndexedItem entry)
        {
            return new SearchResult(entry.Item, 0, MatchKind.None, FuzzyMatcher.NoRangesList);
        }

        // -------------------------------------------------------------------------------------------
        // Snapshot management.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns an immutable array of the current items. Rebuilt lazily after mutations: a live-typing
        /// user performs no mutation, so searches after warm-up just read the published array reference.
        /// </summary>
        private IndexedItem[] GetSnapshot()
        {
            IndexedItem[] snapshot = _snapshot;
            if (!_snapshotDirty)
            {
                return snapshot;
            }

            lock (_gate)
            {
                if (_snapshotDirty)
                {
                    _snapshot = _items.Count == 0 ? EmptyItems : _items.ToArray();
                    _snapshotDirty = false;
                }

                return _snapshot;
            }
        }
    }
}

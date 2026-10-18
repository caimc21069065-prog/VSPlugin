using System;
using System.Collections.Generic;
using System.Diagnostics;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using CtrlTNav.Core.Services;

namespace CtrlTNav.UiDemo
{
    /// <summary>
    /// In-memory <see cref="ISearchService"/> used by the demo. It mimics the real ranking pipeline closely
    /// enough for the UI (fuzzy match kinds, highlight ranges, MRU boost, status text) but never touches disk.
    /// </summary>
    internal sealed class FakeSearchService : ISearchService
    {
        private const int SimulatedIndexedSymbols = 12438;
        private const int SimulatedIndexedFiles = 96;

        private readonly List<NavigationItem> _items;
        private readonly List<string> _recentIdentities = new List<string>();
        private readonly SearchOptions _searchOptions = new SearchOptions { MaxResults = 200 };

        public FakeSearchService(IReadOnlyList<NavigationItem> items)
        {
            _items = new List<NavigationItem>(items);

            // Seed the "recently used" list so the screenshot shows MRU weighting at work.
            _recentIdentities.Add("Class|" + System.IO.Path.Combine(SampleData.BaseDirectory, @"Engine\Render\Vulkan", "VulkanDevice.h") + "|18|0|Engine::Render::Vulkan::VulkanDevice");
            _recentIdentities.Add("Function|" + System.IO.Path.Combine(SampleData.BaseDirectory, @"Engine\Render\Vulkan", "VulkanDeviceFactory.cpp") + "|46|0|Engine::Render::Vulkan::CreateDevice");
        }

        public bool IsIndexReady
        {
            get { return true; }
        }

        public int IndexedItemCount
        {
            get { return SimulatedIndexedSymbols; }
        }

        public IndexState IndexState
        {
            get { return IndexState.Ready; }
        }

        public IndexProgress Progress
        {
            get { return new IndexProgress(SimulatedIndexedFiles, SimulatedIndexedFiles, SimulatedIndexedSymbols, null); }
        }

        public string StatusText
        {
            get { return "12,438 symbols \u00B7 96 files \u00B7 Ready"; }
        }

        public SearchOptions CurrentOptions
        {
            get { return _searchOptions; }
        }

        public event EventHandler? IndexStateChanged
        {
            add { }
            remove { }
        }

        public SearchResponse Search(string? rawText)
        {
            var stopwatch = Stopwatch.StartNew();
            SearchQuery query = SearchQuery.Parse(rawText);
            string text = query.Text;

            var hits = new List<SearchResult>();
            for (int i = 0; i < _items.Count; i++)
            {
                NavigationItem item = _items[i];
                if (query.HasFilterPrefix && !item.Kind.MatchesFilter(query.Filter))
                {
                    continue;
                }

                MatchKind matchKind;
                List<TextRange> ranges;
                int score;
                bool matched;

                if (text.Length == 0)
                {
                    matchKind = MatchKind.None;
                    ranges = new List<TextRange>(0);
                    score = GetEmptyQueryScore(item);
                    matched = score > 0;
                }
                else
                {
                    matched = DemoMatcher.TryMatch(item.Name, text, out matchKind, out ranges, out score);
                    if (!matched && item.FullName.Length > 0)
                    {
                        List<TextRange> ignored;
                        int fullScore;
                        MatchKind ignoredKind;
                        if (DemoMatcher.TryMatch(item.FullName, text, out ignoredKind, out ignored, out fullScore))
                        {
                            matchKind = MatchKind.FullName;
                            ranges = new List<TextRange>(0);
                            score = 140;
                            matched = true;
                        }
                    }
                }

                if (!matched)
                {
                    continue;
                }

                score += GetRecentBoost(item);
                score += item.IsPublicSymbol ? 40 : 0;
                hits.Add(new SearchResult(item, score, matchKind, ranges));
            }

            hits.Sort(CompareResults);
            if (hits.Count > _searchOptions.MaxResults)
            {
                hits.RemoveRange(_searchOptions.MaxResults, hits.Count - _searchOptions.MaxResults);
            }

            stopwatch.Stop();
            return new SearchResponse(rawText ?? string.Empty, query.Filter, hits, SimulatedIndexedSymbols, stopwatch.Elapsed.TotalMilliseconds);
        }

        public void RecordUsage(NavigationItem item)
        {
            string identity = item.GetIdentity();
            _recentIdentities.Remove(identity);
            _recentIdentities.Insert(0, identity);
            if (_recentIdentities.Count > 50)
            {
                _recentIdentities.RemoveAt(_recentIdentities.Count - 1);
            }
        }

        private static int CompareResults(SearchResult a, SearchResult b)
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            int byLength = a.Item.Name.Length.CompareTo(b.Item.Name.Length);
            if (byLength != 0)
            {
                return byLength;
            }

            return string.CompareOrdinal(a.Item.Name, b.Item.Name);
        }

        private int GetEmptyQueryScore(NavigationItem item)
        {
            int score = 0;
            if (item.IsFile)
            {
                score += 100;
            }
            else if (item.IsPublicSymbol)
            {
                score += 200;
            }
            else
            {
                score += 50;
            }

            return score + GetRecentBoost(item);
        }

        private int GetRecentBoost(NavigationItem item)
        {
            string identity = item.GetIdentity();
            for (int i = 0; i < _recentIdentities.Count; i++)
            {
                if (string.Equals(_recentIdentities[i], identity, StringComparison.Ordinal))
                {
                    return Math.Max(0, 520 - (i * 120));
                }
            }

            return 0;
        }
    }

    /// <summary>Recording <see cref="INavigationService"/> for the demo; never actually opens a file.</summary>
    internal sealed class FakeNavigationService : INavigationService
    {
        public NavigationItem? LastNavigatedItem { get; private set; }

        public int NavigationCount { get; private set; }

        public bool Navigate(NavigationItem item, out string? errorMessage)
        {
            errorMessage = null;
            LastNavigatedItem = item;
            NavigationCount++;
            return true;
        }
    }

    /// <summary>
    /// Simplified fuzzy matcher: exact / prefix / substring / camel / subsequence, which is enough to render
    /// realistic ordering and highlight ranges in the demo.
    /// </summary>
    internal static class DemoMatcher
    {
        public static bool TryMatch(string name, string query, out MatchKind kind, out List<TextRange> ranges, out int score)
        {
            kind = MatchKind.None;
            ranges = new List<TextRange>(0);
            score = 0;

            if (query.Length == 0)
            {
                score = 100;
                return true;
            }

            if (name.Length == 0)
            {
                return false;
            }

            if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
            {
                kind = MatchKind.Exact;
                ranges.Add(new TextRange(0, name.Length));
                score = 1200;
                return true;
            }

            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                kind = MatchKind.Prefix;
                ranges.Add(new TextRange(0, query.Length));
                score = 640;
                return true;
            }

            int substring = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (substring > 0)
            {
                kind = MatchKind.Camel;
                ranges.Add(new TextRange(substring, query.Length));
                score = 430;
                return true;
            }

            if (TryCamel(name, query, ranges))
            {
                kind = MatchKind.Camel;
                score = 300;
                return true;
            }

            if (TrySubsequence(name, query, ranges))
            {
                kind = MatchKind.Subsequence;
                score = 160 - Math.Min(80, name.Length - query.Length);
                return true;
            }

            ranges = new List<TextRange>(0);
            return false;
        }

        private static bool TryCamel(string name, string query, List<TextRange> ranges)
        {
            int queryIndex = 0;
            var matched = new List<TextRange>(query.Length);
            for (int i = 0; i < name.Length && queryIndex < query.Length; i++)
            {
                char c = name[i];
                bool wordStart = i == 0
                    || name[i - 1] == '_'
                    || char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                if (!wordStart)
                {
                    continue;
                }

                if (char.ToUpperInvariant(c) == char.ToUpperInvariant(query[queryIndex]))
                {
                    matched.Add(new TextRange(i, 1));
                    queryIndex++;
                }
            }

            if (queryIndex != query.Length)
            {
                return false;
            }

            ranges.Clear();
            ranges.AddRange(matched);
            return true;
        }

        private static bool TrySubsequence(string name, string query, List<TextRange> ranges)
        {
            int queryIndex = 0;
            var matched = new List<TextRange>(query.Length);
            for (int i = 0; i < name.Length && queryIndex < query.Length; i++)
            {
                if (char.ToUpperInvariant(name[i]) == char.ToUpperInvariant(query[queryIndex]))
                {
                    matched.Add(new TextRange(i, 1));
                    queryIndex++;
                }
            }

            if (queryIndex != query.Length)
            {
                return false;
            }

            ranges.Clear();
            ranges.AddRange(matched);
            return true;
        }
    }
}

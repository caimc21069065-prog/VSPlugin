using System;
using System.Collections.Generic;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Core.Search
{
    /// <summary>Result of a single search invocation.</summary>
    public sealed class SearchResponse
    {
        private static readonly SearchResult[] NoResults = new SearchResult[0];

        public SearchResponse(
            string rawText,
            ItemKindFilter filter,
            IReadOnlyList<SearchResult> results,
            int totalIndexed,
            double elapsedMs)
        {
            RawText = rawText ?? string.Empty;
            Filter = filter;
            Results = results ?? NoResults;
            TotalIndexed = totalIndexed;
            ElapsedMs = elapsedMs;
        }

        public string RawText { get; }

        public ItemKindFilter Filter { get; }

        public IReadOnlyList<SearchResult> Results { get; }

        /// <summary>Number of items currently in the index (all kinds).</summary>
        public int TotalIndexed { get; }

        public double ElapsedMs { get; }

        public bool IsEmpty
        {
            get { return Results.Count == 0; }
        }

        public static SearchResponse CreateEmpty(ItemKindFilter filter, int totalIndexed, double elapsedMs)
        {
            return new SearchResponse(string.Empty, filter, NoResults, totalIndexed, elapsedMs);
        }
    }
}

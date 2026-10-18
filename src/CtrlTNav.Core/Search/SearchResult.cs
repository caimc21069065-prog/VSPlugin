using System.Collections.Generic;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Core.Search
{
    /// <summary>A ranked search hit.</summary>
    public sealed class SearchResult
    {
        public SearchResult(NavigationItem item, int score, MatchKind matchKind, IReadOnlyList<TextRange> matchedRanges)
        {
            Item = item;
            Score = score;
            MatchKind = matchKind;
            MatchedRanges = matchedRanges ?? EmptyRanges;
        }

        private static readonly TextRange[] EmptyRanges = new TextRange[0];

        public NavigationItem Item { get; }

        public int Score { get; }

        public MatchKind MatchKind { get; }

        /// <summary>
        /// Highlight ranges relative to <see cref="NavigationItem.Name"/>. Empty when the match was found on
        /// the qualified name only or when the query is empty.
        /// </summary>
        public IReadOnlyList<TextRange> MatchedRanges { get; }

        public override string ToString()
        {
            return Score + " " + MatchKind + " " + Item;
        }
    }
}

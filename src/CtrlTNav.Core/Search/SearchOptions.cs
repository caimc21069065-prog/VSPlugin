using CtrlTNav.Core.Model;

namespace CtrlTNav.Core.Search
{
    /// <summary>
    /// Tunable inputs of the search pipeline. The UI layer creates one snapshot per keystroke from the
    /// current plugin settings; the search engine treats the instance as immutable while searching.
    /// </summary>
    public sealed class SearchOptions
    {
        /// <summary>Maximum number of results returned. Values below 1 are clamped to 1.</summary>
        public int MaxResults { get; set; } = 100;

        /// <summary>Filter used when the query itself does not contain a prefix.</summary>
        public ItemKindFilter DefaultFilter { get; set; } = ItemKindFilter.All;

        /// <summary>When false, file results are not returned by the all/&lt;prefix&gt; queries.</summary>
        public bool IncludeFiles { get; set; } = true;

        /// <summary>When false, symbol results are not returned by the all/&lt;prefix&gt; queries.</summary>
        public bool IncludeSymbols { get; set; } = true;

        public bool BoostRecentlyUsed { get; set; } = true;

        public bool BoostCurrentProject { get; set; } = true;

        public bool BoostPublicSymbols { get; set; } = true;

        /// <summary>Absolute path of the project that owns the active document, used for the current-project boost.</summary>
        public string? CurrentProjectPath { get; set; }

        // ----- Score weights (points; higher is better) -----

        public int ExactMatchBonus { get; set; } = 1200;

        public int PrefixMatchBonus { get; set; } = 600;

        public int CamelMatchBonus { get; set; } = 300;

        public int SubsequenceMatchBonus { get; set; } = 0;

        public int FullNameMatchBonus { get; set; } = 120;

        /// <summary>Maximum points added for a very recently used item.</summary>
        public int RecentUsageBoostMax { get; set; } = 500;

        /// <summary>Points added to items that belong to the current project.</summary>
        public int CurrentProjectBonus { get; set; } = 150;

        /// <summary>Points added to public/exported symbols.</summary>
        public int PublicSymbolBonus { get; set; } = 40;

        public SearchOptions Clone()
        {
            return (SearchOptions)MemberwiseClone();
        }

        public SearchOptions Normalized()
        {
            SearchOptions clone = Clone();
            if (clone.MaxResults < 1)
            {
                clone.MaxResults = 1;
            }

            if (clone.MaxResults > 5000)
            {
                clone.MaxResults = 5000;
            }

            return clone;
        }

        public static SearchOptions CreateDefault()
        {
            return new SearchOptions();
        }
    }
}

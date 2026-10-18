using System;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;

namespace CtrlTNav.Core.Services
{
    /// <summary>
    /// Front door used by the search window: performs queries, records MRU usage and exposes index status
    /// for the footer. Implemented by <c>CtrlTNav.Matching.NavigationSearchService</c>.
    /// </summary>
    public interface ISearchService
    {
        bool IsIndexReady { get; }

        int IndexedItemCount { get; }

        IndexState IndexState { get; }

        IndexProgress Progress { get; }

        /// <summary>Human readable status line shown in the window footer (never null).</summary>
        string StatusText { get; }

        /// <summary>Snapshot of the options used for the next query.</summary>
        SearchOptions CurrentOptions { get; }

        /// <summary>Raised when index state or item count changed. May be raised on a background thread.</summary>
        event EventHandler? IndexStateChanged;

        /// <summary>Searches the index. Never throws for malformed input; returns an empty response instead.</summary>
        SearchResponse Search(string? rawText);

        /// <summary>Records that the user navigated to an item (feeds the recent-usage boost).</summary>
        void RecordUsage(NavigationItem item);
    }
}

using System.Collections.Generic;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Core.Search
{
    /// <summary>
    /// In-memory search index over <see cref="NavigationItem"/> instances. Implemented by the
    /// <c>CtrlTNav.Matching</c> assembly; the interface lives in Core so that the VS integration and the
    /// UI can be developed (and compiled) independently of the matching engine.
    /// </summary>
    /// <remarks>
    /// Implementations must be thread-safe for concurrent readers as long as mutation happens on a single
    /// writer thread (the indexer's background thread). Searches must not allocate more than a few hundred
    /// kilobytes per call in the common case.
    /// </remarks>
    public interface ISearchIndex
    {
        /// <summary>Number of indexed items.</summary>
        int Count { get; }

        /// <summary>Adds or updates a single item (identity: file + line + column + kind + name).</summary>
        void AddOrUpdate(NavigationItem item);

        void AddRange(IEnumerable<NavigationItem> items);

        /// <summary>Replaces every item that originates from <paramref name="filePath"/>.</summary>
        void ReplaceFile(string filePath, IEnumerable<NavigationItem> items);

        void RemoveFile(string filePath);

        void Clear();

        /// <summary>
        /// Executes a query and returns the best <paramref name="options"/>.MaxResults hits.
        /// </summary>
        IReadOnlyList<SearchResult> Search(SearchQuery query, SearchOptions options);
    }
}

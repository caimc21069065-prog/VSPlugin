using System.Collections.Generic;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Core.Search
{
    /// <summary>Tracks recently used navigation items and turns them into ranking boosts.</summary>
    public interface IRecentUsageStore
    {
        /// <summary>Records that the user navigated to <paramref name="item"/>.</summary>
        void RecordUsage(NavigationItem item);

        /// <summary>
        /// Returns a relative usage strength in the range 0..1000 for an item, based on how recently and
        /// how often it was used. The search engine scales this value by <c>SearchOptions.RecentUsageBoostMax</c>.
        /// </summary>
        int GetBoost(NavigationItem item);

        /// <summary>Most recently opened file paths, newest first.</summary>
        IReadOnlyList<string> GetRecentFilePaths(int maxCount);

        /// <summary>True when the store contains no usage data.</summary>
        bool IsEmpty { get; }

        void Clear();
    }

    /// <summary>Persistence for <see cref="IRecentUsageStore"/>.</summary>
    public interface IRecentUsagePersistence
    {
        void Load();

        void Save();
    }
}

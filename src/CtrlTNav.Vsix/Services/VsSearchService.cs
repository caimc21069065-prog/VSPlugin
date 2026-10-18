using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Options;
using CtrlTNav.Core.Search;
using CtrlTNav.Core.Services;
using CtrlTNav.Matching;
using CtrlTNav.Vsix.Options;
using IndexStateEnum = CtrlTNav.Core.Indexing.IndexState;
using Timer = System.Threading.Timer;

namespace CtrlTNav.Vsix.Services
{
    /// <summary>
    /// <see cref="ISearchService"/> implementation for Visual Studio. Owns the in-memory
    /// <see cref="SearchIndex"/> and <see cref="RecentUsageTracker"/>, keeps the search index in sync with
    /// the background <see cref="ICppIndex"/> and exposes the current index state to the popup footer.
    /// </summary>
    /// <remarks>
    /// Search is called from the UI's background search task, so the class does not touch the DTE on the
    /// search path; the current project path is pushed in by the UI thread when the popup is about to be
    /// shown. Index updates are applied under a private lock with a single writer so the search index
    /// can be read concurrently.
    /// </remarks>
    public sealed class VsSearchService : ISearchService, IDisposable
    {
        private const int RecentUsageSaveDebounceMs = 2000;
        private const int StateNotificationThrottleMs = 120;

        private readonly ICppIndex _cppIndex;
        private readonly OptionsProvider _optionsProvider;
        private readonly ILog _log;
        private readonly object _indexGate = new object();

        private SearchIndex _searchIndex;
        private RecentUsageTracker _recentUsage;
        private volatile string? _currentProjectPath;
        private Timer? _recentUsageSaveTimer;
        private long _lastStateNotificationTimestamp;
        private int _disposed;

        internal VsSearchService(
            SearchIndex searchIndex,
            RecentUsageTracker recentUsage,
            ICppIndex cppIndex,
            OptionsProvider optionsProvider,
            ILog log)
        {
            _searchIndex = searchIndex ?? throw new ArgumentNullException(nameof(searchIndex));
            _recentUsage = recentUsage ?? throw new ArgumentNullException(nameof(recentUsage));
            _cppIndex = cppIndex ?? throw new ArgumentNullException(nameof(cppIndex));
            _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            _searchIndex.RecentUsage = _recentUsage;

            try
            {
                _cppIndex.ItemsChanged += OnIndexItemsChanged;
                _cppIndex.ProgressChanged += OnIndexProgressChanged;
            }
            catch (Exception ex)
            {
                _log.Error("Could not subscribe to index events; search results will not update.", ex);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // ISearchService
        // ---------------------------------------------------------------------------------------------

        public event EventHandler? IndexStateChanged;

        public bool IsIndexReady
        {
            get
            {
                try
                {
                    return _cppIndex.State == IndexStateEnum.Ready;
                }
                catch
                {
                    return false;
                }
            }
        }

        public int IndexedItemCount
        {
            get
            {
                lock (_indexGate)
                {
                    return _searchIndex.Count;
                }
            }
        }

        public IndexStateEnum IndexState
        {
            get
            {
                try
                {
                    return _cppIndex.State;
                }
                catch
                {
                    return IndexStateEnum.NotStarted;
                }
            }
        }

        public IndexProgress Progress
        {
            get
            {
                try
                {
                    return _cppIndex.Progress;
                }
                catch
                {
                    return IndexProgress.Empty;
                }
            }
        }

        public string StatusText
        {
            get
            {
                try
                {
                    CtrlTNavOptions options = _optionsProvider.Current;
                    IndexStateEnum state = IndexState;

                    if (!options.ShowIndexStatus)
                    {
                        return GetShortStatus(state);
                    }

                    switch (state)
                    {
                        case IndexStateEnum.Building:
                        {
                            IndexProgress progress = Progress;
                            if (progress.FilesTotal <= 0)
                            {
                                return "Indexing...";
                            }

                            return string.Format(
                                "Indexing {0}/{1} files ({2} symbols)",
                                progress.FilesProcessed,
                                progress.FilesTotal,
                                IndexedItemCount);
                        }

                        case IndexStateEnum.Ready:
                        {
                            int items = IndexedItemCount;
                            int files = SafeFileCount();
                            if (files > 0)
                            {
                                return string.Format("{0} symbols from {1} files", items, files);
                            }

                            return string.Format("{0} symbols indexed", items);
                        }

                        case IndexStateEnum.Failed:
                            return "Indexing failed - see " + CtrlTNavConstants.ProductName + " log";

                        default:
                            return "Not indexed yet";
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not build the index status text: " + ex.Message);
                    return "Ready";
                }
            }
        }

        public SearchOptions CurrentOptions
        {
            get { return BuildSearchOptions(); }
        }

        public SearchResponse Search(string? rawText)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            SearchQuery query;
            try
            {
                query = SearchQuery.Parse(rawText);
            }
            catch
            {
                query = SearchQuery.Empty;
            }

            try
            {
                SearchOptions options = BuildSearchOptions();
                SearchIndex index;
                lock (_indexGate)
                {
                    index = _searchIndex;
                }

                IReadOnlyList<SearchResult> results = index.Search(query, options);
                stopwatch.Stop();
                return new SearchResponse(
                    rawText ?? string.Empty,
                    query.Filter,
                    results,
                    index.Count,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _log.Error("Search failed for query '" + rawText + "'.", ex);
                return new SearchResponse(rawText ?? string.Empty, query.Filter, Array.Empty<SearchResult>(), 0, stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        public void RecordUsage(NavigationItem item)
        {
            if (item == null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                if (!_optionsProvider.Current.EnableRecentUsage)
                {
                    return;
                }

                lock (_indexGate)
                {
                    _recentUsage.RecordUsage(item);
                }

                ScheduleRecentUsageSave();
            }
            catch (Exception ex)
            {
                _log.Warn("Recording recent usage failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Wiring
        // ---------------------------------------------------------------------------------------------

        /// <summary>Drops every indexed item (used when the solution closes).</summary>
        public void Reset()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                var replacement = new SearchIndex(_log)
                {
                    RecentUsage = GetRecentUsage(),
                };

                lock (_indexGate)
                {
                    replacement.RecentUsage = _recentUsage;
                    _searchIndex = replacement;
                }

                NotifyIndexStateChanged(force: true);
            }
            catch (Exception ex)
            {
                _log.Warn("Resetting the search index failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Updates the cached project path of the active document. Called on the UI thread right before the
        /// popup is shown so that <see cref="Search"/> never has to talk to the DTE.
        /// </summary>
        public void UpdateCurrentProjectPath(string? projectPath)
        {
            _currentProjectPath = string.IsNullOrWhiteSpace(projectPath) ? null : projectPath;
        }

        /// <summary>Applies changed options (recent usage capacity / enable flag).</summary>
        public void UpdateOptions(CtrlTNavOptions options)
        {
            if (options == null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                CtrlTNavOptions normalized = options.Normalize();
                int capacity = normalized.RecentUsageCapacity;
                string storagePath;
                bool capacityChanged;
                lock (_indexGate)
                {
                    capacityChanged = _recentUsage.Capacity != capacity;
                    storagePath = _recentUsage.StorageFilePath;

                    // Do not lose entries recorded since the last debounced save when the tracker is
                    // replaced (the replacement reloads the file from disk).
                    if (capacityChanged)
                    {
                        _recentUsage.Save();
                    }
                }

                if (!capacityChanged)
                {
                    return;
                }

                var replacement = new RecentUsageTracker(storagePath, capacity, _log);
                replacement.Load();
                lock (_indexGate)
                {
                    _recentUsage = replacement;
                    _searchIndex.RecentUsage = replacement;
                }

                _log.Info("Recent-usage tracker reconfigured with capacity " + capacity + ".");
            }
            catch (Exception ex)
            {
                _log.Warn("Could not reconfigure the recent-usage tracker: " + ex.Message);
            }
        }

        private SearchOptions BuildSearchOptions()
        {
            CtrlTNavOptions options;
            try
            {
                options = _optionsProvider.Current;
            }
            catch
            {
                options = CtrlTNavOptions.CreateDefault();
            }

            SearchOptions searchOptions = options.ToSearchOptions(_currentProjectPath);
            searchOptions.DefaultFilter = ItemKindFilter.All;
            return searchOptions.Normalized();
        }

        private void OnIndexItemsChanged(object? sender, IndexItemsChangedEventArgs e)
        {
            if (e == null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                if (e.IsFullRebuild)
                {
                    IReadOnlyList<NavigationItem> items = e.Items ?? Array.Empty<NavigationItem>();

                    // Build the replacement index first and swap it in atomically: readers (the popup
                    // search task) never observe a half-filled index during a rebuild.
                    var replacement = new SearchIndex(_log);
                    replacement.RecentUsage = GetRecentUsage();
                    replacement.AddRange(items);
                    lock (_indexGate)
                    {
                        // Pick up a tracker that was replaced by UpdateOptions while this index was
                        // being built.
                        replacement.RecentUsage = _recentUsage;
                        _searchIndex = replacement;
                    }

                    _log.Info("Search index rebuilt with " + items.Count + " items.");
                }
                else if (!string.IsNullOrEmpty(e.FilePath))
                {
                    IReadOnlyList<NavigationItem> items = e.Items ?? Array.Empty<NavigationItem>();
                    lock (_indexGate)
                    {
                        _searchIndex.ReplaceFile(e.FilePath!, items);
                    }
                }
                else
                {
                    _log.Info("Received an index change without a file path and without a rebuild flag; ignoring.");
                }

                // Item changes are infrequent (one event per completed rebuild or file update) and the
                // final "ready" event must never be dropped by the throttle: the popup refreshes its
                // results only when it observes that the index became ready.
                NotifyIndexStateChanged(force: true);
            }
            catch (Exception ex)
            {
                _log.Error("Applying index changes to the search index failed.", ex);
            }
        }

        private IRecentUsageStore GetRecentUsage()
        {
            lock (_indexGate)
            {
                return _recentUsage;
            }
        }

        private void OnIndexProgressChanged(object? sender, IndexProgressEventArgs e)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            bool force = false;
            try
            {
                force = _cppIndex.State != IndexStateEnum.Building;
            }
            catch
            {
                // Treat an unreadable state as advisory; the throttle still applies.
            }

            NotifyIndexStateChanged(force);
        }

        private void NotifyIndexStateChanged(bool force)
        {
            EventHandler? handler = IndexStateChanged;
            if (handler == null)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastStateNotificationTimestamp);
            long elapsedMs = (now - last) * 1000 / Stopwatch.Frequency;
            if (!force && last != 0 && elapsedMs < StateNotificationThrottleMs)
            {
                return;
            }

            Interlocked.Exchange(ref _lastStateNotificationTimestamp, now);
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _log.Warn("An index-state subscriber failed: " + ex.Message);
            }
        }

        private void ScheduleRecentUsageSave()
        {
            try
            {
                if (_recentUsageSaveTimer == null)
                {
                    _recentUsageSaveTimer = new Timer(OnRecentUsageSaveTimer, null, Timeout.Infinite, Timeout.Infinite);
                }

                _recentUsageSaveTimer.Change(RecentUsageSaveDebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _log.Warn("Could not schedule saving the recent usage data: " + ex.Message);
            }
        }

        private void OnRecentUsageSaveTimer(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                lock (_indexGate)
                {
                    _recentUsage.Save();
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Saving the recent usage data failed: " + ex.Message);
            }
        }

        private int SafeFileCount()
        {
            try
            {
                return _cppIndex.FileCount;
            }
            catch
            {
                return 0;
            }
        }

        private static string GetShortStatus(IndexStateEnum state)
        {
            switch (state)
            {
                case IndexStateEnum.Building:
                    return "Indexing...";
                case IndexStateEnum.Ready:
                    return "Ready";
                case IndexStateEnum.Failed:
                    return "Indexing failed";
                default:
                    return "Not indexed yet";
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _cppIndex.ItemsChanged -= OnIndexItemsChanged;
                _cppIndex.ProgressChanged -= OnIndexProgressChanged;
            }
            catch (Exception ex)
            {
                _log.Warn("Could not unsubscribe from index events: " + ex.Message);
            }

            Timer? timer = _recentUsageSaveTimer;
            _recentUsageSaveTimer = null;
            if (timer != null)
            {
                try
                {
                    timer.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not dispose the recent-usage save timer: " + ex.Message);
                }
            }

            try
            {
                lock (_indexGate)
                {
                    _recentUsage.Save();
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Saving the recent usage data during shutdown failed: " + ex.Message);
            }
        }
    }
}



using System;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Options;
using Microsoft.VisualStudio.Shell;

namespace CtrlTNav.Vsix.Options
{
    /// <summary>
    /// Single source of truth for the current <see cref="CtrlTNavOptions"/> snapshot. The snapshot is
    /// replaced atomically when the user applies the options page, so readers on the UI thread and on
    /// background indexing threads always observe a consistent object.
    /// </summary>
    public sealed class OptionsProvider : IDisposable
    {
        private readonly CtrlTNavPackage _package;
        private readonly ILog _log;
        private readonly object _gate = new object();

        private CtrlTNavOptions _current;
        private CtrlTNavOptionsPage? _page;
        private bool _disposed;

        /// <summary>Creates the provider. Call <see cref="InitializeOnUiThread"/> on the UI thread afterwards.</summary>
        internal OptionsProvider(CtrlTNavPackage package, ILog log)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _current = CtrlTNavOptions.CreateDefault();
        }

        /// <summary>Raised after the settings changed. Raised on the thread that applied the change (UI thread).</summary>
        public event EventHandler<OptionsChangedEventArgs>? OptionsChanged;

        /// <summary>
        /// Current immutable options snapshot. Callers must not mutate the returned instance; every
        /// <see cref="CtrlTNavOptions"/> property is treated as read-only by the extension.
        /// </summary>
        public CtrlTNavOptions Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        /// <summary>
        /// Loads the persisted page state (creating the <see cref="CtrlTNavOptionsPage"/> on first use) and
        /// hooks the page's apply callback. Must be called on the Visual Studio main thread.
        /// </summary>
        public void InitializeOnUiThread()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                CtrlTNavOptionsPage? page = _package.GetDialogPage(typeof(CtrlTNavOptionsPage)) as CtrlTNavOptionsPage;
                if (page == null)
                {
                    _log.Warn("The Ctrl+T Navigator options page could not be created; defaults are used.");
                    return;
                }

                lock (_gate)
                {
                    if (_page != null)
                    {
                        return;
                    }

                    _page = page;
                }

                page.OptionsApplied += OnOptionsApplied;

                // GetDialogPage has already loaded the persisted values from the user settings store.
                CtrlTNavOptions loaded = page.ToOptions();
                Replace(loaded, raiseEvent: false, source: "stored settings");
                _log.Info("Options loaded (OverrideCtrlT=" + loaded.OverrideCtrlT + ", AutoStartIndexing=" + loaded.AutoStartIndexing + ").");
            }
            catch (Exception ex)
            {
                _log.Error("Could not load the extension options; using defaults.", ex);
            }
        }

        /// <summary>Applies a new options snapshot (also used by tests and by the options page).</summary>
        public void Update(CtrlTNavOptions options, string source = "options")
        {
            if (options == null)
            {
                return;
            }

            Replace(options.Normalize(), raiseEvent: true, source: source);
        }

        private void OnOptionsApplied(object? sender, CtrlTNavOptions options)
        {
            try
            {
                Replace(options.Normalize(), raiseEvent: true, source: "options page");
            }
            catch (Exception ex)
            {
                _log.Error("Applying the options page failed.", ex);
            }
        }

        private void Replace(CtrlTNavOptions normalized, bool raiseEvent, string source)
        {
            if (normalized == null)
            {
                return;
            }

            CtrlTNavOptions previous;
            lock (_gate)
            {
                previous = _current;
                _current = normalized;
            }

            _log.Info("Options updated (" + source + ").");

            if (!raiseEvent)
            {
                return;
            }

            EventHandler<OptionsChangedEventArgs>? handler = OptionsChanged;
            if (handler == null)
            {
                return;
            }

            var args = new OptionsChangedEventArgs(previous, normalized, source);
            foreach (EventHandler<OptionsChangedEventArgs> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, args);
                }
                catch (Exception ex)
                {
                    // A failing subscriber (for example a bug in the index coordinator) must not take
                    // down the options dialog or the other subscribers.
                    _log.Error("An options-change subscriber failed.", ex);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CtrlTNavOptionsPage? page;
            lock (_gate)
            {
                page = _page;
                _page = null;
            }

            if (page != null)
            {
                try
                {
                    page.OptionsApplied -= OnOptionsApplied;
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not detach from the options page: " + ex.Message);
                }
            }
        }
    }

    /// <summary>Describes a change of the extension options and classifies it for the subscribers.</summary>
    public sealed class OptionsChangedEventArgs : EventArgs
    {
        public OptionsChangedEventArgs(CtrlTNavOptions previous, CtrlTNavOptions current, string source)
        {
            Previous = previous ?? CtrlTNavOptions.CreateDefault();
            Current = current ?? CtrlTNavOptions.CreateDefault();
            Source = source ?? string.Empty;

            IndexScopeChanged =
                Previous.IndexSolutionFiles != Current.IndexSolutionFiles ||
                Previous.IndexCppFiles != Current.IndexCppFiles ||
                Previous.IndexIncludeDirectories != Current.IndexIncludeDirectories ||
                Previous.AutoStartIndexing != Current.AutoStartIndexing ||
                Previous.MaxFileSizeKb != Current.MaxFileSizeKb ||
                !string.Equals(Previous.AdditionalIncludeDirectories, Current.AdditionalIncludeDirectories, StringComparison.Ordinal) ||
                !string.Equals(Previous.ExcludePatterns, Current.ExcludePatterns, StringComparison.Ordinal) ||
                !string.Equals(Previous.SourceExtensions, Current.SourceExtensions, StringComparison.Ordinal);

            RecentUsageChanged =
                Previous.EnableRecentUsage != Current.EnableRecentUsage ||
                Previous.RecentUsageCapacity != Current.RecentUsageCapacity;

            DisplayChanged =
                Previous.ShowLineNumbers != Current.ShowLineNumbers ||
                Previous.ShowRelativePaths != Current.ShowRelativePaths ||
                Previous.CloseOnFocusLost != Current.CloseOnFocusLost ||
                Previous.ShowIndexStatus != Current.ShowIndexStatus;
        }

        public CtrlTNavOptions Previous { get; }

        public CtrlTNavOptions Current { get; }

        /// <summary>Human readable description of where the change came from (for logging).</summary>
        public string Source { get; }

        /// <summary>True when the change requires rebuilding the C/C++ index.</summary>
        public bool IndexScopeChanged { get; }

        /// <summary>True when the recent-usage tracker has to be reconfigured.</summary>
        public bool RecentUsageChanged { get; }

        /// <summary>True when the search window appearance/behaviour has to be refreshed.</summary>
        public bool DisplayChanged { get; }
    }
}


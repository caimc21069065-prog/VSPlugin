using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Options;
using CtrlTNav.Indexing;
using CtrlTNav.Vsix.Options;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Events;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace CtrlTNav.Vsix.Services
{
    /// <summary>
    /// Orchestrates the background C/C++ index for the extension: full rebuild when a solution is opened,
    /// incremental re-index when a document is saved or changes on disk, status-bar progress, and cleanup
    /// when the solution closes or the package is disposed.
    /// </summary>
    /// <remarks>
    /// Solution lifecycle notifications come from <see cref="SolutionEvents"/> (raised on background
    /// threads), document saves from <see cref="IVsRunningDocTableEvents3"/>, and external edits from a
    /// <see cref="FileSystemWatcher"/> with an 800 ms debounce. All COM access goes through
    /// <see cref="JoinableTaskFactory"/> to the UI thread.
    /// </remarks>
    public sealed class IndexCoordinator : IDisposable, IVsRunningDocTableEvents3
    {
        private const int S_OK = 0;
        private const int WatchDebounceMs = 800;
        private const int RebuildDebounceMs = 1500;
        private const int InitialRebuildDelayMs = 400;
        private const int StatusThrottleMs = 120;

        private readonly CtrlTNavPackage _package;
        private readonly ICppIndex _index;
        private readonly VsSolutionFileProvider _fileProvider;
        private readonly VsSearchService _searchService;
        private readonly OptionsProvider _optionsProvider;
        private readonly ILog _log;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly object _gate = new object();
        private readonly Dictionary<string, SourceFileInfo> _trackedFiles = new Dictionary<string, SourceFileInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _pendingFileChanges = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _buildCancellation;
        private FileSystemWatcher? _watcher;
        private Timer? _watcherTimer;
        private Timer? _rebuildTimer;
        private IVsRunningDocumentTable? _runningDocumentTable;
        private uint _runningDocumentTableCookie;
        private bool _runningDocumentTableAdvised;
        private bool _initialized;
        private bool _disposed;
        private int _buildInProgress;
        private int _rebuildRequested;
        private int _lastStatusTick;
        private string? _solutionPath;
        private string _pendingRebuildReason = "solution opened";

        internal IndexCoordinator(
            CtrlTNavPackage package,
            ICppIndex index,
            VsSolutionFileProvider fileProvider,
            VsSearchService searchService,
            OptionsProvider optionsProvider,
            ILog log)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _joinableTaskFactory = package.JoinableTaskFactory;
        }

        /// <summary>Wires up all lifecycle events and kicks off indexing if a solution is already open.</summary>
        public void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            lock (_gate)
            {
                if (_initialized || _disposed)
                {
                    return;
                }

                _initialized = true;
            }

            try
            {
                SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;
                SolutionEvents.OnAfterCloseSolution += OnAfterCloseSolution;
                SolutionEvents.OnAfterBackgroundSolutionLoadComplete += OnAfterBackgroundSolutionLoadComplete;
                SolutionEvents.OnAfterOpenProject += OnAfterOpenProject;
                SolutionEvents.OnAfterLoadProject += OnAfterLoadProject;
                SolutionEvents.OnAfterRenameSolution += OnAfterRenameSolution;
                _optionsProvider.OptionsChanged += OnOptionsChanged;
                _index.ProgressChanged += OnIndexProgressChanged;

                AdviseRunningDocumentTable();

                string? solutionPath = VsDteUtilities.GetSolutionPath(VsDteUtilities.GetDte(_package));
                if (!string.IsNullOrEmpty(solutionPath))
                {
                    _solutionPath = solutionPath;
                    _log.Info("A solution is already open; scheduling the initial index build.");
                    ScheduleRebuild("solution already open", initial: true);
                }
                else
                {
                    _log.Info("No solution is open; waiting for SolutionEvents.");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Initializing the index coordinator failed; background indexing is disabled.", ex);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Solution lifecycle (raised on background threads by SolutionEvents)
        // ---------------------------------------------------------------------------------------------

        private void OnAfterOpenSolution(object? sender, OpenSolutionEventArgs e)
        {
            HandleSolutionChanged("solution opened", initial: false);
        }

        private void OnAfterBackgroundSolutionLoadComplete(object? sender, EventArgs e)
        {
            HandleSolutionChanged("solution load completed", initial: true);
        }

        private void OnAfterOpenProject(object? sender, OpenProjectEventArgs e)
        {
            HandleSolutionChanged("project opened", initial: false);
        }

        private void OnAfterLoadProject(object? sender, LoadProjectEventArgs e)
        {
            HandleSolutionChanged("project loaded", initial: false);
        }

        private void OnAfterRenameSolution(object? sender, RenameSolutionEventArgs e)
        {
            HandleSolutionChanged("solution renamed", initial: true);
        }

        private void OnAfterCloseSolution(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _log.Info("Solution closed; clearing the index.");
                CancelBuild();
                StopWatcher();
                _solutionPath = null;

                lock (_gate)
                {
                    _trackedFiles.Clear();
                }

                _pendingFileChanges.Clear();
                try
                {
                    _index.Clear();
                }
                catch (Exception ex)
                {
                    _log.Warn("Clearing the index after solution close failed: " + ex.Message);
                }

                _searchService.Reset();
                _package.SetStatusProgress(false, CtrlTNavConstants.ProductName + ": no solution open");
            }
            catch (Exception ex)
            {
                _log.Error("Handling solution close failed.", ex);
            }
        }

        private void HandleSolutionChanged(string reason, bool initial)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                CtrlTNavOptions options = _optionsProvider.Current;
                _log.Info("Solution event: " + reason + " (AutoStartIndexing=" + options.AutoStartIndexing + ", IndexCppFiles=" + options.IndexCppFiles + ").");

                if (!options.AutoStartIndexing || !options.IndexCppFiles)
                {
                    _package.SetStatusProgress(false, CtrlTNavConstants.ProductName + ": indexing disabled in the options");
                    return;
                }

                string? solutionPath = GetSolutionPathOnUiThread();
                if (string.IsNullOrEmpty(solutionPath))
                {
                    _log.Info("Ignoring solution event '" + reason + "': no solution path is available yet.");
                    return;
                }

                _solutionPath = solutionPath;
                CancelBuild();
                ScheduleRebuild(reason, initial);
            }
            catch (Exception ex)
            {
                _log.Error("Handling solution event '" + reason + "' failed.", ex);
            }
        }

        private string? GetSolutionPathOnUiThread()
        {
            try
            {
                return _joinableTaskFactory.Run(async () =>
                {
                    await _joinableTaskFactory.SwitchToMainThreadAsync();
                    return VsDteUtilities.GetSolutionPath(VsDteUtilities.GetDte(_package));
                });
            }
            catch (Exception ex)
            {
                _log.Warn("Could not read the solution path: " + ex.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Full rebuild
        // ---------------------------------------------------------------------------------------------

        private void ScheduleRebuild(string reason, bool initial)
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingRebuildReason = reason;
                if (_rebuildTimer == null)
                {
                    _rebuildTimer = new Timer(OnRebuildTimer, null, Timeout.Infinite, Timeout.Infinite);
                }

                int delay = initial ? InitialRebuildDelayMs : RebuildDebounceMs;
                _rebuildTimer.Change(delay, Timeout.Infinite);
            }
        }

        private void OnRebuildTimer(object? state)
        {
            if (_disposed)
            {
                return;
            }

            string reason;
            lock (_gate)
            {
                reason = _pendingRebuildReason;
            }

            // Fire and forget: RunBuildAsync catches everything and reports through the log/status bar.
            _ = RunBuildAsync(reason);
        }

        private async Task RunBuildAsync(string reason)
        {
            if (_disposed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _buildInProgress, 1, 0) != 0)
            {
                // A build is already running; remember that the result is stale and repeat the build once
                // the running one has finished (for example when a second solution/project event arrives).
                Interlocked.Exchange(ref _rebuildRequested, 1);
                _log.Info("Skipping rebuild ('" + reason + "'): another build is already running; it will be repeated afterwards.");
                return;
            }

            CancellationTokenSource cancellation;
            lock (_gate)
            {
                try
                {
                    _buildCancellation?.Cancel();
                }
                catch
                {
                    // ignore
                }

                cancellation = new CancellationTokenSource();
                _buildCancellation = cancellation;
            }

            CancellationToken token = cancellation.Token;
            try
            {
                StopWatcher();
                CtrlTNavOptions options = _optionsProvider.Current;
                _package.SetStatusProgress(true, CtrlTNavConstants.ProductName + ": scanning solution files...", 0, 0);

                IReadOnlyList<SourceFileInfo> files = await Task.Run(() => _fileProvider.GetSourceFiles(), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (!options.IndexCppFiles)
                {
                    _log.Info("Indexing of C/C++ files is disabled; keeping the previously indexed content.");
                    return;
                }

                _log.Info("Starting index build over " + files.Count + " files ('" + reason + "').");
                if (files.Count > 0)
                {
                    _package.SetStatusProgress(true, CtrlTNavConstants.ProductName + ": indexing " + files.Count + " files...", 0, (uint)files.Count);
                    await _index.BuildAsync(files, token).ConfigureAwait(false);
                }
                else
                {
                    _index.Clear();
                }

                token.ThrowIfCancellationRequested();

                lock (_gate)
                {
                    _trackedFiles.Clear();
                    foreach (SourceFileInfo file in files)
                    {
                        if (file != null && !string.IsNullOrEmpty(file.FilePath))
                        {
                            _trackedFiles[file.FilePath] = file;
                        }
                    }
                }

                string? solutionPath = _fileProvider.SolutionPath;
                if (!string.IsNullOrEmpty(solutionPath))
                {
                    _solutionPath = solutionPath;
                }

                StartWatcher();

                _package.SetStatusProgress(false, CtrlTNavConstants.ProductName + ": " + _index.ItemCount + " symbols from " + files.Count + " files indexed");
                _log.Info("Index build finished: " + _index.ItemCount + " symbols from " + files.Count + " files.");
            }
            catch (OperationCanceledException)
            {
                _log.Info("Index build cancelled ('" + reason + "').");
                _package.SetStatusProgress(false, CtrlTNavConstants.ProductName + ": indexing cancelled");
            }
            catch (Exception ex)
            {
                _log.Error("Index build failed ('" + reason + "').", ex);
                _package.SetStatusProgress(false, CtrlTNavConstants.ProductName + ": indexing failed - see log");
            }
            finally
            {
                Interlocked.Exchange(ref _buildInProgress, 0);

                if (Interlocked.Exchange(ref _rebuildRequested, 0) != 0 && !_disposed)
                {
                    _log.Info("A rebuild was requested while the previous build was running; starting it now.");
                    ScheduleRebuild("rebuild requested during indexing", initial: true);
                }
            }
        }

        private void CancelBuild()
        {
            CancellationTokenSource? cancellation;
            lock (_gate)
            {
                cancellation = _buildCancellation;
                _buildCancellation = null;
            }

            if (cancellation == null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (Exception ex)
            {
                _log.Warn("Cancelling the index build failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Progress reporting
        // ---------------------------------------------------------------------------------------------

        private void OnIndexProgressChanged(object? sender, IndexProgressEventArgs e)
        {
            if (_disposed || e == null)
            {
                return;
            }

            int now = Environment.TickCount;
            if (unchecked(now - _lastStatusTick) < StatusThrottleMs)
            {
                return;
            }

            _lastStatusTick = now;

            try
            {
                IndexProgress progress = e.Progress;
                uint total = (uint)Math.Max(1, progress.FilesTotal);
                uint processed = (uint)Math.Max(0, progress.FilesProcessed);
                string label = string.Format(
                    "{0}: indexing {1}/{2} files ({3} symbols)",
                    CtrlTNavConstants.ProductName,
                    progress.FilesProcessed,
                    progress.FilesTotal,
                    progress.ItemCount);
                _package.SetStatusProgress(true, label, processed, total);
            }
            catch (Exception ex)
            {
                _log.Info("Reporting index progress failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Document save notifications (IVsRunningDocTableEvents3)
        // ---------------------------------------------------------------------------------------------

        private void AdviseRunningDocumentTable()
        {
            try
            {
                if (((IServiceProvider)_package).GetService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable runningDocumentTable)
                {
                    _runningDocumentTable = runningDocumentTable;
                    int hr = runningDocumentTable.AdviseRunningDocTableEvents(this, out _runningDocumentTableCookie);
                    _runningDocumentTableAdvised = hr >= 0;
                    if (!_runningDocumentTableAdvised)
                    {
                        _log.Warn("AdviseRunningDocTableEvents failed (hr=0x" + hr.ToString("X8") + "); files will not be re-indexed on save.");
                    }
                    else
                    {
                        _log.Info("Running document table events advised (save re-indexing enabled).");
                    }
                }
                else
                {
                    _log.Warn("SVsRunningDocumentTable is not available; files will not be re-indexed on save.");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Could not advise running document table events: " + ex.Message);
            }
        }

        private void UnadviseRunningDocumentTable()
        {
            if (!_runningDocumentTableAdvised || _runningDocumentTable == null)
            {
                return;
            }

            _runningDocumentTableAdvised = false;
            try
            {
                _runningDocumentTable.UnadviseRunningDocTableEvents(_runningDocumentTableCookie);
            }
            catch (Exception ex)
            {
                _log.Warn("Could not unadvise running document table events: " + ex.Message);
            }
            finally
            {
                _runningDocumentTable = null;
            }
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            return S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            return S_OK;
        }

        public int OnAfterSave(uint docCookie)
        {
            if (_disposed)
            {
                return S_OK;
            }

            try
            {
                string? path = GetDocumentMoniker(docCookie);
                if (!string.IsNullOrEmpty(path))
                {
                    ScheduleIncrementalUpdate(path!, "saved");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Re-indexing the saved document failed: " + ex.Message);
            }

            return S_OK;
        }

        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs)
        {
            return S_OK;
        }

        public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
        {
            return S_OK;
        }

        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
        {
            return S_OK;
        }

        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame)
        {
            return S_OK;
        }

        public int OnBeforeSave(uint docCookie)
        {
            return S_OK;
        }

        private string? GetDocumentMoniker(uint docCookie)
        {
            IVsRunningDocumentTable? runningDocumentTable = _runningDocumentTable;
            if (runningDocumentTable == null)
            {
                return null;
            }

            if (!ThreadHelper.CheckAccess())
            {
                return _joinableTaskFactory.Run(async () =>
                {
                    await _joinableTaskFactory.SwitchToMainThreadAsync();
                    return GetDocumentMoniker(docCookie);
                });
            }

            try
            {
                uint flags;
                uint readLocks;
                uint editLocks;
                uint itemId;
                string moniker;
                IVsHierarchy? hierarchy;
                IntPtr docData;
                int hr = runningDocumentTable.GetDocumentInfo(
                    docCookie,
                    out flags,
                    out readLocks,
                    out editLocks,
                    out moniker,
                    out hierarchy,
                    out itemId,
                    out docData);
                return hr >= 0 ? moniker : null;
            }
            catch (Exception ex)
            {
                _log.Info("Could not resolve the saved document moniker: " + ex.Message);
                return null;
            }
        }

        private void ScheduleIncrementalUpdate(string filePath, string reason)
        {
            try
            {
                CtrlTNavOptions options = _optionsProvider.Current;
                if (!options.IndexCppFiles)
                {
                    return;
                }

                if (GlobMatcher.IsExcluded(filePath, options.GetExcludePatterns()))
                {
                    return;
                }

                SourceFileInfo? info;
                lock (_gate)
                {
                    _trackedFiles.TryGetValue(filePath, out info);
                }

                if (info == null)
                {
                    _log.Info("Not re-indexing '" + filePath + "' after " + reason + ": the file is not part of the index.");
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _index.UpdateFileAsync(info, _package.DisposalToken).ConfigureAwait(false);
                        _log.Info("Incremental re-index finished for '" + filePath + "' (" + reason + ").");
                    }
                    catch (OperationCanceledException)
                    {
                        // shutting down
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Incremental re-index failed for '" + filePath + "': " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                _log.Warn("Scheduling an incremental re-index failed for '" + filePath + "': " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // File system watcher
        // ---------------------------------------------------------------------------------------------

        private void StartWatcher()
        {
            string? solutionPath = _solutionPath;
            if (string.IsNullOrEmpty(solutionPath))
            {
                return;
            }

            string directory;
            try
            {
                directory = Path.GetDirectoryName(solutionPath!) ?? string.Empty;
            }
            catch
            {
                return;
            }

            if (directory.Length == 0 || !Directory.Exists(directory))
            {
                _log.Info("No file system watcher: solution directory is not available.");
                return;
            }

            try
            {
                StopWatcher();

                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    Filter = "*.*",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                };

                watcher.Changed += OnWatchedFileChanged;
                watcher.Created += OnWatchedFileCreated;
                watcher.Deleted += OnWatchedFileDeleted;
                watcher.Renamed += OnWatchedFileRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;

                lock (_gate)
                {
                    _watcher = watcher;
                    _watcherTimer = new Timer(OnWatcherTimer, null, Timeout.Infinite, Timeout.Infinite);
                }

                _log.Info("File system watcher started on '" + directory + "'.");
            }
            catch (Exception ex)
            {
                _log.Warn("Could not start the file system watcher for '" + directory + "': " + ex.Message);
            }
        }

        private void StopWatcher()
        {
            FileSystemWatcher? watcher;
            Timer? timer;
            lock (_gate)
            {
                watcher = _watcher;
                _watcher = null;
                timer = _watcherTimer;
                _watcherTimer = null;
            }

            if (watcher != null)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Changed -= OnWatchedFileChanged;
                    watcher.Created -= OnWatchedFileCreated;
                    watcher.Deleted -= OnWatchedFileDeleted;
                    watcher.Renamed -= OnWatchedFileRenamed;
                    watcher.Error -= OnWatcherError;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Warn("Disposing the file system watcher failed: " + ex.Message);
                }
            }

            if (timer != null)
            {
                try
                {
                    timer.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Warn("Disposing the watcher debounce timer failed: " + ex.Message);
                }
            }
        }

        private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
        {
            EnqueueWatchedPath(e.FullPath);
        }

        private void OnWatchedFileCreated(object sender, FileSystemEventArgs e)
        {
            EnqueueWatchedPath(e.FullPath);
        }

        private void OnWatchedFileDeleted(object sender, FileSystemEventArgs e)
        {
            EnqueueWatchedPath(e.FullPath);
        }

        private void OnWatchedFileRenamed(object sender, RenamedEventArgs e)
        {
            EnqueueWatchedPath(e.OldFullPath);
            EnqueueWatchedPath(e.FullPath);
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _log.Warn("The file system watcher overflowed or failed (" + e.GetException().Message + "); scheduling a full rebuild.");
            ScheduleRebuild("watcher overflow", initial: false);
        }

        private void EnqueueWatchedPath(string? path)
        {
            if (_disposed || string.IsNullOrEmpty(path) || !IsPotentiallyRelevant(path!))
            {
                return;
            }

            try
            {
                _pendingFileChanges[path!] = DateTime.UtcNow;
                Timer? timer;
                lock (_gate)
                {
                    timer = _watcherTimer;
                }

                timer?.Change(WatchDebounceMs, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                _log.Info("Queueing the file change failed: " + ex.Message);
            }
        }

        private bool IsPotentiallyRelevant(string path)
        {
            try
            {
                CtrlTNavOptions options = _optionsProvider.Current;
                if (!options.IndexCppFiles)
                {
                    return false;
                }

                if (GlobMatcher.IsExcluded(path, options.GetExcludePatterns()))
                {
                    return false;
                }

                return SourceFileScanner.IsSupportedFile(path, options);
            }
            catch
            {
                return false;
            }
        }

        private void OnWatcherTimer(object? state)
        {
            if (_disposed)
            {
                return;
            }

            var paths = new List<string>(_pendingFileChanges.Keys);
            _pendingFileChanges.Clear();
            if (paths.Count == 0)
            {
                return;
            }

            _ = Task.Run(() => ProcessWatchedChangesAsync(paths));
        }

        private async Task ProcessWatchedChangesAsync(List<string> paths)
        {
            foreach (string path in paths)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    await ProcessWatchedChangeAsync(path).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warn("Processing the external change of '" + path + "' failed: " + ex.Message);
                }
            }
        }

        private async Task ProcessWatchedChangeAsync(string path)
        {
            SourceFileInfo? info;
            lock (_gate)
            {
                _trackedFiles.TryGetValue(path, out info);
            }

            bool exists;
            try
            {
                exists = File.Exists(path);
            }
            catch
            {
                exists = false;
            }

            if (info == null)
            {
                // A new file that appeared after the last enumeration cannot be added incrementally
                // because its owning project is unknown; the next rebuild (or save) picks it up.
                return;
            }

            if (!exists)
            {
                try
                {
                    _index.RemoveFile(path);
                }
                catch (Exception ex)
                {
                    _log.Warn("Removing the deleted file '" + path + "' from the index failed: " + ex.Message);
                }

                lock (_gate)
                {
                    _trackedFiles.Remove(path);
                }

                _log.Info("Removed deleted file from the index: " + path);
                return;
            }

            await _index.UpdateFileAsync(info, _package.DisposalToken).ConfigureAwait(false);
            _log.Info("Re-indexed externally changed file: " + path);
        }

        // ---------------------------------------------------------------------------------------------
        // Options and disposal
        // ---------------------------------------------------------------------------------------------

        private void OnOptionsChanged(object? sender, OptionsChangedEventArgs e)
        {
            if (_disposed || e == null)
            {
                return;
            }

            try
            {
                if (!e.Current.AutoStartIndexing || !e.Current.IndexCppFiles)
                {
                    _log.Info("Indexing was disabled in the options; cancelling pending work.");
                    CancelBuild();
                    StopWatcher();
                    return;
                }

                if (!e.IndexScopeChanged)
                {
                    return;
                }

                _log.Info("Indexing-related options changed; scheduling a rebuild.");
                ScheduleRebuild("options changed", initial: false);
            }
            catch (Exception ex)
            {
                _log.Error("Handling the options change failed.", ex);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            try
            {
                SolutionEvents.OnAfterOpenSolution -= OnAfterOpenSolution;
                SolutionEvents.OnAfterCloseSolution -= OnAfterCloseSolution;
                SolutionEvents.OnAfterBackgroundSolutionLoadComplete -= OnAfterBackgroundSolutionLoadComplete;
                SolutionEvents.OnAfterOpenProject -= OnAfterOpenProject;
                SolutionEvents.OnAfterLoadProject -= OnAfterLoadProject;
                SolutionEvents.OnAfterRenameSolution -= OnAfterRenameSolution;
            }
            catch (Exception ex)
            {
                _log.Warn("Could not unsubscribe from solution events: " + ex.Message);
            }

            try
            {
                _optionsProvider.OptionsChanged -= OnOptionsChanged;
                _index.ProgressChanged -= OnIndexProgressChanged;
            }
            catch (Exception ex)
            {
                _log.Warn("Could not unsubscribe from index/options events: " + ex.Message);
            }

            CancelBuild();
            StopWatcher();
            UnadviseRunningDocumentTable();

            Timer? rebuildTimer;
            lock (_gate)
            {
                rebuildTimer = _rebuildTimer;
                _rebuildTimer = null;
            }

            if (rebuildTimer != null)
            {
                try
                {
                    rebuildTimer.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not dispose the rebuild timer: " + ex.Message);
                }
            }

            _pendingFileChanges.Clear();
            _log.Info("Index coordinator disposed.");
        }
    }
}



using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Options;

namespace CtrlTNav.Indexing
{
    /// <summary>
    /// Background C/C++ symbol index. Performs a parallel, cancellable full build and supports
    /// incremental single-file updates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thread safety: mutations are serialized with an internal lock and the immutable
    /// <see cref="Snapshot"/> array is swapped atomically, so readers never block and never see a torn
    /// view. <see cref="ProgressChanged"/> and <see cref="ItemsChanged"/> are raised on background threads.
    /// </para>
    /// <para>
    /// Cancelling <see cref="BuildAsync"/> keeps the previous index content and does not throw; the
    /// returned task completes normally. Editors can call <see cref="UpdateFileAsync"/> after a save event
    /// to re-parse just one file (target NF4). Events raised by this class never throw to the caller:
    /// subscriber exceptions are logged and swallowed so a UI bug cannot break the index.
    /// </para>
    /// </remarks>
    public sealed class CppIndex : ICppIndex
    {
        private static readonly NavigationItem[] EmptyItems = new NavigationItem[0];
        private static readonly SourceFileInfo[] EmptyFiles = new SourceFileInfo[0];

        private sealed class IndexedFile
        {
            public SourceFileInfo Info = null!;
            public NavigationItem[] Items = EmptyItems;
            public DateTime LastWriteUtc;
            public long Length;
        }

        private readonly CtrlTNavOptions _options;
        private readonly ILog _log;
        private readonly CppSymbolExtractor _extractor = new CppSymbolExtractor();
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private readonly string[] _extensions;
        private readonly string[] _excludePatterns;
        private readonly int _maxDegreeOfParallelism;

        private Dictionary<string, IndexedFile> _files = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        private NavigationItem[] _snapshot = EmptyItems;
        private IndexProgress _progress = IndexProgress.Empty;
        private int _state = (int)IndexState.NotStarted;
        private int _fileCount;
        private int _itemCount;
        private int _buildItemCount;
        private string? _currentFile;
        private bool _disposed;

        public CppIndex(CtrlTNavOptions? options = null, ILog? log = null)
        {
            _options = (options ?? CtrlTNavOptions.CreateDefault()).Normalize();
            _log = log ?? NullLog.Instance;
            _extensions = CppFileTypes.NormalizeExtensions(_options.GetSourceExtensions());
            _excludePatterns = _options.GetExcludePatterns();

            int parallelism = Environment.ProcessorCount;
            if (parallelism < 2)
            {
                parallelism = 2;
            }

            if (parallelism > 8)
            {
                parallelism = 8;
            }

            _maxDegreeOfParallelism = parallelism;
        }

        public int FileCount
        {
            get { return Volatile.Read(ref _fileCount); }
        }

        public int ItemCount
        {
            get { return Volatile.Read(ref _itemCount); }
        }

        public IndexState State
        {
            get { return (IndexState)Volatile.Read(ref _state); }
        }

        public IndexProgress Progress
        {
            get
            {
                lock (_sync)
                {
                    return _progress;
                }
            }
        }

        public event EventHandler<IndexProgressEventArgs>? ProgressChanged;

        public event EventHandler<IndexItemsChangedEventArgs>? ItemsChanged;

        public async Task BuildAsync(IReadOnlyCollection<SourceFileInfo> files, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return;
            }

            bool acquired = false;
            try
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;

                if (_disposed)
                {
                    return;
                }

                SourceFileInfo[] candidates = PrepareFiles(files);
                IndexState fallbackState = FileCount > 0 ? IndexState.Ready : IndexState.NotStarted;

                SetState(IndexState.Building);
                SetProgress(new IndexProgress(candidates.Length, 0, ItemCount, null), false);
                RaiseProgress();

                var results = new IndexedFile?[candidates.Length];
                int processed = 0;
                Interlocked.Exchange(ref _buildItemCount, 0);
                var progressClock = Stopwatch.StartNew();
                bool cancelled = false;

                try
                {
                    Parallel.For(
                        0,
                        candidates.Length,
                        new ParallelOptions
                        {
                            CancellationToken = cancellationToken,
                            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
                        },
                        index =>
                        {
                            SourceFileInfo info = candidates[index];
                            Volatile.Write(ref _currentFile, info.FilePath);
                            IndexedFile? indexed = IndexFile(info, cancellationToken);
                            if (indexed != null)
                            {
                                results[index] = indexed;
                                Interlocked.Add(ref _buildItemCount, indexed.Items.Length);
                            }

                            int done = Interlocked.Increment(ref processed);
                            if (done == candidates.Length || progressClock.ElapsedMilliseconds >= 50)
                            {
                                progressClock.Restart();
                                SetProgress(
                                    new IndexProgress(
                                        candidates.Length,
                                        done,
                                        Volatile.Read(ref _buildItemCount),
                                        Volatile.Read(ref _currentFile)),
                                    false);
                                RaiseProgress();
                            }
                        });
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (AggregateException aggregate) when (ContainsCancellation(aggregate))
                {
                    cancelled = true;
                }
                catch (Exception exception)
                {
                    _log.Error("Ctrl+T Navigator: index build failed.", exception);
                    SetState(IndexState.Failed);
                    RaiseProgress();
                    return;
                }

                Volatile.Write(ref _currentFile, null);

                if (cancelled || cancellationToken.IsCancellationRequested)
                {
                    SetState(fallbackState);
                    SetProgress(
                        new IndexProgress(FileCount, FileCount, ItemCount, null),
                        false);
                    RaiseProgress();
                    _log.Info("Ctrl+T Navigator: index build cancelled; previous index retained.");
                    return;
                }

                var newFiles = new Dictionary<string, IndexedFile>(candidates.Length, StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < candidates.Length; index++)
                {
                    IndexedFile? indexed = results[index];
                    if (indexed != null)
                    {
                        newFiles[candidates[index].FilePath] = indexed;
                    }
                }

                NavigationItem[] snapshot;
                int itemCount;
                bool installed;
                lock (_sync)
                {
                    // Re-check under the writer lock: a cancellation or a concurrent Clear()/Dispose()
                    // may have happened after the first check. Installing here would resurrect a
                    // solution that was closed (or a disposed index).
                    installed = !_disposed && !cancellationToken.IsCancellationRequested;
                    if (installed)
                    {
                        _files = newFiles;
                        snapshot = RebuildSnapshotLocked(out _, out itemCount);
                    }
                    else
                    {
                        snapshot = EmptyItems;
                        itemCount = 0;
                    }
                }

                if (!installed)
                {
                    SetState(fallbackState);
                    SetProgress(new IndexProgress(FileCount, FileCount, ItemCount, null), false);
                    RaiseProgress();
                    _log.Info("Ctrl+T Navigator: index build discarded (cancelled or index disposed).");
                    return;
                }

                SetState(IndexState.Ready);
                SetProgress(new IndexProgress(candidates.Length, candidates.Length, itemCount, null), false);
                RaiseProgress();
                RaiseItemsChanged(null, snapshot, true);
                _log.Info(
                    "Ctrl+T Navigator: indexed " + Volatile.Read(ref _fileCount) + " files, " +
                    Volatile.Read(ref _itemCount) + " items.");
            }
            catch (OperationCanceledException)
            {
                // Cancelled while waiting for the operation gate: keep the previous index.
            }
            finally
            {
                if (acquired)
                {
                    _operationGate.Release();
                }
            }
        }

        public async Task UpdateFileAsync(SourceFileInfo file, CancellationToken cancellationToken = default)
        {
            if (_disposed || file == null || string.IsNullOrEmpty(file.FilePath))
            {
                return;
            }

            bool acquired = false;
            try
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;

                if (_disposed)
                {
                    return;
                }

                string key = NormalizeKey(file.FilePath);
                lock (_sync)
                {
                    if (!_files.ContainsKey(key))
                    {
                        _log.Info("Ctrl+T Navigator: ignoring update for untracked file " + key);
                        return;
                    }
                }

                if (!SourceFileScanner.IsSupportedFile(key, _extensions, _excludePatterns))
                {
                    RemoveFile(key);
                    return;
                }

                var info = new SourceFileInfo(
                    key,
                    file.ProjectName ?? string.Empty,
                    file.ProjectPath ?? string.Empty,
                    string.IsNullOrEmpty(file.Language) ? CppFileTypes.GetLanguage(key) : file.Language);

                IndexedFile? indexed = IndexFile(info, cancellationToken);
                if (indexed == null)
                {
                    // Unreadable or filtered file: keep the previous items instead of dropping content.
                    return;
                }

                NavigationItem[] snapshot;
                int itemCount;
                lock (_sync)
                {
                    if (!_files.ContainsKey(key) || _disposed || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    _files[key] = indexed;
                    snapshot = RebuildSnapshotLocked(out int fileCount, out itemCount);
                }

                SetProgress(new IndexProgress(FileCount, FileCount, itemCount, null), false);
                RaiseItemsChanged(key, indexed.Items, false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled incremental update is a no-op.
            }
            catch (Exception exception)
            {
                _log.Error("Ctrl+T Navigator: incremental index update failed.", exception);
            }
            finally
            {
                if (acquired)
                {
                    _operationGate.Release();
                }
            }
        }

        public void RemoveFile(string filePath)
        {
            if (_disposed || string.IsNullOrEmpty(filePath))
            {
                return;
            }

            string key = NormalizeKey(filePath);
            bool removed;
            int itemCount;
            lock (_sync)
            {
                removed = _files.Remove(key);
                if (removed)
                {
                    RebuildSnapshotLocked(out int fileCount, out itemCount);
                }
                else
                {
                    itemCount = ItemCount;
                }
            }

            if (removed)
            {
                SetProgress(new IndexProgress(FileCount, FileCount, itemCount, null), false);
                RaiseItemsChanged(filePath, EmptyItems, false);
            }
        }

        public void Clear()
        {
            if (_disposed)
            {
                return;
            }

            lock (_sync)
            {
                _files = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
                RebuildSnapshotLocked(out int fileCount, out int itemCount);
            }

            SetState(IndexState.NotStarted);
            SetProgress(IndexProgress.Empty, false);
            RaiseProgress();
            RaiseItemsChanged(null, EmptyItems, true);
        }

        public IReadOnlyList<NavigationItem> Snapshot()
        {
            return Volatile.Read(ref _snapshot);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_sync)
            {
                _files.Clear();
                Volatile.Write(ref _snapshot, EmptyItems);
                Volatile.Write(ref _fileCount, 0);
                Volatile.Write(ref _itemCount, 0);
                _progress = IndexProgress.Empty;
            }

            Volatile.Write(ref _state, (int)IndexState.NotStarted);
            ProgressChanged = null;
            ItemsChanged = null;
        }

        // ---------------------------------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------------------------------

        private static bool ContainsCancellation(AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (inner is OperationCanceledException)
                {
                    return true;
                }
            }

            return false;
        }

        private SourceFileInfo[] PrepareFiles(IReadOnlyCollection<SourceFileInfo>? files)
        {
            if (files == null || files.Count == 0)
            {
                return EmptyFiles;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<SourceFileInfo>(files.Count);
            foreach (SourceFileInfo? file in files)
            {
                if (file == null || string.IsNullOrEmpty(file.FilePath))
                {
                    continue;
                }

                string path;
                try
                {
                    path = Path.GetFullPath(file.FilePath);
                }
                catch
                {
                    continue;
                }

                if (!SourceFileScanner.IsSupportedFile(path, _extensions, _excludePatterns))
                {
                    continue;
                }

                if (!seen.Add(path))
                {
                    continue;
                }

                result.Add(new SourceFileInfo(
                    path,
                    file.ProjectName ?? string.Empty,
                    file.ProjectPath ?? string.Empty,
                    string.IsNullOrEmpty(file.Language) ? CppFileTypes.GetLanguage(path) : file.Language));
            }

            return result.ToArray();
        }

        private IndexedFile? IndexFile(SourceFileInfo info, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(info.FilePath);
                if (!fileInfo.Exists)
                {
                    _log.Warn("Ctrl+T Navigator: skipping missing file " + info.FilePath);
                    return null;
                }

                long length = fileInfo.Length;
                DateTime lastWriteUtc = fileInfo.LastWriteTimeUtc;
                if (_options.MaxFileSizeKb > 0 && length > (long)_options.MaxFileSizeKb * 1024L)
                {
                    _log.Info(
                        "Ctrl+T Navigator: skipping " + info.FilePath + " (" + length + " bytes; limit " +
                        _options.MaxFileSizeKb + " KB).");
                    return null;
                }

                string content;
                try
                {
                    content = File.ReadAllText(info.FilePath);
                }
                catch (IOException exception)
                {
                    _log.Warn("Ctrl+T Navigator: cannot read " + info.FilePath + ": " + exception.Message);
                    return null;
                }
                catch (UnauthorizedAccessException exception)
                {
                    _log.Warn("Ctrl+T Navigator: cannot read " + info.FilePath + ": " + exception.Message);
                    return null;
                }

                if (LooksBinary(content))
                {
                    _log.Info("Ctrl+T Navigator: skipping binary file " + info.FilePath);
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<NavigationItem> symbols = _extractor.Extract(
                    info.FilePath,
                    content,
                    info.ProjectName,
                    info.ProjectPath,
                    info.Language);

                var items = new NavigationItem[symbols.Count + 1];
                items[0] = CreateFileItem(info);
                for (int index = 0; index < symbols.Count; index++)
                {
                    items[index + 1] = symbols[index];
                }

                return new IndexedFile
                {
                    Info = info,
                    Items = items,
                    LastWriteUtc = lastWriteUtc,
                    Length = length,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log.Warn("Ctrl+T Navigator: failed to index " + info.FilePath + ": " + exception.Message);
                return null;
            }
        }

        private static bool LooksBinary(string content)
        {
            int limit = Math.Min(content.Length, 1024);
            for (int index = 0; index < limit; index++)
            {
                if (content[index] == '\0')
                {
                    return true;
                }
            }

            return false;
        }

        private static NavigationItem CreateFileItem(SourceFileInfo info)
        {
            string fileName;
            try
            {
                fileName = Path.GetFileName(info.FilePath);
            }
            catch (ArgumentException)
            {
                fileName = info.FilePath;
            }

            ItemFlags flags = ItemFlags.Public;
            if (CppFileTypes.IsHeaderPath(info.FilePath))
            {
                flags |= ItemFlags.HeaderFile;
            }

            return new NavigationItem(fileName, ItemKind.File, info.FilePath, 1, 1)
            {
                FullName = info.FilePath,
                ProjectName = info.ProjectName,
                ProjectPath = info.ProjectPath,
                Language = info.Language,
                Flags = flags,
            };
        }

        private NavigationItem[] RebuildSnapshotLocked(out int fileCount, out int itemCount)
        {
            fileCount = _files.Count;
            int total = 0;
            foreach (IndexedFile file in _files.Values)
            {
                total += file.Items.Length;
            }

            var snapshot = new NavigationItem[total];
            if (fileCount > 0)
            {
                var keys = new List<string>(_files.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                int offset = 0;
                for (int index = 0; index < keys.Count; index++)
                {
                    NavigationItem[] items = _files[keys[index]].Items;
                    Array.Copy(items, 0, snapshot, offset, items.Length);
                    offset += items.Length;
                }
            }

            Volatile.Write(ref _snapshot, snapshot);
            itemCount = total;
            Volatile.Write(ref _fileCount, fileCount);
            Volatile.Write(ref _itemCount, total);
            return snapshot;
        }

        private static string NormalizeKey(string filePath)
        {
            try
            {
                return Path.GetFullPath(filePath);
            }
            catch
            {
                return filePath;
            }
        }

        private void SetState(IndexState state)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _state, (int)state);
        }

        private void SetProgress(IndexProgress progress, bool raiseEvent)
        {
            lock (_sync)
            {
                _progress = progress;
            }

            if (raiseEvent)
            {
                RaiseProgress();
            }
        }

        private void RaiseProgress()
        {
            EventHandler<IndexProgressEventArgs>? handler = ProgressChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, new IndexProgressEventArgs(Progress));
            }
            catch (Exception exception)
            {
                _log.Error("Ctrl+T Navigator: ProgressChanged handler failed.", exception);
            }
        }

        private void RaiseItemsChanged(string? filePath, IReadOnlyList<NavigationItem> items, bool isFullRebuild)
        {
            EventHandler<IndexItemsChangedEventArgs>? handler = ItemsChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, new IndexItemsChangedEventArgs(filePath, items, isFullRebuild));
            }
            catch (Exception exception)
            {
                _log.Error("Ctrl+T Navigator: ItemsChanged handler failed.", exception);
            }
        }
    }
}

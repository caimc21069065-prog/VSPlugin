using System;

namespace CtrlTNav.Core.Indexing
{
    /// <summary>Lifecycle state of the background index.</summary>
    public enum IndexState
    {
        NotStarted = 0,
        Building = 1,
        Ready = 2,
        Failed = 3,
    }

    /// <summary>A snapshot of indexing progress, safe to pass across threads.</summary>
    public readonly struct IndexProgress
    {
        public IndexProgress(int filesTotal, int filesProcessed, int itemCount, string? currentFile)
        {
            FilesTotal = filesTotal;
            FilesProcessed = filesProcessed;
            ItemCount = itemCount;
            CurrentFile = currentFile;
        }

        public int FilesTotal { get; }

        public int FilesProcessed { get; }

        public int ItemCount { get; }

        public string? CurrentFile { get; }

        public bool IsComplete
        {
            get { return FilesTotal > 0 && FilesProcessed >= FilesTotal; }
        }

        public static readonly IndexProgress Empty = new IndexProgress(0, 0, 0, null);

        public override string ToString()
        {
            return FilesProcessed + "/" + FilesTotal + " files, " + ItemCount + " items";
        }
    }

    public sealed class IndexProgressEventArgs : EventArgs
    {
        public IndexProgressEventArgs(IndexProgress progress)
        {
            Progress = progress;
        }

        public IndexProgress Progress { get; }
    }

    /// <summary>Raised after the set of indexed items changed.</summary>
    public sealed class IndexItemsChangedEventArgs : EventArgs
    {
        public IndexItemsChangedEventArgs(string? filePath, System.Collections.Generic.IReadOnlyList<Model.NavigationItem> items, bool isFullRebuild)
        {
            FilePath = filePath;
            Items = items;
            IsFullRebuild = isFullRebuild;
        }

        /// <summary>File whose items changed, or null for a full rebuild.</summary>
        public string? FilePath { get; }

        /// <summary>New items for <see cref="FilePath"/> (or the full snapshot when <see cref="IsFullRebuild"/>).</summary>
        public System.Collections.Generic.IReadOnlyList<Model.NavigationItem> Items { get; }

        public bool IsFullRebuild { get; }
    }
}

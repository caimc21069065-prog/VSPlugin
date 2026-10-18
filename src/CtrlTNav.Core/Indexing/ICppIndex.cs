using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Core.Indexing
{
    /// <summary>
    /// Background C/C++ symbol index. Implemented by <c>CtrlTNav.Indexing</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="BuildAsync"/> performs a full rebuild; <see cref="UpdateFileAsync"/> re-parses a single file.
    /// Events are raised on background threads. <see cref="Snapshot"/> returns a consistent point-in-time copy
    /// and may be called from any thread.
    /// </remarks>
    public interface ICppIndex : IDisposable
    {
        int FileCount { get; }

        int ItemCount { get; }

        IndexState State { get; }

        IndexProgress Progress { get; }

        event EventHandler<IndexProgressEventArgs>? ProgressChanged;

        /// <summary>Raised whenever the content of the index changes.</summary>
        event EventHandler<IndexItemsChangedEventArgs>? ItemsChanged;

        /// <summary>Full rebuild over the supplied files. Replaces all previous content.</summary>
        Task BuildAsync(IReadOnlyCollection<SourceFileInfo> files, CancellationToken cancellationToken = default);

        /// <summary>Re-parses a single file and replaces its items. No-op when the file is not tracked.</summary>
        Task UpdateFileAsync(SourceFileInfo file, CancellationToken cancellationToken = default);

        /// <summary>Removes a file and all items that came from it.</summary>
        void RemoveFile(string filePath);

        /// <summary>Drops all indexed content.</summary>
        void Clear();

        /// <summary>Consistent snapshot of every item currently indexed.</summary>
        IReadOnlyList<NavigationItem> Snapshot();
    }
}

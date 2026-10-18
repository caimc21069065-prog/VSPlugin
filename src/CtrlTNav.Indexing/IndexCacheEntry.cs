using System;
using System.Collections.Generic;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Indexing
{
    /// <summary>
    /// One file's cached symbols as stored in the versioned binary index cache.
    /// </summary>
    public sealed class IndexCacheEntry
    {
        /// <summary>Absolute path of the indexed file.</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Last write time of the file when it was indexed (UTC).</summary>
        public DateTime LastWriteUtc { get; set; }

        /// <summary>File size in bytes when it was indexed.</summary>
        public long Length { get; set; }

        /// <summary>Items that were produced for the file (including its file pseudo item).</summary>
        public IReadOnlyList<NavigationItem> Items { get; set; } = new NavigationItem[0];
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Indexing
{
    /// <summary>
    /// Versioned binary persistence for the symbol index. Loading is completely tolerant: a missing,
    /// truncated, corrupted or foreign file returns <c>null</c> instead of throwing, so a bad cache can
    /// never break Visual Studio startup (NF6).
    /// </summary>
    /// <remarks>
    /// The format starts with a magic marker and a format version. Entries that are stale (file size or
    /// last write time changed) are reported by <see cref="IsUpToDate"/> so the caller can skip re-parsing
    /// unchanged files.
    /// </remarks>
    public sealed class IndexCache
    {
        internal const int CurrentFormatVersion = 1;

        private static readonly byte[] Magic = { 0x43, 0x54, 0x4E, 0x49, 0x44, 0x58, 0x31, 0x00 }; // "CTNIDX1\0"

        private const int MaxStringBytes = 64 * 1024;
        private const int MaxEntries = 1000000;
        private const int MaxItemsPerEntry = 2000000;

        private readonly List<IndexCacheEntry> _entries;

        /// <summary>Creates an empty cache that can be filled and saved (for a first-time build).</summary>
        public IndexCache()
            : this(null, new List<IndexCacheEntry>())
        {
        }

        private IndexCache(string? solutionPath, List<IndexCacheEntry> entries)
        {
            SolutionPath = solutionPath;
            _entries = entries;
        }

        /// <summary>Solution path that was stored with the cache, or null.</summary>
        public string? SolutionPath { get; private set; }

        /// <summary>Entries loaded from (or saved to) the cache.</summary>
        public IReadOnlyList<IndexCacheEntry> Entries
        {
            get { return _entries; }
        }

        /// <summary>
        /// Loads a cache file. Returns <c>null</c> when the file is missing, unreadable, corrupted, has a
        /// different format version, or contains no entries.
        /// </summary>
        public static IndexCache? Load(string? cacheFilePath)
        {
            if (string.IsNullOrEmpty(cacheFilePath))
            {
                return null;
            }

            try
            {
                if (!File.Exists(cacheFilePath))
                {
                    return null;
                }

                using (var stream = new FileStream(cacheFilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    byte[] magic = reader.ReadBytes(Magic.Length);
                    if (magic.Length != Magic.Length)
                    {
                        return null;
                    }

                    for (int index = 0; index < Magic.Length; index++)
                    {
                        if (magic[index] != Magic[index])
                        {
                            return null;
                        }
                    }

                    int version = reader.ReadInt32();
                    if (version != CurrentFormatVersion)
                    {
                        return null;
                    }

                    string? solutionPath = ReadString(reader, MaxStringBytes);
                    int entryCount = reader.ReadInt32();
                    if (entryCount < 0 || entryCount > MaxEntries)
                    {
                        return null;
                    }

                    var entries = new List<IndexCacheEntry>(Math.Min(entryCount, 4096));
                    for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
                    {
                        var entry = new IndexCacheEntry
                        {
                            FilePath = ReadString(reader, MaxStringBytes) ?? string.Empty,
                            LastWriteUtc = ReadDateTime(reader),
                            Length = reader.ReadInt64(),
                        };

                        int itemCount = reader.ReadInt32();
                        if (itemCount < 0 || itemCount > MaxItemsPerEntry)
                        {
                            return null;
                        }

                        var items = new List<NavigationItem>(Math.Min(itemCount, 4096));
                        for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
                        {
                            items.Add(ReadItem(reader, entry.FilePath, entry.LastWriteUtc, entry.Length));
                        }

                        entry.Items = items;
                        entries.Add(entry);
                    }

                    return new IndexCache(solutionPath, entries);
                }
            }
            catch
            {
                // Corrupted caches are treated as absent.
                return null;
            }
        }

        /// <summary>
        /// Saves the entries. The write goes to a temporary file that is then moved over the target, so a
        /// crash cannot leave a half-written cache. IO errors are swallowed (the cache is an optimization).
        /// </summary>
        public void Save(string cacheFilePath, string solutionPath, IEnumerable<IndexCacheEntry> entries)
        {
            if (string.IsNullOrEmpty(cacheFilePath) || entries == null)
            {
                return;
            }

            string? temporaryPath = null;
            try
            {
                string? directory = Path.GetDirectoryName(cacheFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                temporaryPath = cacheFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var materialized = new List<IndexCacheEntry>();
                foreach (IndexCacheEntry? entry in entries)
                {
                    if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
                    {
                        materialized.Add(entry);
                    }
                }

                using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Magic);
                    writer.Write(CurrentFormatVersion);
                    writer.Write(solutionPath ?? string.Empty);
                    writer.Write(materialized.Count);
                    for (int entryIndex = 0; entryIndex < materialized.Count; entryIndex++)
                    {
                        IndexCacheEntry entry = materialized[entryIndex];
                        writer.Write(entry.FilePath);
                        writer.Write(entry.LastWriteUtc.Ticks);
                        writer.Write(entry.Length);

                        IReadOnlyList<NavigationItem>? items = entry.Items;
                        int itemCount = items == null ? 0 : items.Count;
                        writer.Write(itemCount);
                        for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
                        {
                            WriteItem(writer, items![itemIndex]);
                        }
                    }
                }

                if (File.Exists(cacheFilePath))
                {
                    try
                    {
                        File.Replace(temporaryPath, cacheFilePath, null);
                    }
                    catch (IOException)
                    {
                        File.Delete(cacheFilePath);
                        File.Move(temporaryPath, cacheFilePath);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(cacheFilePath);
                        File.Move(temporaryPath, cacheFilePath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, cacheFilePath);
                }

                temporaryPath = null;
            }
            catch
            {
                // Best effort only.
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }

        /// <summary>
        /// True when the file described by <paramref name="entry"/> still has the size and last write time
        /// that were recorded when it was indexed (so its cached symbols can be reused).
        /// </summary>
        public static bool IsUpToDate(IndexCacheEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
            {
                return false;
            }

            try
            {
                var fileInfo = new FileInfo(entry.FilePath);
                if (!fileInfo.Exists)
                {
                    return false;
                }

                return fileInfo.Length == entry.Length &&
                       fileInfo.LastWriteTimeUtc.Ticks == entry.LastWriteUtc.Ticks;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteItem(BinaryWriter writer, NavigationItem item)
        {
            writer.Write(item.Name ?? string.Empty);
            writer.Write(item.FullName ?? string.Empty);
            writer.Write((int)item.Kind);
            writer.Write(item.Line);
            writer.Write(item.Column);
            writer.Write(item.ProjectName ?? string.Empty);
            writer.Write(item.ProjectPath ?? string.Empty);
            writer.Write((int)item.Flags);
            writer.Write(item.ContainerName ?? string.Empty);
            writer.Write(item.Signature ?? string.Empty);
            writer.Write(item.Language ?? "cpp");
        }

        private static NavigationItem ReadItem(BinaryReader reader, string filePath, DateTime lastWriteUtc, long length)
        {
            string name = ReadString(reader, MaxStringBytes) ?? string.Empty;
            string fullName = ReadString(reader, MaxStringBytes) ?? string.Empty;
            int kindValue = reader.ReadInt32();
            int line = reader.ReadInt32();
            int column = reader.ReadInt32();
            string projectName = ReadString(reader, MaxStringBytes) ?? string.Empty;
            string projectPath = ReadString(reader, MaxStringBytes) ?? string.Empty;
            int flagsValue = reader.ReadInt32();
            string containerName = ReadString(reader, MaxStringBytes) ?? string.Empty;
            string signature = ReadString(reader, MaxStringBytes) ?? string.Empty;
            string language = ReadString(reader, MaxStringBytes) ?? "cpp";

            ItemKind kind = Enum.IsDefined(typeof(ItemKind), kindValue) ? (ItemKind)kindValue : ItemKind.Unknown;
            var item = new NavigationItem(name, kind, filePath, line < 0 ? 0 : line, column < 0 ? 0 : column)
            {
                FullName = fullName,
                ProjectName = projectName,
                ProjectPath = projectPath,
                Flags = (ItemFlags)flagsValue,
                ContainerName = containerName,
                Signature = signature,
                Language = language,
            };
            return item;
        }

        private static DateTime ReadDateTime(BinaryReader reader)
        {
            long ticks = reader.ReadInt64();
            if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            {
                return DateTime.MinValue;
            }

            return new DateTime(ticks, DateTimeKind.Utc);
        }

        /// <summary>Reads a length-prefixed UTF-8 string with a defensive upper bound.</summary>
        private static string? ReadString(BinaryReader reader, int maxBytes)
        {
            int byteCount = Read7BitEncodedInt(reader);
            if (byteCount < 0)
            {
                return null;
            }

            if (byteCount > maxBytes)
            {
                throw new InvalidDataException("Index cache string is too long.");
            }

            if (byteCount == 0)
            {
                return string.Empty;
            }

            byte[] bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount)
            {
                throw new EndOfStreamException("Index cache is truncated.");
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static int Read7BitEncodedInt(BinaryReader reader)
        {
            int result = 0;
            int shift = 0;
            while (shift <= 28)
            {
                byte value = reader.ReadByte();
                result |= (value & 0x7F) << shift;
                if ((value & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }

            throw new InvalidDataException("Bad index cache string length.");
        }
    }
}

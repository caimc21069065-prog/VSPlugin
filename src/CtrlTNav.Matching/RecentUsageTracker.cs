using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CtrlTNav.Core;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;

namespace CtrlTNav.Matching
{
    /// <summary>
    /// Persistent recent-usage store (MRU list) that turns how recently and how often an item was navigated
    /// to into a 0..1000 ranking strength for <see cref="SearchIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>The tracker keeps at most <c>capacity</c> entries (oldest evicted first) and persists them to a
    /// small text file when <see cref="Save"/> is called. <see cref="Load"/> is tolerant: a missing file yields
    /// an empty store, corrupt files/lines are ignored, and no IO exception ever escapes.</para>
    /// <para>Boost formula (0..1000): a recency component that decays with the entry's position in the MRU
    /// list (newest gets 700) plus a frequency component of 60 points per navigation, capped at 300.</para>
    /// <para>Thread safety: <see cref="RecordUsage"/>, <see cref="Clear"/>, <see cref="Load"/> and
    /// <see cref="Save"/> may be called from any thread; <see cref="GetBoost"/>, <see cref="IsEmpty"/> and
    /// <see cref="GetRecentFilePaths"/> are lock-free and read an immutable snapshot, which keeps them safe
    /// (and cheap) to call from the search hot path.</para>
    /// </remarks>
    public sealed class RecentUsageTracker : IRecentUsageStore, IRecentUsagePersistence
    {
        private const int MaxStrength = 1000;
        private const int RecencyRange = 700;
        private const int FrequencyRange = 300;
        private const int FrequencyPerUse = 60;
        private const string Header = "CtrlTNav.RecentUsage.v1";

        private static readonly UsageEntry[] NoEntries = new UsageEntry[0];

        private static readonly IReadOnlyList<string> NoPaths = new string[0];

        private static readonly Comparison<UsageEntry> NewestFirst =
            (a, b) => b.Sequence.CompareTo(a.Sequence);

        private readonly object _gate = new object();
        private readonly Dictionary<string, UsageEntry> _entries = new Dictionary<string, UsageEntry>(StringComparer.Ordinal);
        private readonly string _storageFilePath;
        private readonly int _capacity;
        private readonly ILog _log;

        private long _sequence;
        private volatile Snapshot _snapshot = new Snapshot(new Dictionary<string, int>(StringComparer.Ordinal), NoEntries);

        /// <summary>
        /// Creates a tracker. <paramref name="storageFilePath"/> defaults to
        /// <see cref="AppPaths.GetRecentUsageFilePath"/>; tests pass a temporary path. Call
        /// <see cref="Load"/> to restore previously saved data and <see cref="Save"/> to persist it.
        /// </summary>
        public RecentUsageTracker(string? storageFilePath = null, int capacity = 300, ILog? log = null)
        {
            _log = log ?? NullLog.Instance;
            _capacity = capacity < 1 ? 1 : capacity;

            if (storageFilePath != null && storageFilePath.Length > 0)
            {
                _storageFilePath = storageFilePath;
            }
            else
            {
                try
                {
                    _storageFilePath = AppPaths.GetRecentUsageFilePath();
                }
                catch (Exception exception)
                {
                    _log.Warn("Could not resolve the recent-usage storage path; falling back to the temp directory: " + exception.Message);
                    _storageFilePath = Path.Combine(Path.GetTempPath(), "ctrltnav-recent-usage.dat");
                }
            }
        }

        /// <summary>Path of the file used by <see cref="Load"/> and <see cref="Save"/>.</summary>
        public string StorageFilePath
        {
            get { return _storageFilePath; }
        }

        /// <summary>Maximum number of remembered entries.</summary>
        public int Capacity
        {
            get { return _capacity; }
        }

        /// <inheritdoc />
        public bool IsEmpty
        {
            get { return _snapshot.Strengths.Count == 0; }
        }

        /// <summary>Number of entries currently held in memory.</summary>
        public int EntryCount
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        // -------------------------------------------------------------------------------------------
        // Recording / lookup.
        // -------------------------------------------------------------------------------------------

        /// <inheritdoc />
        public void RecordUsage(NavigationItem item)
        {
            if (item == null)
            {
                return;
            }

            try
            {
                string identity = item.GetIdentity();
                lock (_gate)
                {
                    if (!_entries.TryGetValue(identity, out UsageEntry? entry))
                    {
                        entry = new UsageEntry
                        {
                            Identity = identity,
                            Name = item.Name ?? string.Empty,
                            Kind = item.Kind,
                            FilePath = item.FilePath ?? string.Empty,
                            Line = item.Line,
                            Column = item.Column,
                            Count = 1,
                            Sequence = ++_sequence,
                            LastUsedUtc = DateTime.UtcNow,
                        };
                        _entries[identity] = entry;
                    }
                    else
                    {
                        entry.Count = entry.Count == int.MaxValue ? int.MaxValue : entry.Count + 1;
                        entry.Sequence = ++_sequence;
                        entry.LastUsedUtc = DateTime.UtcNow;
                        entry.Name = item.Name ?? string.Empty;
                        entry.Kind = item.Kind;
                        entry.FilePath = item.FilePath ?? string.Empty;
                        entry.Line = item.Line;
                        entry.Column = item.Column;
                    }

                    TrimOverflowLocked();
                    _snapshot = BuildSnapshotLocked();
                }
            }
            catch (Exception exception)
            {
                _log.Warn("RecentUsageTracker.RecordUsage failed: " + exception.Message);
            }
        }

        /// <inheritdoc />
        /// <remarks>Returns 0 for items that were never recorded. The value is always in the 0..1000 range.</remarks>
        public int GetBoost(NavigationItem item)
        {
            if (item == null)
            {
                return 0;
            }

            return GetStrength(item.GetIdentity());
        }

        /// <summary>
        /// Fast identity-based strength lookup used by <see cref="SearchIndex"/> for already indexed items.
        /// Returns 0 when the identity was never recorded.
        /// </summary>
        internal int GetStrength(string identity)
        {
            if (string.IsNullOrEmpty(identity))
            {
                return 0;
            }

            Snapshot snapshot = _snapshot;
            return snapshot.Strengths.TryGetValue(identity, out int strength) ? strength : 0;
        }

        /// <summary>Identities of the remembered items, newest first. Used by the empty-query search path.</summary>
        internal string[] GetRecentIdentities()
        {
            UsageEntry[] ordered = _snapshot.Ordered;
            if (ordered.Length == 0)
            {
                return new string[0];
            }

            var identities = new string[ordered.Length];
            for (int i = 0; i < ordered.Length; i++)
            {
                identities[i] = ordered[i].Identity;
            }

            return identities;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetRecentFilePaths(int maxCount)
        {
            if (maxCount <= 0)
            {
                return NoPaths;
            }

            UsageEntry[] ordered = _snapshot.Ordered;
            if (ordered.Length == 0)
            {
                return NoPaths;
            }

            var paths = new List<string>(maxCount < ordered.Length ? maxCount : ordered.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ordered.Length && paths.Count < maxCount; i++)
            {
                string path = ordered[i].FilePath;
                if (path.Length == 0)
                {
                    continue;
                }

                if (seen.Add(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        /// <inheritdoc />
        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _sequence = 0;
                _snapshot = new Snapshot(new Dictionary<string, int>(StringComparer.Ordinal), NoEntries);
            }
        }

        // -------------------------------------------------------------------------------------------
        // Persistence.
        // -------------------------------------------------------------------------------------------

        /// <inheritdoc />
        public void Load()
        {
            try
            {
                var loaded = new Dictionary<string, UsageEntry>(StringComparer.Ordinal);
                long maxSequence = 0;

                if (File.Exists(_storageFilePath))
                {
                    bool headerSeen = false;
                    bool nonEmptyFile = false;
                    using (var reader = new StreamReader(_storageFilePath, Encoding.UTF8, true))
                    {
                        string? line = reader.ReadLine();
                        if (line != null && line.Trim() == Header)
                        {
                            headerSeen = true;
                            nonEmptyFile = true;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Length == 0)
                                {
                                    continue;
                                }

                                UsageEntry? entry = TryParseEntry(line);
                                if (entry == null)
                                {
                                    continue; // tolerate corrupt individual entries
                                }

                                loaded[entry.Identity] = entry;
                                if (entry.Sequence > maxSequence)
                                {
                                    maxSequence = entry.Sequence;
                                }
                            }
                        }
                        else if (line != null && line.Trim().Length > 0)
                        {
                            nonEmptyFile = true;
                        }
                    }

                    if (nonEmptyFile && !headerSeen)
                    {
                        _log.Warn("Recent-usage file has an unrecognized format and was ignored: " + _storageFilePath);
                    }
                }

                lock (_gate)
                {
                    _entries.Clear();
                    foreach (KeyValuePair<string, UsageEntry> pair in loaded)
                    {
                        _entries[pair.Key] = pair.Value;
                    }

                    _sequence = maxSequence;
                    TrimOverflowLocked();
                    _snapshot = BuildSnapshotLocked();
                }
            }
            catch (Exception exception)
            {
                _log.Warn("RecentUsageTracker.Load failed; starting with an empty recent list: " + exception.Message);
                lock (_gate)
                {
                    _entries.Clear();
                    _sequence = 0;
                    _snapshot = new Snapshot(new Dictionary<string, int>(StringComparer.Ordinal), NoEntries);
                }
            }
        }

        /// <inheritdoc />
        public void Save()
        {
            string tempPath = _storageFilePath + ".tmp";
            try
            {
                UsageEntry[] ordered;
                lock (_gate)
                {
                    ordered = BuildOrderedEntriesLocked();
                }

                string? directory = Path.GetDirectoryName(_storageFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                using (var writer = new StreamWriter(tempPath, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine(Header);
                    for (int i = 0; i < ordered.Length; i++)
                    {
                        writer.WriteLine(FormatEntry(ordered[i]));
                    }
                }

                ReplaceFileAtomic(tempPath, _storageFilePath);
            }
            catch (Exception exception)
            {
                _log.Warn("RecentUsageTracker.Save failed: " + exception.Message);
                TryDelete(tempPath);
            }
        }

        private static void ReplaceFileAtomic(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(sourcePath, destinationPath, null);
                    return;
                }
                catch (Exception)
                {
                    // Some file systems (or a file opened without FileShare.Delete) reject File.Replace;
                    // fall back to a best-effort delete+move below.
                    TryDelete(destinationPath);
                }
            }

            File.Move(sourcePath, destinationPath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // best effort only
            }
        }

        // -------------------------------------------------------------------------------------------
        // Snapshot building.
        // -------------------------------------------------------------------------------------------

        private void TrimOverflowLocked()
        {
            while (_entries.Count > _capacity)
            {
                UsageEntry? oldest = null;
                foreach (UsageEntry candidate in _entries.Values)
                {
                    if (oldest == null || candidate.Sequence < oldest.Sequence)
                    {
                        oldest = candidate;
                    }
                }

                if (oldest == null)
                {
                    return;
                }

                _entries.Remove(oldest.Identity);
            }
        }

        private UsageEntry[] BuildOrderedEntriesLocked()
        {
            if (_entries.Count == 0)
            {
                return NoEntries;
            }

            var list = new List<UsageEntry>(_entries.Values);
            list.Sort(NewestFirst);
            return list.ToArray();
        }

        private Snapshot BuildSnapshotLocked()
        {
            UsageEntry[] ordered = BuildOrderedEntriesLocked();
            var strengths = new Dictionary<string, int>(ordered.Length, StringComparer.Ordinal);
            for (int i = 0; i < ordered.Length; i++)
            {
                UsageEntry entry = ordered[i];

                int recency = i < _capacity
                    ? (int)(((long)RecencyRange * (_capacity - i)) / _capacity)
                    : 0;
                if (recency < 0)
                {
                    recency = 0;
                }

                long frequency = (long)entry.Count * FrequencyPerUse;
                if (frequency > FrequencyRange)
                {
                    frequency = FrequencyRange;
                }
                else if (frequency < 0)
                {
                    frequency = 0;
                }

                int strength = recency + (int)frequency;
                if (strength > MaxStrength)
                {
                    strength = MaxStrength;
                }
                else if (strength < 0)
                {
                    strength = 0;
                }

                strengths[entry.Identity] = strength;
            }

            return new Snapshot(strengths, ordered);
        }

        // -------------------------------------------------------------------------------------------
        // File format: one header line followed by one tab-separated entry per line. All text fields are
        // escaped, which keeps the format simple, greppable and robust against odd file names/tabs.
        // -------------------------------------------------------------------------------------------

        private static string FormatEntry(UsageEntry entry)
        {
            var builder = new StringBuilder(128);
            builder.Append(entry.Sequence.ToString(CultureInfo.InvariantCulture)).Append('\t');
            builder.Append(entry.Count.ToString(CultureInfo.InvariantCulture)).Append('\t');
            builder.Append(entry.LastUsedUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t');
            builder.Append(((int)entry.Kind).ToString(CultureInfo.InvariantCulture)).Append('\t');
            builder.Append(entry.Line.ToString(CultureInfo.InvariantCulture)).Append('\t');
            builder.Append(entry.Column.ToString(CultureInfo.InvariantCulture)).Append('\t');
            AppendEscaped(builder, entry.Identity).Append('\t');
            AppendEscaped(builder, entry.Name).Append('\t');
            AppendEscaped(builder, entry.FilePath);
            return builder.ToString();
        }

        private static UsageEntry? TryParseEntry(string line)
        {
            var fields = new List<string>(9);
            var current = new StringBuilder(64);
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    i++;
                    char escaped = line[i];
                    switch (escaped)
                    {
                        case 't':
                            current.Append('\t');
                            break;
                        case 'n':
                            current.Append('\n');
                            break;
                        case 'r':
                            current.Append('\r');
                            break;
                        case '\\':
                            current.Append('\\');
                            break;
                        default:
                            current.Append(escaped);
                            break;
                    }
                }
                else if (c == '\t')
                {
                    fields.Add(current.ToString());
                    current.Length = 0;
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString());
            if (fields.Count < 9)
            {
                return null;
            }

            if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sequence))
            {
                return null;
            }

            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            {
                return null;
            }

            if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                return null;
            }

            if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int kind))
            {
                return null;
            }

            if (!int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineNumber))
            {
                return null;
            }

            if (!int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int column))
            {
                return null;
            }

            string identity = fields[6];
            if (identity.Length == 0)
            {
                return null;
            }

            return new UsageEntry
            {
                Sequence = sequence,
                Count = count < 1 ? 1 : count,
                LastUsedUtc = new DateTime(ticks, DateTimeKind.Utc),
                Kind = (ItemKind)kind,
                Line = lineNumber,
                Column = column,
                Identity = identity,
                Name = fields[7],
                FilePath = fields[8],
            };
        }

        private static StringBuilder AppendEscaped(StringBuilder builder, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder;
        }

        // -------------------------------------------------------------------------------------------
        // Nested types.
        // -------------------------------------------------------------------------------------------

        private sealed class UsageEntry
        {
            internal string Identity = string.Empty;
            internal string Name = string.Empty;
            internal ItemKind Kind;
            internal string FilePath = string.Empty;
            internal int Line;
            internal int Column;
            internal int Count;
            internal long Sequence;
            internal DateTime LastUsedUtc;
        }

        private sealed class Snapshot
        {
            internal Snapshot(Dictionary<string, int> strengths, UsageEntry[] ordered)
            {
                Strengths = strengths;
                Ordered = ordered;
            }

            /// <summary>Immutable identity -&gt; strength map (0..1000).</summary>
            internal Dictionary<string, int> Strengths { get; }

            /// <summary>Entries sorted by recency, newest first.</summary>
            internal UsageEntry[] Ordered { get; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;

namespace CtrlTNav.UI.Internal
{
    /// <summary>
    /// Pre-computed, WPF-independent view model for one result row. Building this list is the only work done
    /// when results arrive; the (virtualized) row visuals bind to these instances.
    /// </summary>
    internal sealed class ResultRowData
    {
        internal ResultRowData(SearchResult result, ThemePalette palette, SearchWindowOptions options)
        {
            Result = result;
            Palette = palette;
            Item = result.Item;
            Name = result.Item.Name ?? string.Empty;
            BadgeText = result.Item.Kind.GetShortCode();
            KindDisplayName = result.Item.Kind.GetDisplayName();
            Location = LocationFormatter.Format(result.Item, options);
            Highlights = NormalizeRanges(result.MatchedRanges, Name.Length);
            ToolTipText = LocationFormatter.FormatToolTip(result.Item, KindDisplayName);
        }

        public SearchResult Result { get; }

        public NavigationItem Item { get; }

        public ThemePalette Palette { get; }

        public string Name { get; }

        public string BadgeText { get; }

        public string KindDisplayName { get; }

        /// <summary>Right-aligned secondary text: relative path and optional <c>:line</c>.</summary>
        public string Location { get; }

        /// <summary>Merge-sorted, clamped character ranges to emphasise in <see cref="Name"/>.</summary>
        public IReadOnlyList<TextRange> Highlights { get; }

        public string ToolTipText { get; }

        private static IReadOnlyList<TextRange> NormalizeRanges(IReadOnlyList<TextRange>? ranges, int nameLength)
        {
            if (ranges == null || ranges.Count == 0 || nameLength <= 0)
            {
                return EmptyRanges;
            }

            var list = new List<TextRange>(ranges.Count);
            for (int i = 0; i < ranges.Count; i++)
            {
                TextRange range = ranges[i];
                int start = range.Start;
                int length = range.Length;
                if (start < 0)
                {
                    length += start;
                    start = 0;
                }

                if (start >= nameLength || length <= 0)
                {
                    continue;
                }

                if (start + length > nameLength)
                {
                    length = nameLength - start;
                }

                list.Add(new TextRange(start, length));
            }

            if (list.Count <= 1)
            {
                return list;
            }

            list.Sort(CompareRanges);

            // Merge overlapping/adjacent runs so the row paints one continuous emphasis per cluster.
            var merged = new List<TextRange>(list.Count);
            TextRange current = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                TextRange next = list[i];
                if (next.Start <= current.End)
                {
                    int end = Math.Max(current.End, next.End);
                    current = new TextRange(current.Start, end - current.Start);
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
            return merged;
        }

        private static int CompareRanges(TextRange a, TextRange b)
        {
            return a.Start.CompareTo(b.Start);
        }

        private static readonly TextRange[] EmptyRanges = new TextRange[0];
    }

    /// <summary>Formats the location column and tooltips for result rows.</summary>
    internal static class LocationFormatter
    {
        private const int MaxLocationLength = 58;

        public static string Format(NavigationItem item, SearchWindowOptions options)
        {
            if (item == null)
            {
                return string.Empty;
            }

            string path = item.FilePath ?? string.Empty;
            try
            {
                path = path.Replace('/', '\\');
                if (options.ShowRelativePaths && !string.IsNullOrEmpty(options.RelativePathBaseDirectory))
                {
                    path = MakeRelative(path, options.RelativePathBaseDirectory!);
                }

                path = Compact(path, MaxLocationLength);
                if (options.ShowLineNumbers && item.Line > 0)
                {
                    path = path + ":" + item.Line.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                // Path formatting must never take the popup down; fall back to the raw value.
                return item.FilePath ?? string.Empty;
            }

            return path;
        }

        public static string FormatToolTip(NavigationItem item, string kindDisplayName)
        {
            var builder = new StringBuilder(160);
            builder.Append(item.Name);
            if (!string.IsNullOrEmpty(item.FullName) && !string.Equals(item.FullName, item.Name, StringComparison.Ordinal))
            {
                builder.Append('\n').Append(item.FullName);
            }

            builder.Append('\n').Append(kindDisplayName);
            if (!string.IsNullOrEmpty(item.ProjectName))
            {
                builder.Append(" in ").Append(item.ProjectName);
            }

            builder.Append('\n').Append(item.FilePath);
            if (item.Line > 0)
            {
                builder.Append(':').Append(item.Line.ToString(CultureInfo.InvariantCulture));
                if (item.Column > 0)
                {
                    builder.Append(':').Append(item.Column.ToString(CultureInfo.InvariantCulture));
                }
            }

            return builder.ToString();
        }

        private static string MakeRelative(string path, string baseDirectory)
        {
            if (path.Length == 0 || baseDirectory.Length == 0)
            {
                return path;
            }

            string fullPath = Path.GetFullPath(path);
            string fullBase = Path.GetFullPath(baseDirectory);
            if (!fullBase.EndsWith("\\", StringComparison.Ordinal))
            {
                fullBase += "\\";
            }

            if (fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(fullBase.Length);
            }

            return fullPath;
        }

        /// <summary>Keeps the tail of long paths so the file name plus a couple of folders stay visible.</summary>
        private static string Compact(string path, int maxLength)
        {
            if (path.Length <= maxLength)
            {
                return path;
            }

            int start = path.Length - maxLength;
            int separator = path.IndexOf('\\', start);
            if (separator >= 0 && separator + 1 < path.Length - 1)
            {
                return "\u2026" + path.Substring(separator);
            }

            return "\u2026" + path.Substring(start);
        }
    }
}

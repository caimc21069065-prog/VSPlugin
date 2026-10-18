using System;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Core.Search
{
    /// <summary>
    /// A parsed search box text. Supports the following filter prefixes:
    /// <list type="bullet">
    /// <item><description><c>t </c> / <c>t:</c> / <c>type:</c> - only types</description></item>
    /// <item><description><c>f </c> / <c>f:</c> / <c>file:</c> - only files</description></item>
    /// <item><description><c>#</c> / <c>s </c> / <c>sym:</c> - only symbols</description></item>
    /// <item><description><c>m </c> / <c>member:</c> - only members</description></item>
    /// </list>
    /// Everything else is free text that is matched against names.
    /// </summary>
    public sealed class SearchQuery
    {
        private static readonly SearchQuery EmptyInstance = new SearchQuery(string.Empty, string.Empty, ItemKindFilter.All, false);

        private SearchQuery(string rawText, string text, ItemKindFilter filter, bool hasFilterPrefix)
        {
            RawText = rawText;
            Text = text;
            Filter = filter;
            HasFilterPrefix = hasFilterPrefix;
        }

        /// <summary>Text as typed by the user.</summary>
        public string RawText { get; }

        /// <summary>Query text with the filter prefix removed.</summary>
        public string Text { get; }

        public ItemKindFilter Filter { get; }

        public bool HasFilterPrefix { get; }

        public bool IsEmpty
        {
            get { return Text.Length == 0; }
        }

        public static SearchQuery Empty
        {
            get { return EmptyInstance; }
        }

        /// <summary>Parses raw search box text into a query. Never returns null.</summary>
        public static SearchQuery Parse(string? raw)
        {
            if (raw == null)
            {
                return EmptyInstance;
            }

            // Do not trim the end before prefix detection: "t " is a type filter, while a final
            // Trim() would turn it into the free-text query "t".
            string leadingTrimmed = raw.TrimStart();
            if (leadingTrimmed.Length == 0)
            {
                return EmptyInstance;
            }

            ItemKindFilter filter;
            string remainder;
            if (TryStripPrefix(leadingTrimmed, out filter, out remainder))
            {
                return new SearchQuery(raw, remainder.Trim(), filter, true);
            }

            return new SearchQuery(raw, raw.Trim(), ItemKindFilter.All, false);
        }

        private static bool TryStripPrefix(string text, out ItemKindFilter filter, out string remainder)
        {
            filter = ItemKindFilter.All;
            remainder = text;

            if (text[0] == '#')
            {
                filter = ItemKindFilter.Symbols;
                remainder = text.Substring(1).TrimStart();
                return true;
            }

            // Longest prefixes first so that "symbol:" wins over "s ".
            string[] longPrefixes = { "member:", "symbol:", "source:", "type:", "file:", "sym:" };
            ItemKindFilter[] longFilters = { ItemKindFilter.Members, ItemKindFilter.Symbols, ItemKindFilter.Files, ItemKindFilter.Types, ItemKindFilter.Files, ItemKindFilter.Symbols };
            for (int i = 0; i < longPrefixes.Length; i++)
            {
                if (StartsWithIgnoreCase(text, longPrefixes[i]))
                {
                    filter = longFilters[i];
                    remainder = text.Substring(longPrefixes[i].Length).TrimStart();
                    return true;
                }
            }

            // One character prefixes require a trailing space or colon.
            if (text.Length >= 2 && (text[1] == ' ' || text[1] == ':'))
            {
                ItemKindFilter single;
                if (TryGetSingleFilter(text[0], out single))
                {
                    filter = single;
                    remainder = text.Substring(2).TrimStart();
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetSingleFilter(char c, out ItemKindFilter filter)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 't':
                    filter = ItemKindFilter.Types;
                    return true;
                case 'f':
                    filter = ItemKindFilter.Files;
                    return true;
                case 's':
                    filter = ItemKindFilter.Symbols;
                    return true;
                case 'm':
                    filter = ItemKindFilter.Members;
                    return true;
                default:
                    filter = ItemKindFilter.All;
                    return false;
            }
        }

        private static bool StartsWithIgnoreCase(string text, string prefix)
        {
            return text.Length >= prefix.Length && string.Compare(text, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        public override string ToString()
        {
            return "SearchQuery(Filter=" + Filter + ", Text=\"" + Text + "\")";
        }
    }
}

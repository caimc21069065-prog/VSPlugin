using CtrlTNav.Core.Model;

namespace CtrlTNav.Matching
{
    /// <summary>
    /// Immutable, search-optimised wrapper around a <see cref="NavigationItem"/>.
    /// </summary>
    /// <remarks>
    /// All per-item data that the matching hot path needs is pre-computed when the item is added to the
    /// <see cref="SearchIndex"/>: the lower-cased name, the token-boundary bit mask, the character presence
    /// mask and the identity string used for de-duplication and MRU lookups. Searches therefore never call
    /// <c>ToLower()</c>, <c>Substring</c> or <c>GetIdentity()</c> per candidate.
    /// </remarks>
    internal sealed class IndexedItem
    {
        internal IndexedItem(NavigationItem item)
        {
            Item = item;
            Name = item.Name ?? string.Empty;
            NameLength = Name.Length;
            LowerName = Name.ToLowerInvariant();
            Identity = item.GetIdentity();
            Kind = item.Kind;
            IsFile = item.Kind == ItemKind.File;
            IsPublicSymbol = !IsFile && item.IsPublicSymbol;
            FilePath = item.FilePath ?? string.Empty;
            ProjectPath = item.ProjectPath ?? string.Empty;
            BoundaryBits = FuzzyMatcher.ComputeBoundaryBits(Name);
            NameMask = FuzzyMatcher.ComputeAsciiMask(LowerName);

            // Secondary text is the qualified name for symbols, or the full path for file entries, so that
            // typing path fragments (e.g. "src main") can still find a file whose simple name does not match.
            string secondary = item.FullName ?? string.Empty;
            if (secondary.Length == 0 && IsFile)
            {
                secondary = FilePath;
            }

            if (secondary.Length > 0)
            {
                SecondaryText = secondary;
                SecondaryLength = secondary.Length;
                SecondaryMask = FuzzyMatcher.ComputeAsciiMask(secondary);
            }
            else
            {
                SecondaryText = null;
                SecondaryLength = 0;
                SecondaryMask = 0UL;
            }
        }

        internal NavigationItem Item { get; }

        /// <summary>Name shown in the result list (and the only text that highlight ranges refer to).</summary>
        internal string Name { get; }

        internal int NameLength { get; }

        internal string LowerName { get; }

        /// <summary>Stable identity (see <see cref="NavigationItem.GetIdentity"/>), computed once.</summary>
        internal string Identity { get; }

        internal ItemKind Kind { get; }

        internal bool IsFile { get; }

        internal bool IsPublicSymbol { get; }

        internal string FilePath { get; }

        internal string ProjectPath { get; }

        /// <summary>Token-boundary bit mask of <see cref="Name"/>; 0 when the name is too long to pre-compute.</summary>
        internal ulong BoundaryBits { get; }

        /// <summary>ASCII character presence mask of <see cref="LowerName"/> (see FuzzyMatcher.ComputeAsciiMask).</summary>
        internal ulong NameMask { get; }

        /// <summary>Qualified name (symbols) or file path (files) used for secondary matching; may be null.</summary>
        internal string? SecondaryText { get; }

        internal int SecondaryLength { get; }

        internal ulong SecondaryMask { get; }

        /// <summary>
        /// Position of this entry inside the index's internal list. Maintained by <see cref="SearchIndex"/> so
        /// that removals are O(1) (swap with the last element).
        /// </summary>
        internal int ListIndex { get; set; }

        public override string ToString()
        {
            return Kind + " " + Name;
        }
    }
}

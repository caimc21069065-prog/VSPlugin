using System;
using System.Collections.Generic;
using CtrlTNav.Core.Model;

namespace CtrlTNav.Matching
{
    /// <summary>
    /// Details of a successful fuzzy match. <see cref="Quality"/> is a within-class tie-breaking score:
    /// it is always in the range 0..299 so it can never outrank a better <see cref="MatchKind"/> when the
    /// default <c>SearchOptions</c> weights are used (the classes are 300 points apart, except Exact which
    /// is 600 above Prefix).
    /// </summary>
    public readonly struct FuzzyMatchResult
    {
        public FuzzyMatchResult(MatchKind kind, int quality)
        {
            Kind = kind;
            Quality = quality;
        }

        /// <summary>Which of the match classes the query fell into.</summary>
        public MatchKind Kind { get; }

        /// <summary>Within-class quality, 0..299, higher is better.</summary>
        public int Quality { get; }

        public bool IsMatch
        {
            get { return Kind != MatchKind.None; }
        }

        public override string ToString()
        {
            return Kind + "(" + Quality + ")";
        }
    }

    /// <summary>
    /// Tolerant, allocation-free fuzzy matcher for C/C++ symbol and file names.
    /// </summary>
    /// <remarks>
    /// The matcher recognises four classes (strongest first):
    /// <list type="number">
    /// <item><description><see cref="MatchKind.Exact"/> - the whole name equals the query, ignoring ASCII case.</description></item>
    /// <item><description><see cref="MatchKind.Prefix"/> - the name starts with the query (for example
    /// <c>getU</c> matches <c>getUserName</c>).</description></item>
    /// <item><description><see cref="MatchKind.Camel"/> - every query character is either the first character of a
    /// word (<c>getUserName</c>, <c>get_user_name</c>, <c>Get.User.Name</c> all match <c>gun</c>) or part of a
    /// contiguous run that starts at a word boundary (<c>getName</c> matches <c>getUserName</c>).</description></item>
    /// <item><description><see cref="MatchKind.Subsequence"/> - all query characters appear in order, but the
    /// camel rule does not hold.</description></item>
    /// </list>
    /// Empty queries and empty candidates never match; the caller handles the "empty query" (browse) state.
    /// The matcher never throws and never allocates on the hot path: callers pass a pre-computed lower-case
    /// candidate (see <see cref="SearchIndex"/>) plus a pre-computed token-boundary bit mask.
    /// </remarks>
    public static class FuzzyMatcher
    {
        /// <summary>Upper bound of the within-class quality score (keeps classes strictly ordered).</summary>
        internal const int MaxQuality = 299;

        /// <summary>Number of leading characters for which token-boundary bits are pre-computed.</summary>
        internal const int MaxBoundaryBitsLength = 64;

        internal static readonly TextRange[] NoRanges = new TextRange[0];

        internal static readonly IReadOnlyList<TextRange> NoRangesList = NoRanges;

        // -------------------------------------------------------------------------------------------
        // Public convenience API (used by tests and by callers that do not maintain pre-computed data).
        // These overloads allocate; the search index uses the *Prepared overloads below instead.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns the best match class of <paramref name="query"/> against <paramref name="candidate"/>,
        /// or <see cref="MatchKind.None"/> when the query does not match.
        /// </summary>
        public static MatchKind Match(string? candidate, string? query)
        {
            return TryMatch(candidate, query, out FuzzyMatchResult result) ? result.Kind : MatchKind.None;
        }

        /// <summary>
        /// Tries to match <paramref name="query"/> against <paramref name="candidate"/>.
        /// Returns false (and a <see cref="MatchKind.None"/> result) for null/empty inputs.
        /// </summary>
        public static bool TryMatch(string? candidate, string? query, out FuzzyMatchResult result)
        {
            result = default(FuzzyMatchResult);
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(query))
            {
                return false;
            }

            string lowerName = candidate!.ToLowerInvariant();
            string lowerQuery = query!.ToLowerInvariant();
            ulong boundaryBits = ComputeBoundaryBits(candidate);
            if (!TryMatchPrepared(candidate, lowerName, boundaryBits, lowerQuery, out MatchKind kind, out int quality))
            {
                return false;
            }

            result = new FuzzyMatchResult(kind, quality);
            return true;
        }

        /// <summary>
        /// Returns the character ranges of <paramref name="candidate"/> that the query matched, relative to
        /// the candidate string (for example <c>gun</c> against <c>getUserName</c> yields three one-character
        /// ranges). Returns an empty list for no match, an empty query or a qualified-name-only hit.
        /// </summary>
        public static IReadOnlyList<TextRange> GetHighlightRanges(string? candidate, string? query)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(query))
            {
                return NoRangesList;
            }

            string lowerName = candidate!.ToLowerInvariant();
            string lowerQuery = query!.ToLowerInvariant();
            ulong boundaryBits = ComputeBoundaryBits(candidate);
            if (!TryMatchPrepared(candidate, lowerName, boundaryBits, lowerQuery, out MatchKind kind, out _))
            {
                return NoRangesList;
            }

            return GetHighlightRangesPrepared(candidate, lowerName, boundaryBits, lowerQuery, kind, null);
        }

        // -------------------------------------------------------------------------------------------
        // Prepared hot-path API: no allocations, all input strings pre-lowercased.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Matches a pre-lowercased query against a pre-lowercased candidate. The boundary bit mask must be
        /// the value returned by <see cref="ComputeBoundaryBits"/> for <paramref name="name"/> (a mask of 0
        /// means "name is longer than <see cref="MaxBoundaryBitsLength"/>"; boundaries are then computed on
        /// the fly from <paramref name="name"/>). Never allocates.
        /// </summary>
        internal static bool TryMatchPrepared(
            string name,
            string lowerName,
            ulong boundaryBits,
            string lowerQuery,
            out MatchKind kind,
            out int quality)
        {
            kind = MatchKind.None;
            quality = 0;

            int queryLength = lowerQuery.Length;
            int nameLength = lowerName.Length;
            if (queryLength == 0 || queryLength > nameLength)
            {
                return false;
            }

            // 1. Exact (case-insensitive; both strings are already folded to lower case).
            if (queryLength == nameLength && string.Equals(lowerName, lowerQuery, StringComparison.Ordinal))
            {
                kind = MatchKind.Exact;
                return true;
            }

            // 2. Prefix.
            if (StartsWithOrdinal(lowerName, lowerQuery))
            {
                kind = MatchKind.Prefix;
                quality = PrefixQuality(queryLength, nameLength);
                return true;
            }

            // 3. Subsequence first: it is the cheapest reject and camel matches are always subsequences.
            if (!TrySubsequence(lowerName, boundaryBits, name, lowerQuery, out int subsequenceQuality))
            {
                return false;
            }

            // 4. Camel (word boundary rule).
            if (TryCamel(name, lowerName, boundaryBits, lowerQuery, out int camelQuality))
            {
                kind = MatchKind.Camel;
                quality = camelQuality;
                return true;
            }

            kind = MatchKind.Subsequence;
            quality = subsequenceQuality;
            return true;
        }

        /// <summary>
        /// Matches the query as a loose subsequence against the qualified name / file path of an item.
        /// Lower-casing happens on the fly (this path is only reached for candidates that already failed the
        /// primary name match). Returns a small 0..99 quality so qualified-name hits stay below camel matches.
        /// </summary>
        internal static bool TryMatchSecondary(string text, string lowerQuery, out int quality)
        {
            quality = 0;
            int queryLength = lowerQuery.Length;
            if (queryLength == 0 || queryLength > text.Length)
            {
                return false;
            }

            int j = 0;
            int first = -1;
            int last = -1;
            int previous = -2;
            int boundaryHits = 0;
            int contiguous = 0;

            for (int i = 0; i < text.Length && j < queryLength; i++)
            {
                char c = text[i];
                if (c >= 'A' && c <= 'Z')
                {
                    c = (char)(c + 32);
                }
                else if (c > 127)
                {
                    c = char.ToLowerInvariant(c);
                }

                if (c != lowerQuery[j])
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                }

                if (previous == i - 1)
                {
                    contiguous++;
                }

                if (IsBoundary(text, i))
                {
                    boundaryHits++;
                }

                previous = i;
                last = i;
                j++;
            }

            if (j < queryLength)
            {
                return false;
            }

            int score = (10 * boundaryHits) + (5 * contiguous);
            if (first == 0)
            {
                score += 10;
            }

            score -= first < 20 ? first : 20;
            int span = last - first + 1;
            int gap = span - queryLength;
            score -= gap < 30 ? gap : 30;
            if (score < 0)
            {
                score = 0;
            }

            // Keep qualified-name quality below camel quality (both feed a 0..299 slot, this one 0..99).
            quality = score > 198 ? 99 : score / 2;
            return true;
        }

        /// <summary>
        /// Produces highlight ranges for an already-known match. <paramref name="scratch"/> may be a reusable
        /// buffer of at least <c>lowerQuery.Length</c> integers; when null a temporary buffer is allocated.
        /// </summary>
        internal static IReadOnlyList<TextRange> GetHighlightRangesPrepared(
            string name,
            string lowerName,
            ulong boundaryBits,
            string lowerQuery,
            MatchKind kind,
            int[]? scratch)
        {
            int queryLength = lowerQuery.Length;
            if (queryLength == 0)
            {
                return NoRangesList;
            }

            if (kind == MatchKind.Exact || kind == MatchKind.Prefix)
            {
                int length = name.Length < queryLength ? name.Length : queryLength;
                return length <= 0 ? NoRangesList : new[] { new TextRange(0, length) };
            }

            if (kind != MatchKind.Camel && kind != MatchKind.Subsequence)
            {
                return NoRangesList;
            }

            int[] positions = scratch != null && scratch.Length >= queryLength ? scratch : new int[queryLength];

            if (kind == MatchKind.Camel)
            {
                if (!TryFillCamelPositions(name, lowerName, boundaryBits, lowerQuery, positions))
                {
                    return NoRangesList;
                }
            }
            else if (!TryFillSubsequencePositions(lowerName, lowerQuery, positions))
            {
                return NoRangesList;
            }

            return BuildRanges(name.Length, positions, queryLength);
        }

        // -------------------------------------------------------------------------------------------
        // Pre-computation helpers used by SearchIndex when items are added.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns a bit mask with bit <c>i</c> set when position <c>i</c> in <paramref name="text"/> starts a
        /// new "word" (start of string, after a separator, lower-to-upper case change, or digit transition).
        /// Returns 0 when the text is empty or longer than <see cref="MaxBoundaryBitsLength"/> (callers then
        /// fall back to computing boundaries on the fly).
        /// </summary>
        internal static ulong ComputeBoundaryBits(string text)
        {
            int length = text.Length;
            if (length == 0 || length > MaxBoundaryBitsLength)
            {
                return 0UL;
            }

            ulong bits = 1UL; // index 0 always starts a word
            for (int i = 1; i < length; i++)
            {
                if (IsBoundary(text, i))
                {
                    bits |= 1UL << i;
                }
            }

            return bits;
        }

        /// <summary>
        /// Computes a 36-bit presence mask for ASCII lower-case letters (bits 0..25) and digits (bits 26..35)
        /// of <paramref name="text"/>. Returns <see cref="ulong.MaxValue"/> when the text contains a non-ASCII
        /// character, which makes the mask permissive (never rejects a candidate wrongly).
        /// </summary>
        internal static ulong ComputeAsciiMask(string text)
        {
            ulong mask = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c > 127)
                {
                    return ulong.MaxValue;
                }

                if (c >= 'A' && c <= 'Z')
                {
                    c = (char)(c + 32);
                }

                if (c >= 'a' && c <= 'z')
                {
                    mask |= 1UL << (c - 'a');
                }
                else if (c >= '0' && c <= '9')
                {
                    mask |= 1UL << (26 + (c - '0'));
                }
            }

            return mask;
        }

        /// <summary>
        /// Computes the presence mask of an already lower-cased query. When <paramref name="usable"/> is false
        /// the query contains characters outside [a-z0-9] and mask-based rejection must be skipped.
        /// </summary>
        internal static ulong ComputeQueryMask(string lowerQuery, out bool usable)
        {
            usable = true;
            ulong mask = 0;
            for (int i = 0; i < lowerQuery.Length; i++)
            {
                char c = lowerQuery[i];
                if (c >= 'a' && c <= 'z')
                {
                    mask |= 1UL << (c - 'a');
                }
                else if (c >= '0' && c <= '9')
                {
                    mask |= 1UL << (26 + (c - '0'));
                }
                else
                {
                    usable = false;
                    return 0;
                }
            }

            return mask;
        }

        // -------------------------------------------------------------------------------------------
        // Token boundary helpers.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// True when position <paramref name="index"/> starts a new word in <paramref name="text"/>.
        /// Handles camelCase, PascalCase, snake_case, kebab-case, dotted/scoped names and digit transitions.
        /// </summary>
        internal static bool IsBoundary(string text, int index)
        {
            if (index <= 0)
            {
                return true;
            }

            if (index >= text.Length)
            {
                return false;
            }

            char current = text[index];
            if (!char.IsLetterOrDigit(current))
            {
                return false;
            }

            char previous = text[index - 1];
            if (!char.IsLetterOrDigit(previous))
            {
                return true;
            }

            if (char.IsUpper(current) && !char.IsUpper(previous))
            {
                return true;
            }

            if (char.IsDigit(current) && !char.IsDigit(previous))
            {
                return true;
            }

            if (char.IsLetter(current) && char.IsDigit(previous))
            {
                return true;
            }

            return false;
        }

        private static bool IsBoundaryAt(string originalName, ulong boundaryBits, int index)
        {
            if (index <= 0)
            {
                return true;
            }

            if (boundaryBits != 0)
            {
                return ((boundaryBits >> index) & 1UL) != 0;
            }

            return IsBoundary(originalName, index);
        }

        private static int FindNextBoundary(string originalName, ulong boundaryBits, int from)
        {
            if (from < 1)
            {
                from = 1;
            }

            int length = originalName.Length;
            for (int i = from; i < length; i++)
            {
                if (IsBoundaryAt(originalName, boundaryBits, i))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindNextBoundaryMatch(string originalName, string lowerName, ulong boundaryBits, int from, char target)
        {
            int boundary = FindNextBoundary(originalName, boundaryBits, from);
            while (boundary >= 0 && lowerName[boundary] != target)
            {
                boundary = FindNextBoundary(originalName, boundaryBits, boundary + 1);
            }

            return boundary;
        }

        // -------------------------------------------------------------------------------------------
        // Algorithm cores.
        // -------------------------------------------------------------------------------------------

        private static bool StartsWithOrdinal(string text, string prefix)
        {
            if (prefix.Length > text.Length)
            {
                return false;
            }

            for (int i = 0; i < prefix.Length; i++)
            {
                if (text[i] != prefix[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int PrefixQuality(int queryLength, int nameLength)
        {
            if (nameLength <= queryLength)
            {
                return 0;
            }

            int quality = (queryLength * 100) / nameLength;
            return quality > 99 ? 99 : quality;
        }

        private static bool TrySubsequence(string lowerName, ulong boundaryBits, string originalName, string lowerQuery, out int quality)
        {
            quality = 0;
            int queryLength = lowerQuery.Length;
            int nameLength = lowerName.Length;
            int j = 0;
            int first = -1;
            int last = -1;
            int previous = -2;
            int boundaryHits = 0;
            int contiguous = 0;

            for (int i = 0; i < nameLength && j < queryLength; i++)
            {
                if (lowerName[i] != lowerQuery[j])
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                }

                if (previous == i - 1)
                {
                    contiguous++;
                }

                if (IsBoundaryAt(originalName, boundaryBits, i))
                {
                    boundaryHits++;
                }

                previous = i;
                last = i;
                j++;
            }

            if (j < queryLength)
            {
                return false;
            }

            int score = (15 * boundaryHits) + (10 * contiguous);
            if (first == 0)
            {
                score += 20;
            }

            score -= first < 40 ? first : 40;
            int span = last - first + 1;
            int gap = span - queryLength;
            score -= gap < 60 ? gap : 60;
            if (score < 0)
            {
                score = 0;
            }
            else if (score > MaxQuality)
            {
                score = MaxQuality;
            }

            quality = score;
            return true;
        }

        private static bool TryCamel(string originalName, string lowerName, ulong boundaryBits, string lowerQuery, out int quality)
        {
            quality = 0;
            int start = FindCamelStart(originalName, lowerName, boundaryBits, lowerQuery[0]);
            while (start >= 0)
            {
                if (TryCamelFrom(originalName, lowerName, boundaryBits, lowerQuery, start, null, out quality))
                {
                    return true;
                }

                start = FindNextBoundaryMatch(originalName, lowerName, boundaryBits, start + 1, lowerQuery[0]);
            }

            return false;
        }

        /// <summary>Finds the first word boundary whose character matches <paramref name="first"/>.</summary>
        private static int FindCamelStart(string originalName, string lowerName, ulong boundaryBits, char first)
        {
            if (lowerName.Length > 0 && lowerName[0] == first)
            {
                return 0;
            }

            return FindNextBoundaryMatch(originalName, lowerName, boundaryBits, 1, first);
        }

        private static bool TryCamelFrom(
            string originalName,
            string lowerName,
            ulong boundaryBits,
            string lowerQuery,
            int start,
            int[]? positions,
            out int quality)
        {
            quality = 0;
            int queryLength = lowerQuery.Length;
            int nameLength = lowerName.Length;
            int i = start;
            int j = 0;
            int previous = -2;
            int boundaryHits = 0;
            int contiguous = 0;

            while (j < queryLength)
            {
                if (i < nameLength && lowerName[i] == lowerQuery[j])
                {
                    if (positions != null)
                    {
                        positions[j] = i;
                    }

                    if (previous == i - 1)
                    {
                        contiguous++;
                    }

                    if (IsBoundaryAt(originalName, boundaryBits, i))
                    {
                        boundaryHits++;
                    }

                    previous = i;
                    i++;
                    j++;
                    continue;
                }

                int boundary = FindNextBoundaryMatch(originalName, lowerName, boundaryBits, i + 1, lowerQuery[j]);
                if (boundary < 0)
                {
                    quality = 0;
                    return false;
                }

                i = boundary;
            }

            int score = (70 * boundaryHits) + (20 * contiguous);
            quality = score > MaxQuality ? MaxQuality : score;
            return true;
        }

        private static bool TryFillCamelPositions(string originalName, string lowerName, ulong boundaryBits, string lowerQuery, int[] positions)
        {
            int start = FindCamelStart(originalName, lowerName, boundaryBits, lowerQuery[0]);
            while (start >= 0)
            {
                if (TryCamelFrom(originalName, lowerName, boundaryBits, lowerQuery, start, positions, out _))
                {
                    return true;
                }

                start = FindNextBoundaryMatch(originalName, lowerName, boundaryBits, start + 1, lowerQuery[0]);
            }

            return false;
        }

        private static bool TryFillSubsequencePositions(string lowerName, string lowerQuery, int[] positions)
        {
            int queryLength = lowerQuery.Length;
            int j = 0;
            for (int i = 0; i < lowerName.Length && j < queryLength; i++)
            {
                if (lowerName[i] == lowerQuery[j])
                {
                    positions[j] = i;
                    j++;
                }
            }

            return j == queryLength;
        }

        private static IReadOnlyList<TextRange> BuildRanges(int nameLength, int[] positions, int queryLength)
        {
            int runCount = 0;
            int runStart = -1;
            int previous = -2;
            for (int i = 0; i < queryLength; i++)
            {
                int position = positions[i];
                if (position < 0 || position >= nameLength)
                {
                    position = -1;
                }

                if (position < 0)
                {
                    continue;
                }

                if (runStart < 0)
                {
                    runStart = position;
                }
                else if (position != previous + 1)
                {
                    runCount++;
                    runStart = position;
                }

                previous = position;
            }

            if (runStart >= 0)
            {
                runCount++;
            }

            if (runCount == 0)
            {
                return NoRangesList;
            }

            var ranges = new TextRange[runCount];
            int index = 0;
            runStart = -1;
            previous = -2;
            for (int i = 0; i < queryLength; i++)
            {
                int position = positions[i];
                if (position < 0 || position >= nameLength)
                {
                    continue;
                }

                if (runStart < 0)
                {
                    runStart = position;
                }
                else if (position != previous + 1)
                {
                    ranges[index++] = new TextRange(runStart, previous - runStart + 1);
                    runStart = position;
                }

                previous = position;
            }

            if (runStart >= 0)
            {
                ranges[index] = new TextRange(runStart, previous - runStart + 1);
            }

            return ranges;
        }
    }
}

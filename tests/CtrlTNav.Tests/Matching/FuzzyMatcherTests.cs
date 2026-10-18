using System;
using System.Collections.Generic;
using CtrlTNav.Core.Model;
using CtrlTNav.Matching;
using Xunit;

namespace CtrlTNav.Tests.Matching
{
    /// <summary>
    /// Unit tests for <see cref="FuzzyMatcher"/>: every match class, separators, digits, unicode,
    /// case-insensitivity, no-match/empty input and highlight range computation.
    /// </summary>
    public sealed class FuzzyMatcherTests
    {
        [Theory]
        [InlineData("GetUser", "GetUser")]
        [InlineData("GetUser", "getuser")]
        [InlineData("getUser", "GETUSER")]
        [InlineData("STR", "str")]
        public void Exact_MatchesWholeNameIgnoringCase(string candidate, string query)
        {
            Assert.Equal(MatchKind.Exact, FuzzyMatcher.Match(candidate, query));
        }

        [Theory]
        [InlineData("getUserName", "getU")]
        [InlineData("getUserName", "get")]
        [InlineData("getUserName", "GETUSER")]
        [InlineData("string", "str")]
        public void Prefix_MatchesNameStartIgnoringCase(string candidate, string query)
        {
            Assert.Equal(MatchKind.Prefix, FuzzyMatcher.Match(candidate, query));
        }

        [Theory]
        [InlineData("getUserName", "gun")]
        [InlineData("getUserName", "GUN")]
        [InlineData("GetUserName", "gun")]
        [InlineData("get_user_name", "gun")]
        [InlineData("Get_User-Name", "gun")]
        [InlineData("get::user::name", "gun")]
        public void Camel_MatchesWordBoundaries(string candidate, string query)
        {
            Assert.Equal(MatchKind.Camel, FuzzyMatcher.Match(candidate, query));
        }

        [Fact]
        public void Camel_MatchesAcronymOfMixedCaseAndDigits()
        {
            // utf8ToString: word starts at u(0), 8(digit transition), T(case change), S(case change).
            Assert.Equal(MatchKind.Camel, FuzzyMatcher.Match("utf8ToString", "u8ts"));
        }

        [Fact]
        public void Camel_AllowsContiguousRunPerWord()
        {
            // getName = "get" + start of "Name", matching getUserName without being a prefix.
            Assert.Equal(MatchKind.Camel, FuzzyMatcher.Match("getUserName", "getName"));
        }

        [Fact]
        public void Camel_CanStartAtLaterWord()
        {
            Assert.Equal(MatchKind.Camel, FuzzyMatcher.Match("getUserName", "user"));
            Assert.Equal(MatchKind.Camel, FuzzyMatcher.Match("getUserName", "Name"));
        }

        [Fact]
        public void Subsequence_UsedWhenCamelRuleDoesNotHold()
        {
            // eta is a subsequence of getUserName but "e" is not a word boundary, so it cannot be camel.
            Assert.Equal(MatchKind.Subsequence, FuzzyMatcher.Match("getUserName", "eta"));
            Assert.Equal(MatchKind.Subsequence, FuzzyMatcher.Match("Separator", "str"));
            Assert.Equal(MatchKind.Subsequence, FuzzyMatcher.Match("getUserName", "eun"));
        }

        [Fact]
        public void OperatorNames_AreMatched()
        {
            Assert.Equal(MatchKind.Prefix, FuzzyMatcher.Match("operator()", "op"));
            Assert.Equal(MatchKind.Subsequence, FuzzyMatcher.Match("operator<<", "op<<"));

            // "op" (2 chars) and "<<" (2 chars) are two adjacent runs.
            IReadOnlyList<TextRange> ranges = FuzzyMatcher.GetHighlightRanges("operator<<", "op<<");
            Assert.Equal(2, ranges.Count);
            Assert.Equal(new TextRange(0, 2), ranges[0]);
            Assert.Equal(new TextRange(8, 2), ranges[1]);
        }

        [Theory]
        [InlineData("getUserName", "xyz")]
        [InlineData("getUserName", "namuseg")]
        [InlineData("abc", "abcd")]
        [InlineData("abc", "cba")]
        public void NoMatch_ReturnsNone(string candidate, string query)
        {
            Assert.Equal(MatchKind.None, FuzzyMatcher.Match(candidate, query));
        }

        [Theory]
        [InlineData(null, "foo")]
        [InlineData("foo", null)]
        [InlineData("", "foo")]
        [InlineData("foo", "")]
        [InlineData("foo", "   ")]
        public void EmptyOrNullInputs_DoNotMatch(string? candidate, string? query)
        {
            Assert.Equal(MatchKind.None, FuzzyMatcher.Match(candidate, query));
            Assert.False(FuzzyMatcher.TryMatch(candidate, query, out FuzzyMatchResult result));
            Assert.Equal(MatchKind.None, result.Kind);
        }

        [Fact]
        public void Unicode_IsMatchedCaseInsensitively()
        {
            Assert.Equal(MatchKind.Exact, FuzzyMatcher.Match("ÜberClass", "überclass"));
            Assert.Equal(MatchKind.Camel, FuzzyMatcher.Match("ÜberClass", "übc"));
        }

        [Fact]
        public void LongNames_FallBackToOnTheFlyBoundaries()
        {
            // 66 characters: longer than the 64-char pre-computed boundary mask, so the matcher must still
            // recognise the camel-case boundaries of the fallback path.
            string name = "get" + new string('X', 62);
            Assert.Equal(MatchKind.Camel, FuzzyMatcher.Match(name, "gX"));
        }

        [Fact]
        public void Quality_IsBoundedWithinClassRange()
        {
            var candidates = new[]
            {
                "getUserName", "GetUserNameHelper", "get_user_name", "abc",
                "VeryLongSymbolNameThatShouldStillMatch", "SaveToRegistry",
            };
            var queries = new[] { "gun", "ge", "getu", "abc", "vlsn", "str", "u" };
            foreach (string candidate in candidates)
            {
                foreach (string query in queries)
                {
                    if (!FuzzyMatcher.TryMatch(candidate, query, out FuzzyMatchResult result))
                    {
                        continue;
                    }

                    Assert.InRange(result.Quality, 0, 299);
                }
            }
        }

        [Fact]
        public void TryMatch_ReportsMatchKindAndQuality()
        {
            Assert.True(FuzzyMatcher.TryMatch("getUserName", "gun", out FuzzyMatchResult result));
            Assert.Equal(MatchKind.Camel, result.Kind);
            Assert.True(result.IsMatch);

            Assert.False(FuzzyMatcher.TryMatch("getUserName", "zzz", out FuzzyMatchResult noMatch));
            Assert.False(noMatch.IsMatch);
        }

        [Fact]
        public void HighlightRanges_ExactCoversWholeName()
        {
            IReadOnlyList<TextRange> ranges = FuzzyMatcher.GetHighlightRanges("GetUser", "getuser");
            Assert.Single(ranges);
            Assert.Equal(new TextRange(0, 7), ranges[0]);
        }

        [Fact]
        public void HighlightRanges_PrefixCoversQueryLength()
        {
            IReadOnlyList<TextRange> ranges = FuzzyMatcher.GetHighlightRanges("string", "str");
            Assert.Single(ranges);
            Assert.Equal(new TextRange(0, 3), ranges[0]);
        }

        [Fact]
        public void HighlightRanges_CamelProducesOneRangePerMatchedCharacter()
        {
            IReadOnlyList<TextRange> ranges = FuzzyMatcher.GetHighlightRanges("getUserName", "gun");
            Assert.Equal(3, ranges.Count);
            Assert.Equal(new TextRange(0, 1), ranges[0]);
            Assert.Equal(new TextRange(3, 1), ranges[1]);
            Assert.Equal(new TextRange(7, 1), ranges[2]);
        }

        [Fact]
        public void HighlightRanges_SubsequenceMergesAdjacentCharacters()
        {
            // "eta" matches e(1) t(2) contiguous, then a(8): two ranges, first of length 2.
            IReadOnlyList<TextRange> ranges = FuzzyMatcher.GetHighlightRanges("getUserName", "eta");
            Assert.Equal(2, ranges.Count);
            Assert.Equal(new TextRange(1, 2), ranges[0]);
            Assert.Equal(new TextRange(8, 1), ranges[1]);
        }

        [Fact]
        public void HighlightRanges_NoMatch_AreEmpty()
        {
            Assert.Empty(FuzzyMatcher.GetHighlightRanges("getUserName", "zzz"));
            Assert.Empty(FuzzyMatcher.GetHighlightRanges(string.Empty, "abc"));
            Assert.Empty(FuzzyMatcher.GetHighlightRanges(null, "abc"));
            Assert.Empty(FuzzyMatcher.GetHighlightRanges("abc", null));
        }

        [Fact]
        public void SecondaryQualifiedNameMatch_IsSubsequenceAndLowQuality()
        {
            Assert.True(FuzzyMatcher.TryMatchSecondary("std::vector::push_back", "stdvec", out int quality));
            Assert.InRange(quality, 0, 99);
            Assert.False(FuzzyMatcher.TryMatchSecondary("std::vector::push_back", "zzz", out _));
        }

        [Fact]
        public void BoundaryDetection_RecognisesCppNamingStyles()
        {
            Assert.True(FuzzyMatcher.IsBoundary("getUserName", 0));
            Assert.True(FuzzyMatcher.IsBoundary("getUserName", 3));
            Assert.False(FuzzyMatcher.IsBoundary("getUserName", 2));
            Assert.True(FuzzyMatcher.IsBoundary("get_user_name", 4));
            Assert.True(FuzzyMatcher.IsBoundary("std::vector", 5));
            Assert.True(FuzzyMatcher.IsBoundary("utf8ToString", 3));
            Assert.True(FuzzyMatcher.IsBoundary("utf8ToString", 4));
        }
    }
}

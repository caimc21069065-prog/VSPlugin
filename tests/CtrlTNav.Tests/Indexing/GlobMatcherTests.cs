using System;
using CtrlTNav.Indexing;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    public sealed class GlobMatcherTests
    {
        [Theory]
        [InlineData("C:\\projects\\demo\\build\\generated.cpp", "**/build/**", true)]
        [InlineData("C:/projects/demo/build/deep/generated.cpp", "**/build/**", true)]
        [InlineData("C:\\projects\\demo\\src\\main.cpp", "**/build/**", false)]
        [InlineData("C:\\projects\\demo\\build\\main.cpp", "build/**", true)]
        [InlineData("C:\\projects\\demo\\src\\main.cpp", "build/**", false)]
        [InlineData("C:\\projects\\demo\\.git\\x.cpp", "**/.git/**", true)]
        [InlineData("C:\\projects\\demo\\src\\x.g.cs", "**/*.g.cs", true)]
        [InlineData("C:\\projects\\demo\\src\\x.cs", "**/*.g.cs", false)]
        [InlineData("C:\\projects\\demo\\obj\\x.obj", "**/obj/**", true)]
        public void MatchesExpectedPaths(string path, string pattern, bool expected)
        {
            Assert.Equal(expected, GlobMatcher.IsMatch(path, pattern));
        }

        [Fact]
        public void MatchingIsCaseInsensitive()
        {
            Assert.True(GlobMatcher.IsMatch("C:\\Project\\BUILD\\out.cpp", "**/build/**"));
            Assert.True(GlobMatcher.IsMatch("C:\\Project\\Generated\\x.h", "**/generated/**"));
        }

        [Fact]
        public void QuestionMarkMatchesSingleNonSeparatorCharacter()
        {
            Assert.True(GlobMatcher.IsMatch("C:\\src\\a.h", "?.h"));
            Assert.False(GlobMatcher.IsMatch("C:\\src\\ab.h", "?.h"));
            Assert.True(GlobMatcher.IsMatch("C:\\src\\file1.cpp", "file?.cpp"));
            Assert.False(GlobMatcher.IsMatch("C:\\src\\file12.cpp", "file?.cpp"));
        }

        [Fact]
        public void DoubleStarMatchesAnyDepth()
        {
            Assert.True(GlobMatcher.IsMatch("C:\\src\\a\\b\\c\\target.cpp", "**/target.cpp"));
            Assert.True(GlobMatcher.IsMatch("C:\\src\\a\\b\\c\\target.cpp", "**"));
            Assert.True(GlobMatcher.IsMatch("target.cpp", "**/target.cpp"));
        }

        [Fact]
        public void RegexSpecialCharactersAreLiteral()
        {
            Assert.True(GlobMatcher.IsMatch("C:\\src\\foo[1]\\x.cpp", "**/foo[1]/**"));
            Assert.False(GlobMatcher.IsMatch("C:\\src\\foo1\\x.cpp", "**/foo[1]/**"));
            Assert.True(GlobMatcher.IsMatch("C:\\src\\a+b.h", "a+b.h"));
        }

        [Fact]
        public void BackslashAndSlashPathsAreEquivalent()
        {
            Assert.True(GlobMatcher.IsMatch("C:\\src\\out\\x.cpp", "**/out/**"));
            Assert.True(GlobMatcher.IsMatch("C:/src/out/x.cpp", "**/out/**"));
        }

        [Fact]
        public void IsExcludedChecksAllPatterns()
        {
            string[] patterns = { "**/build/**", "**/.git/**", "**/*.generated.cs" };
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\build\\a.cpp", patterns));
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\.git\\a.cpp", patterns));
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\a.generated.cs", patterns));
            Assert.False(GlobMatcher.IsExcluded("C:\\p\\src\\a.cpp", patterns));
        }

        [Fact]
        public void EmptyAndNullInputsAreNotMatches()
        {
            Assert.False(GlobMatcher.IsMatch(string.Empty, "**"));
            Assert.False(GlobMatcher.IsMatch("C:\\p\\a.cpp", string.Empty));
            Assert.False(GlobMatcher.IsMatch(null!, "**"));
            Assert.False(GlobMatcher.IsMatch("C:\\p\\a.cpp", null!));
            Assert.False(GlobMatcher.IsExcluded("C:\\p\\a.cpp", Array.Empty<string>()));
            Assert.False(GlobMatcher.IsExcluded("C:\\p\\a.cpp", null!));
            Assert.False(GlobMatcher.IsExcluded(string.Empty, new[] { "**" }));
        }

        [Fact]
        public void DefaultExcludePatternsWork()
        {
            string[] patterns = CtrlTNav.Core.Options.CtrlTNavOptions.SplitList(
                CtrlTNav.Core.Options.CtrlTNavOptions.DefaultExcludePatterns);

            Assert.True(GlobMatcher.IsExcluded("C:\\p\\build\\a.cpp", patterns));
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\out\\a.cpp", patterns));
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\bin\\a.cpp", patterns));
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\obj\\a.cpp", patterns));
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\.git\\a.cpp", patterns));
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\.vs\\a.cpp", patterns));
            Assert.True(GlobMatcher.IsExcluded("C:\\p\\Generated\\a.cpp", patterns));
            Assert.False(GlobMatcher.IsExcluded("C:\\p\\src\\a.cpp", patterns));
        }
    }
}

using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using Xunit;

namespace CtrlTNav.Tests.Matching
{
    public sealed class SearchQueryTests
    {
        [Theory]
        [InlineData("t ", ItemKindFilter.Types, "")]
        [InlineData("t push", ItemKindFilter.Types, "push")]
        [InlineData("t:", ItemKindFilter.Types, "")]
        [InlineData("T Widget", ItemKindFilter.Types, "Widget")]
        [InlineData("type: widget", ItemKindFilter.Types, "widget")]
        [InlineData("f ", ItemKindFilter.Files, "")]
        [InlineData("f main", ItemKindFilter.Files, "main")]
        [InlineData("file: src/main.cpp", ItemKindFilter.Files, "src/main.cpp")]
        [InlineData("# ", ItemKindFilter.Symbols, "")]
        [InlineData("#push", ItemKindFilter.Symbols, "push")]
        [InlineData("s ", ItemKindFilter.Symbols, "")]
        [InlineData("sym: push", ItemKindFilter.Symbols, "push")]
        [InlineData("m ", ItemKindFilter.Members, "")]
        [InlineData("member: value", ItemKindFilter.Members, "value")]
        [InlineData("symbol: push", ItemKindFilter.Symbols, "push")]
        public void ParsesPrefixesWithAndWithoutText(string raw, ItemKindFilter expectedFilter, string expectedText)
        {
            SearchQuery query = SearchQuery.Parse(raw);

            Assert.True(query.HasFilterPrefix);
            Assert.Equal(expectedFilter, query.Filter);
            Assert.Equal(expectedText, query.Text);
            Assert.Equal(raw, query.RawText);
        }

        [Theory]
        [InlineData("t")]
        [InlineData("push")]
        [InlineData("foo bar")]
        [InlineData("  foo  ")]
        public void TextWithoutPrefixIsSearchedVerbatim(string raw)
        {
            SearchQuery query = SearchQuery.Parse(raw);

            Assert.False(query.HasFilterPrefix);
            Assert.Equal(ItemKindFilter.All, query.Filter);
            Assert.Equal(raw.Trim(), query.Text);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyInputYieldsEmptyQuery(string? raw)
        {
            SearchQuery query = SearchQuery.Parse(raw);

            Assert.True(query.IsEmpty);
            Assert.False(query.HasFilterPrefix);
            Assert.Equal(ItemKindFilter.All, query.Filter);
        }

        [Fact]
        public void LeadingWhitespaceStillAllowsAPrefix()
        {
            SearchQuery query = SearchQuery.Parse("  t widget");

            Assert.True(query.HasFilterPrefix);
            Assert.Equal(ItemKindFilter.Types, query.Filter);
            Assert.Equal("widget", query.Text);
        }
    }
}

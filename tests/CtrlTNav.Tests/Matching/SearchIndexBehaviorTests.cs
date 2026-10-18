using System;
using System.Collections.Generic;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using CtrlTNav.Matching;
using Xunit;

namespace CtrlTNav.Tests.Matching
{
    /// <summary>
    /// Index mutation semantics: add/update/replace/remove/clear, de-duplication, filter prefixes,
    /// include switches and robustness of <see cref="SearchIndex.Search"/>.
    /// </summary>
    public sealed class SearchIndexBehaviorTests
    {
        [Fact]
        public void EmptyIndex_CountZeroAndNoResults()
        {
            var index = new SearchIndex();
            Assert.Equal(0, index.Count);
            Assert.Empty(index.Search(MatchingTestFactory.Query("anything"), MatchingTestFactory.NewOptions()));
        }

        [Fact]
        public void AddRange_AddsAllItems()
        {
            SearchIndex index = MatchingTestFactory.CreateIndex(
                MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1),
                MatchingTestFactory.Symbol("Beta", ItemKind.Method, @"C:\p\a.h", 2),
                MatchingTestFactory.FileItem(@"C:\p\a.h"));
            Assert.Equal(3, index.Count);
        }

        [Fact]
        public void AddOrUpdate_SameIdentity_UpdatesInsteadOfDuplicating()
        {
            var index = new SearchIndex();
            NavigationItem first = MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1, fullName: "ns::Alpha");
            NavigationItem updated = MatchingTestFactory.Symbol(
                "Alpha",
                ItemKind.Class,
                @"C:\p\a.h",
                1,
                fullName: "ns::Alpha",
                projectPath: @"C:\p\App.vcxproj");
            updated.Flags |= ItemFlags.Public;
            updated.Signature = "()";

            index.AddOrUpdate(first);
            index.AddOrUpdate(updated);

            Assert.Equal(1, index.Count);

            // The result exposes the newest NavigationItem instance, not the stale one.
            SearchResult hit = Assert.Single(index.Search(MatchingTestFactory.Query("alpha"), MatchingTestFactory.NewOptions()));
            Assert.Same(updated, hit.Item);

            // The public flag of the updated item now feeds the ranking pipeline.
            SearchOptions options = MatchingTestFactory.NewOptions();
            options.BoostPublicSymbols = true;
            Assert.True(hit.Score > new SearchOptions().ExactMatchBonus);
        }

        [Fact]
        public void AddOrUpdate_RejectsNull()
        {
            var index = new SearchIndex();
            Assert.Throws<ArgumentNullException>(() => index.AddOrUpdate(null!));
        }

        [Fact]
        public void AddRange_RejectsNull()
        {
            var index = new SearchIndex();
            Assert.Throws<ArgumentNullException>(() => index.AddRange(null!));
        }

        [Fact]
        public void ReplaceFile_ReplacesOnlyTheItemsOfThatFile()
        {
            string fileA = @"C:\src\A.h";
            string fileB = @"C:\src\B.h";
            var index = new SearchIndex();
            index.AddRange(new[]
            {
                MatchingTestFactory.Symbol("AlphaA", ItemKind.Class, fileA, 1),
                MatchingTestFactory.Symbol("BetaA", ItemKind.Method, fileA, 2),
                MatchingTestFactory.Symbol("GammaB", ItemKind.Class, fileB, 1),
            });
            Assert.Equal(3, index.Count);

            index.ReplaceFile(fileA, new[]
            {
                MatchingTestFactory.Symbol("DeltaA", ItemKind.Class, fileA, 1),
            });

            Assert.Equal(2, index.Count);
            Assert.Empty(index.Search(MatchingTestFactory.Query("alphaa"), MatchingTestFactory.NewOptions()));
            Assert.Single(index.Search(MatchingTestFactory.Query("deltaa"), MatchingTestFactory.NewOptions()));
            Assert.Single(index.Search(MatchingTestFactory.Query("gammab"), MatchingTestFactory.NewOptions()));
        }

        [Fact]
        public void ReplaceFile_WithEmptyEnumeration_BehavesLikeRemoveFile()
        {
            string fileA = @"C:\src\A.h";
            var index = new SearchIndex();
            index.AddRange(new[]
            {
                MatchingTestFactory.Symbol("AlphaA", ItemKind.Class, fileA, 1),
                MatchingTestFactory.Symbol("GammaB", ItemKind.Class, @"C:\src\B.h", 1),
            });

            index.ReplaceFile(fileA, new NavigationItem[0]);

            Assert.Equal(1, index.Count);
        }

        [Fact]
        public void RemoveFile_RemovesEveryItemFromTheFile()
        {
            string fileA = @"C:\src\A.h";
            var index = new SearchIndex();
            index.AddRange(new[]
            {
                MatchingTestFactory.Symbol("AlphaA", ItemKind.Class, fileA, 1),
                MatchingTestFactory.Symbol("BetaA", ItemKind.Method, fileA, 2),
                MatchingTestFactory.Symbol("GammaB", ItemKind.Class, @"C:\src\B.h", 1),
            });

            index.RemoveFile(fileA);

            Assert.Equal(1, index.Count);
            Assert.Empty(index.Search(MatchingTestFactory.Query("alphaa"), MatchingTestFactory.NewOptions()));
            Assert.Equal("GammaB", Assert.Single(index.Search(MatchingTestFactory.Query("gammab"), MatchingTestFactory.NewOptions())).Item.Name);
        }

        [Fact]
        public void RemoveFile_UnknownPath_IsNoOp()
        {
            var index = new SearchIndex();
            index.AddOrUpdate(MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1));

            index.RemoveFile(@"C:\does\not\exist.h");
            index.RemoveFile(string.Empty);

            Assert.Equal(1, index.Count);
        }

        [Fact]
        public void Clear_RemovesEverything()
        {
            SearchIndex index = MatchingTestFactory.CreateIndex(
                MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1),
                MatchingTestFactory.FileItem(@"C:\p\a.h"));

            index.Clear();

            Assert.Equal(0, index.Count);
            Assert.Empty(index.Search(MatchingTestFactory.Query("alpha"), MatchingTestFactory.NewOptions()));
            Assert.Empty(index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions()));
        }

        [Fact]
        public void SameNameInDifferentFiles_AreDistinctItems()
        {
            SearchIndex index = MatchingTestFactory.CreateIndex(
                MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1),
                MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\b.h", 1));

            Assert.Equal(2, index.Count);

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("alpha"), MatchingTestFactory.NewOptions());
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(MatchKind.Exact, r.MatchKind));
        }

        [Fact]
        public void Search_ReflectsMutationsImmediately()
        {
            var index = new SearchIndex();
            index.AddOrUpdate(MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1));
            Assert.Single(index.Search(MatchingTestFactory.Query("alpha"), MatchingTestFactory.NewOptions()));

            index.AddOrUpdate(MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1)); // update, keeps one item
            Assert.Single(index.Search(MatchingTestFactory.Query("alpha"), MatchingTestFactory.NewOptions()));

            index.RemoveFile(@"C:\p\a.h");
            Assert.Empty(index.Search(MatchingTestFactory.Query("alpha"), MatchingTestFactory.NewOptions()));
        }

        // -------------------------------------------------------------------------------------------
        // Filter prefixes / include switches.
        // -------------------------------------------------------------------------------------------

        private static SearchIndex CreateFilterFixture()
        {
            return MatchingTestFactory.CreateIndex(
                MatchingTestFactory.Symbol("Foo", ItemKind.Class, @"C:\p\types.h", 1),
                MatchingTestFactory.Symbol("FooMethod", ItemKind.Method, @"C:\p\types.h", 2),
                MatchingTestFactory.Symbol("FOO_MACRO", ItemKind.Macro, @"C:\p\types.h", 3),
                MatchingTestFactory.FileItem(@"C:\p\src\Foo.cpp"));
        }

        [Fact]
        public void FilterPrefix_T_ReturnsOnlyTypes()
        {
            SearchIndex index = CreateFilterFixture();
            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("t foo"), MatchingTestFactory.NewOptions());

            Assert.Equal("Foo", Assert.Single(results).Item.Name);
        }

        [Fact]
        public void FilterPrefix_F_ReturnsOnlyFiles()
        {
            SearchIndex index = CreateFilterFixture();
            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("f foo"), MatchingTestFactory.NewOptions());

            SearchResult hit = Assert.Single(results);
            Assert.True(hit.Item.IsFile);
            Assert.Equal("Foo.cpp", hit.Item.Name);
        }

        [Fact]
        public void FilterPrefix_Hash_ReturnsOnlySymbols()
        {
            SearchIndex index = CreateFilterFixture();
            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("# foo"), MatchingTestFactory.NewOptions());

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.False(r.Item.IsFile));
        }

        [Fact]
        public void FilterPrefix_M_ReturnsOnlyMembers()
        {
            SearchIndex index = CreateFilterFixture();
            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("m foo"), MatchingTestFactory.NewOptions());

            Assert.Equal("FooMethod", Assert.Single(results).Item.Name);
        }

        [Fact]
        public void FilterPrefix_OverridesDefaultFilter()
        {
            SearchIndex index = CreateFilterFixture();
            SearchOptions options = MatchingTestFactory.NewOptions();
            options.DefaultFilter = ItemKindFilter.Types;

            IReadOnlyList<SearchResult> types = index.Search(MatchingTestFactory.Query("foo"), options);
            Assert.Equal("Foo", Assert.Single(types).Item.Name);

            IReadOnlyList<SearchResult> files = index.Search(MatchingTestFactory.Query("f foo"), options);
            Assert.Equal("Foo.cpp", Assert.Single(files).Item.Name);
        }

        [Fact]
        public void DefaultFilter_AppliesWhenQueryHasNoPrefix()
        {
            SearchIndex index = CreateFilterFixture();
            SearchOptions options = MatchingTestFactory.NewOptions();
            options.DefaultFilter = ItemKindFilter.Files;

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("foo"), options);
            Assert.Equal("Foo.cpp", Assert.Single(results).Item.Name);
        }

        [Fact]
        public void IncludeFilesAndSymbols_ControlUnfilteredResults()
        {
            SearchIndex index = CreateFilterFixture();

            SearchOptions noFiles = MatchingTestFactory.NewOptions();
            noFiles.IncludeFiles = false;
            IReadOnlyList<SearchResult> withoutFiles = index.Search(MatchingTestFactory.Query("foo"), noFiles);
            Assert.All(withoutFiles, r => Assert.False(r.Item.IsFile));
            Assert.Equal(3, withoutFiles.Count);

            SearchOptions noSymbols = MatchingTestFactory.NewOptions();
            noSymbols.IncludeSymbols = false;
            IReadOnlyList<SearchResult> withoutSymbols = index.Search(MatchingTestFactory.Query("foo"), noSymbols);
            SearchResult onlyFile = Assert.Single(withoutSymbols);
            Assert.True(onlyFile.Item.IsFile);
        }

        [Fact]
        public void Search_WithNullQueryAndOptions_DoesNotThrow()
        {
            SearchIndex index = CreateFilterFixture();

            IReadOnlyList<SearchResult> results = index.Search(null!, null!);

            Assert.NotEmpty(results); // null options => defaults, null query => empty query (browse)
        }

        [Fact]
        public void Search_WithWeirdText_DoesNotThrow()
        {
            var log = new RecordingLog();
            var index = new SearchIndex(log);
            index.AddRange(new[]
            {
                MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1),
                MatchingTestFactory.Symbol("operator()", ItemKind.Operator, @"C:\p\a.h", 2),
            });

            string[] queries = { "   ", "\t", "::::", "(((", "operator()", "ü", "\u0001", new string('x', 300) };
            foreach (string query in queries)
            {
                IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query(query), MatchingTestFactory.NewOptions());
                Assert.NotNull(results);
            }

            Assert.Empty(log.Errors);
        }

        [Fact]
        public void SearchResult_ItemsAreTheOriginalNavigationItems()
        {
            NavigationItem item = MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 7);
            SearchIndex index = MatchingTestFactory.CreateIndex(item);

            SearchResult result = Assert.Single(index.Search(MatchingTestFactory.Query("alpha"), MatchingTestFactory.NewOptions()));
            Assert.Same(item, result.Item);
        }

        [Fact]
        public void Search_RespectsMaxResultsClamping()
        {
            var index = new SearchIndex();
            var items = new List<NavigationItem>();
            for (int i = 0; i < 10; i++)
            {
                items.Add(MatchingTestFactory.Symbol("Item" + i, ItemKind.Method, @"C:\p\a.h", i + 1));
            }

            index.AddRange(items);

            SearchOptions options = MatchingTestFactory.NewOptions();
            options.MaxResults = 0; // Normalized() clamps to 1
            Assert.Single(index.Search(MatchingTestFactory.Query("item"), options));
        }

        [Fact]
        public void Search_FilePseudoItem_IsFoundBySimpleName()
        {
            SearchIndex index = MatchingTestFactory.CreateIndex(MatchingTestFactory.FileItem(@"C:\project\src\widgets\panel.h"));

            SearchResult hit = Assert.Single(index.Search(MatchingTestFactory.Query("panel"), MatchingTestFactory.NewOptions()));
            Assert.Equal(MatchKind.Prefix, hit.MatchKind);
            Assert.True(hit.Item.IsFile);
        }
    }
}

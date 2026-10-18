using System.Collections.Generic;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using CtrlTNav.Matching;
using Xunit;

namespace CtrlTNav.Tests.Matching
{
    /// <summary>
    /// Empty-query ("browse") behaviour: recently used items first, then public symbols, then files,
    /// deterministic ordering, explicit filter prefixes as browse modes and MaxResults handling.
    /// </summary>
    public sealed class SearchIndexEmptyQueryTests
    {
        private static SearchIndex CreateIndex(out NavigationItem alphaFile, out NavigationItem zetaFile)
        {
            alphaFile = MatchingTestFactory.FileItem(@"C:\src\alpha.h");
            zetaFile = MatchingTestFactory.FileItem(@"C:\src\zeta.h");
            return MatchingTestFactory.CreateIndex(
                MatchingTestFactory.PublicSymbol("Zebra", ItemKind.Class, @"C:\src\z.h", 1),
                MatchingTestFactory.PublicSymbol("Aardvark", ItemKind.Class, @"C:\src\a.h", 2),
                MatchingTestFactory.Symbol("Apple", ItemKind.Struct, @"C:\src\apple.h", 3), // not public: hidden
                zetaFile,
                alphaFile);
        }

        [Fact]
        public void EmptyQuery_WithoutUsage_ReturnsPublicSymbolsThenFiles()
        {
            SearchIndex index = CreateIndex(out _, out _);

            IReadOnlyList<SearchResult> results = index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions());

            Assert.Equal(new[] { "Aardvark", "Zebra", "alpha.h", "zeta.h" }, MatchingTestFactory.Names(results));
            Assert.All(results, r => Assert.Equal(MatchKind.None, r.MatchKind));
            Assert.All(results, r => Assert.Empty(r.MatchedRanges));
        }

        [Fact]
        public void EmptyQuery_IsDeterministicAcrossCalls()
        {
            SearchIndex index = CreateIndex(out _, out _);

            IReadOnlyList<string> first = MatchingTestFactory.Names(index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions()));
            IReadOnlyList<string> second = MatchingTestFactory.Names(index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions()));

            Assert.Equal(first, second);
        }

        [Fact]
        public void EmptyQuery_RecentlyUsedItemsComeFirst()
        {
            SearchIndex index = CreateIndex(out _, out NavigationItem zetaFile);

            // Rebuild the same identity in the index (the factory creates a new item instance each call, so
            // fetch the frozen item from the index instead of relying on a local variable).
            IReadOnlyList<SearchResult> zebraHits = index.Search(MatchingTestFactory.Query("# zebra"), MatchingTestFactory.NewOptions());
            NavigationItem zebraItem = Assert.Single(zebraHits).Item;

            using (var directory = new TempDirectory())
            {
                var usage = new RecentUsageTracker(directory.File("recent.dat"));
                usage.RecordUsage(zebraItem);   // older
                usage.RecordUsage(zetaFile);    // newest

                index.RecentUsage = usage;
                IReadOnlyList<SearchResult> results = index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions());

                Assert.Equal(new[] { "zeta.h", "Zebra", "Aardvark", "alpha.h" }, MatchingTestFactory.Names(results));
            }
        }

        [Fact]
        public void EmptyQuery_SkipsRecentIdentitiesThatWereRemovedFromTheIndex()
        {
            NavigationItem fileA = MatchingTestFactory.FileItem(@"C:\src\a.h");
            NavigationItem fileB = MatchingTestFactory.FileItem(@"C:\src\b.h");
            SearchIndex index = MatchingTestFactory.CreateIndex(fileA, fileB);

            using (var directory = new TempDirectory())
            {
                var usage = new RecentUsageTracker(directory.File("recent.dat"));
                usage.RecordUsage(fileA);
                usage.RecordUsage(fileB);
                index.RecentUsage = usage;

                index.RemoveFile(fileB.FilePath);

                IReadOnlyList<SearchResult> results = index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions());
                Assert.Equal(new[] { "a.h" }, MatchingTestFactory.Names(results));
            }
        }

        [Fact]
        public void EmptyQuery_WithForeignRecentUsageStore_UsesRecentFilePaths()
        {
            NavigationItem fileA = MatchingTestFactory.FileItem(@"C:\src\a.h");
            SearchIndex index = MatchingTestFactory.CreateIndex(fileA);

            index.RecentUsage = new ForeignRecentStore(new[] { fileA.FilePath });

            IReadOnlyList<SearchResult> results = index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions());
            Assert.Equal("a.h", Assert.Single(results).Item.Name);
        }

        [Fact]
        public void EmptyQuery_WithTypesPrefix_BrowsesAllTypesIncludingNonPublic()
        {
            var index = MatchingTestFactory.CreateIndex(
                MatchingTestFactory.PublicSymbol("Beta", ItemKind.Class, @"C:\src\b.h", 1),
                MatchingTestFactory.Symbol("Alpha", ItemKind.Struct, @"C:\src\a.h", 2),
                MatchingTestFactory.Symbol("GammaMethod", ItemKind.Method, @"C:\src\c.h", 3),
                MatchingTestFactory.FileItem(@"C:\src\d.h"));

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("t:"), MatchingTestFactory.NewOptions());

            // Public symbols rank first inside the browse list, then the rest by kind and name.
            Assert.Equal(new[] { "Beta", "Alpha" }, MatchingTestFactory.Names(results));
        }

        [Fact]
        public void EmptyQuery_WithFilesPrefix_ReturnsOnlyFiles()
        {
            var index = MatchingTestFactory.CreateIndex(
                MatchingTestFactory.PublicSymbol("Beta", ItemKind.Class, @"C:\src\b.h", 1),
                MatchingTestFactory.FileItem(@"C:\src\zeta.h"),
                MatchingTestFactory.FileItem(@"C:\src\alpha.h"));

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("f:"), MatchingTestFactory.NewOptions());

            Assert.Equal(new[] { "alpha.h", "zeta.h" }, MatchingTestFactory.Names(results));
        }

        [Fact]
        public void EmptyQuery_RespectsMaxResults()
        {
            var items = new List<NavigationItem>();
            for (int i = 0; i < 30; i++)
            {
                items.Add(MatchingTestFactory.PublicSymbol("Symbol" + i.ToString("D2"), ItemKind.Class, @"C:\src\a.h", i + 1));
            }

            SearchIndex index = MatchingTestFactory.CreateIndex(items.ToArray());

            IReadOnlyList<SearchResult> results = index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions(5));

            Assert.Equal(5, results.Count);
            Assert.Equal(new[] { "Symbol00", "Symbol01", "Symbol02", "Symbol03", "Symbol04" }, MatchingTestFactory.Names(results));
        }

        [Fact]
        public void EmptyQuery_AfterClear_ReturnsNothing()
        {
            SearchIndex index = CreateIndex(out _, out _);
            index.Clear();

            Assert.Empty(index.Search(SearchQuery.Empty, MatchingTestFactory.NewOptions()));
        }

        /// <summary>Minimal foreign <see cref="IRecentUsageStore"/> implementation (not a RecentUsageTracker).</summary>
        private sealed class ForeignRecentStore : IRecentUsageStore
        {
            private readonly string[] _paths;

            internal ForeignRecentStore(string[] paths)
            {
                _paths = paths;
            }

            public void RecordUsage(NavigationItem item)
            {
            }

            public int GetBoost(NavigationItem item)
            {
                return 0;
            }

            public IReadOnlyList<string> GetRecentFilePaths(int maxCount)
            {
                return _paths;
            }

            public bool IsEmpty
            {
                get { return _paths.Length == 0; }
            }

            public void Clear()
            {
            }
        }
    }
}

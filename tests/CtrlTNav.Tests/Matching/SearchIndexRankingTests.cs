using System.Collections.Generic;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using CtrlTNav.Matching;
using Xunit;

namespace CtrlTNav.Tests.Matching
{
    /// <summary>
    /// Ranking pipeline tests: match class priority, MRU promotion, current-project/public bonuses,
    /// deterministic tie-breakers, top-K truncation and qualified-name-only matches.
    /// </summary>
    public sealed class SearchIndexRankingTests
    {
        [Fact]
        public void MatchClassPriority_ExactThenPrefixThenCamelThenSubsequence()
        {
            // All four items match "str" in a different class.
            NavigationItem subsequence = MatchingTestFactory.Symbol("Separator", ItemKind.Method, @"C:\p\a.h", 1);
            NavigationItem camel = MatchingTestFactory.Symbol("SaveToRegistry", ItemKind.Method, @"C:\p\b.h", 2);
            NavigationItem exact = MatchingTestFactory.Symbol("Str", ItemKind.Class, @"C:\p\c.h", 3);
            NavigationItem prefix = MatchingTestFactory.Symbol("StringHelper", ItemKind.Method, @"C:\p\d.h", 4);
            SearchIndex index = MatchingTestFactory.CreateIndex(subsequence, camel, exact, prefix);

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("str"), MatchingTestFactory.NewOptions());

            Assert.Equal(4, results.Count);
            Assert.Equal(
                new[] { MatchKind.Exact, MatchKind.Prefix, MatchKind.Camel, MatchKind.Subsequence },
                MatchingTestFactory.Kinds(results));
            Assert.Equal(new[] { "Str", "StringHelper", "SaveToRegistry", "Separator" }, MatchingTestFactory.Names(results));

            for (int i = 1; i < results.Count; i++)
            {
                Assert.True(results[i - 1].Score >= results[i].Score, "scores must be non-increasing: " + results[i - 1] + " / " + results[i]);
            }
        }

        [Fact]
        public void MruBoost_CanLiftCamelMatchAboveSlightlyBetterPrefixMatch()
        {
            NavigationItem prefix = MatchingTestFactory.Symbol("SaveFile", ItemKind.Method, @"C:\p\a.h", 1);
            NavigationItem camel = MatchingTestFactory.Symbol("StoreAndVerify", ItemKind.Method, @"C:\p\b.h", 2);
            SearchIndex index = MatchingTestFactory.CreateIndex(prefix, camel);

            SearchOptions options = MatchingTestFactory.NewOptions();
            SearchQuery query = MatchingTestFactory.Query("save");

            IReadOnlyList<SearchResult> withoutUsage = index.Search(query, options);
            Assert.Equal(new[] { "SaveFile", "StoreAndVerify" }, MatchingTestFactory.Names(withoutUsage));
            Assert.Equal(MatchKind.Prefix, withoutUsage[0].MatchKind);
            Assert.Equal(MatchKind.Camel, withoutUsage[1].MatchKind);

            using (var tracker = new TempDirectory())
            {
                var usage = new RecentUsageTracker(tracker.File("recent.dat"));
                for (int i = 0; i < 8; i++)
                {
                    usage.RecordUsage(camel); // max strength: 1000 => +RecentUsageBoostMax(500)
                }

                index.RecentUsage = usage;
                IReadOnlyList<SearchResult> withUsage = index.Search(query, options);

                Assert.Equal("StoreAndVerify", withUsage[0].Item.Name);
                Assert.Equal(MatchKind.Camel, withUsage[0].MatchKind);
                Assert.Equal("SaveFile", withUsage[1].Item.Name);
            }
        }

        [Fact]
        public void MruBoost_CannotBeatExactMatch()
        {
            NavigationItem exact = MatchingTestFactory.Symbol("Save", ItemKind.Method, @"C:\p\a.h", 1);
            NavigationItem prefix = MatchingTestFactory.Symbol("SaveFile", ItemKind.Method, @"C:\p\b.h", 2);
            SearchIndex index = MatchingTestFactory.CreateIndex(exact, prefix);

            using (var tracker = new TempDirectory())
            {
                var usage = new RecentUsageTracker(tracker.File("recent.dat"));
                for (int i = 0; i < 10; i++)
                {
                    usage.RecordUsage(prefix); // strongest possible MRU boost
                }

                index.RecentUsage = usage;
                IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("save"), MatchingTestFactory.NewOptions());

                Assert.Equal("Save", results[0].Item.Name);
                Assert.Equal(MatchKind.Exact, results[0].MatchKind);
            }
        }

        [Fact]
        public void MruBoost_IsIgnoredWhenDisabledInOptions()
        {
            NavigationItem prefix = MatchingTestFactory.Symbol("SaveFile", ItemKind.Method, @"C:\p\a.h", 1);
            NavigationItem camel = MatchingTestFactory.Symbol("StoreAndVerify", ItemKind.Method, @"C:\p\b.h", 2);
            SearchIndex index = MatchingTestFactory.CreateIndex(prefix, camel);

            using (var tracker = new TempDirectory())
            {
                var usage = new RecentUsageTracker(tracker.File("recent.dat"));
                for (int i = 0; i < 10; i++)
                {
                    usage.RecordUsage(camel);
                }

                index.RecentUsage = usage;
                SearchOptions options = MatchingTestFactory.NewOptions();
                options.BoostRecentlyUsed = false;
                IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("save"), options);

                Assert.Equal("SaveFile", results[0].Item.Name);
            }
        }

        [Fact]
        public void MruBoost_ScalesByRecentUsageBoostMax()
        {
            NavigationItem prefix = MatchingTestFactory.Symbol("SaveFile", ItemKind.Method, @"C:\p\a.h", 1);
            NavigationItem camel = MatchingTestFactory.Symbol("StoreAndVerify", ItemKind.Method, @"C:\p\b.h", 2);
            SearchIndex index = MatchingTestFactory.CreateIndex(prefix, camel);

            using (var tracker = new TempDirectory())
            {
                var usage = new RecentUsageTracker(tracker.File("recent.dat"));
                for (int i = 0; i < 10; i++)
                {
                    usage.RecordUsage(camel);
                }

                index.RecentUsage = usage;
                SearchOptions options = MatchingTestFactory.NewOptions();
                options.RecentUsageBoostMax = 0; // disables the MRU contribution
                IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("save"), options);

                Assert.Equal("SaveFile", results[0].Item.Name);
            }
        }

        [Fact]
        public void CurrentProjectBonus_PrefersItemFromActiveProject()
        {
            NavigationItem otherProject = MatchingTestFactory.Symbol(
                "AlphaHelper", ItemKind.Method, @"C:\p\a.h", 1, projectPath: @"C:\p\Other.vcxproj");
            NavigationItem currentProject = MatchingTestFactory.Symbol(
                "AlphaWorker", ItemKind.Method, @"C:\p\b.h", 2, projectPath: @"C:\p\App.vcxproj");
            SearchIndex index = MatchingTestFactory.CreateIndex(otherProject, currentProject);

            SearchOptions options = MatchingTestFactory.NewOptions();
            options.CurrentProjectPath = @"C:\p\App.vcxproj";
            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("alpha"), options);

            Assert.Equal("AlphaWorker", results[0].Item.Name);
            Assert.True(results[0].Score > results[1].Score);

            options.BoostCurrentProject = false;
            IReadOnlyList<SearchResult> withoutBonus = index.Search(MatchingTestFactory.Query("alpha"), options);
            Assert.Equal("AlphaHelper", withoutBonus[0].Item.Name); // deterministic ordinal tie-break
        }

        [Fact]
        public void PublicSymbolBonus_PrefersPublicOrExportedSymbols()
        {
            NavigationItem privateSymbol = MatchingTestFactory.Symbol("RenderHelper", ItemKind.Method, @"C:\p\a.h", 1);
            NavigationItem publicSymbol = MatchingTestFactory.PublicSymbol("RenderWorker", ItemKind.Method, @"C:\p\b.h", 2);
            SearchIndex index = MatchingTestFactory.CreateIndex(privateSymbol, publicSymbol);

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("render"), MatchingTestFactory.NewOptions());
            Assert.Equal("RenderWorker", results[0].Item.Name);

            SearchOptions options = MatchingTestFactory.NewOptions();
            options.BoostPublicSymbols = false;
            IReadOnlyList<SearchResult> withoutBonus = index.Search(MatchingTestFactory.Query("render"), options);
            Assert.Equal("RenderHelper", withoutBonus[0].Item.Name);
        }

        [Fact]
        public void TieBreakers_ShorterNameThenOrdinalName()
        {
            NavigationItem longer = MatchingTestFactory.Symbol("AbCde", ItemKind.Method, @"C:\p\a.h", 1);
            NavigationItem ordinalEarlier = MatchingTestFactory.Symbol("AbCd", ItemKind.Method, @"C:\p\b.h", 2);
            NavigationItem ordinalLater = MatchingTestFactory.Symbol("AbCe", ItemKind.Method, @"C:\p\c.h", 3);
            SearchIndex index = MatchingTestFactory.CreateIndex(longer, ordinalLater, ordinalEarlier);

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("ab"), MatchingTestFactory.NewOptions());

            Assert.Equal(new[] { "AbCd", "AbCe", "AbCde" }, MatchingTestFactory.Names(results));
        }

        [Fact]
        public void MaxResults_ReturnsAtMostRequestedCountInRankOrder()
        {
            var items = new List<NavigationItem>();
            for (int i = 0; i < 20; i++)
            {
                items.Add(MatchingTestFactory.Symbol("Prefix" + i.ToString("D2"), ItemKind.Method, @"C:\p\a.h", i + 1));
            }

            SearchIndex index = MatchingTestFactory.CreateIndex(items.ToArray());
            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("prefix"), MatchingTestFactory.NewOptions(3));

            Assert.Equal(3, results.Count);
            Assert.Equal(new[] { "Prefix00", "Prefix01", "Prefix02" }, MatchingTestFactory.Names(results));
        }

        [Fact]
        public void QualifiedNameOnlyMatch_IsFullNameKindWithNoHighlightRanges()
        {
            NavigationItem item = MatchingTestFactory.Symbol(
                "push_back",
                ItemKind.Method,
                @"C:\p\vector.h",
                12,
                fullName: "std::vector::push_back");
            SearchIndex index = MatchingTestFactory.CreateIndex(item);

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("stdvec"), MatchingTestFactory.NewOptions());

            SearchResult hit = Assert.Single(results);
            Assert.Equal(MatchKind.FullName, hit.MatchKind);
            Assert.Empty(hit.MatchedRanges);
            Assert.True(hit.Score >= new SearchOptions().FullNameMatchBonus);

            // The item is still findable by its simple name with a real match class.
            IReadOnlyList<SearchResult> byName = index.Search(MatchingTestFactory.Query("push"), MatchingTestFactory.NewOptions());
            Assert.Equal(MatchKind.Prefix, Assert.Single(byName).MatchKind);
        }

        [Fact]
        public void Score_UsesConfiguredMatchClassWeights()
        {
            NavigationItem item = MatchingTestFactory.Symbol("Alpha", ItemKind.Class, @"C:\p\a.h", 1);
            SearchIndex index = MatchingTestFactory.CreateIndex(item);

            SearchOptions defaults = MatchingTestFactory.NewOptions();
            SearchResult defaultHit = Assert.Single(index.Search(MatchingTestFactory.Query("alpha"), defaults));
            Assert.Equal(defaults.ExactMatchBonus, defaultHit.Score);

            SearchOptions custom = MatchingTestFactory.NewOptions();
            custom.ExactMatchBonus = 5000;
            SearchResult customHit = Assert.Single(index.Search(MatchingTestFactory.Query("alpha"), custom));
            Assert.Equal(5000, customHit.Score);
        }

        [Fact]
        public void Score_CombinesClassQualityAndAllBonuses()
        {
            NavigationItem item = MatchingTestFactory.PublicSymbol(
                "SaveFile",
                ItemKind.Method,
                @"C:\p\a.h",
                1,
                projectPath: @"C:\p\App.vcxproj");
            SearchIndex index = MatchingTestFactory.CreateIndex(item);

            using (var directory = new TempDirectory())
            {
                var usage = new RecentUsageTracker(directory.File("recent.dat"));
                for (int i = 0; i < 10; i++)
                {
                    usage.RecordUsage(item);
                }

                index.RecentUsage = usage;

                SearchOptions options = MatchingTestFactory.NewOptions();
                options.CurrentProjectPath = @"C:\p\App.vcxproj";
                SearchResult hit = Assert.Single(index.Search(MatchingTestFactory.Query("save"), options));

                // Prefix class weight + within-class quality (4/8 coverage -> 50) + MRU max + project + public.
                int expected = options.PrefixMatchBonus
                    + 50
                    + options.RecentUsageBoostMax
                    + options.CurrentProjectBonus
                    + options.PublicSymbolBonus;
                Assert.Equal(expected, hit.Score);
            }
        }

        [Fact]
        public void FilePaths_AreMatchedAsSecondaryText()
        {
            NavigationItem file = MatchingTestFactory.FileItem(@"C:\project\src\widgets\mainwindow.cpp");
            SearchIndex index = MatchingTestFactory.CreateIndex(file);

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("widgets"), MatchingTestFactory.NewOptions());
            Assert.Equal("mainwindow.cpp", Assert.Single(results).Item.Name);

            IReadOnlyList<SearchResult> byName = index.Search(MatchingTestFactory.Query("mainwin"), MatchingTestFactory.NewOptions());
            Assert.Equal(MatchKind.Prefix, Assert.Single(byName).MatchKind);
        }

        [Fact]
        public void ScatteredPathSubsequence_IsNotReturnedAsFileResult()
        {
            // Every letter of "inside" appears in this path in order, but only as a random scatter
            // (the within-class quality is zero). Such hits are noise and must not be listed.
            NavigationItem file = MatchingTestFactory.FileItem(
                @"C:\solution\src\third_party\include\broken.cpp");
            SearchIndex index = MatchingTestFactory.CreateIndex(file);

            Assert.Empty(index.Search(MatchingTestFactory.Query("inside"), MatchingTestFactory.NewOptions()));
        }

        [Fact]
        public void ScatteredQualifiedNameSubsequence_IsNotReturnedAsFullNameMatch()
        {
            NavigationItem item = MatchingTestFactory.Symbol(
                "CreateDefault",
                ItemKind.Method,
                @"C:\p\a.h",
                1,
                fullName: "navsample::Widget::CreateDefault");
            SearchIndex index = MatchingTestFactory.CreateIndex(item);

            Assert.Empty(index.Search(MatchingTestFactory.Query("vali"), MatchingTestFactory.NewOptions()));
        }

        [Fact]
        public void HighlightRanges_AreRelativeToItemName()
        {
            NavigationItem item = MatchingTestFactory.Symbol("getUserName", ItemKind.Method, @"C:\p\a.h", 1);
            SearchIndex index = MatchingTestFactory.CreateIndex(item);

            IReadOnlyList<SearchResult> results = index.Search(MatchingTestFactory.Query("gun"), MatchingTestFactory.NewOptions());
            SearchResult hit = Assert.Single(results);

            Assert.Equal(MatchKind.Camel, hit.MatchKind);
            Assert.Equal(3, hit.MatchedRanges.Count);
            Assert.Equal(new TextRange(0, 1), hit.MatchedRanges[0]);
            Assert.Equal(new TextRange(3, 1), hit.MatchedRanges[1]);
            Assert.Equal(new TextRange(7, 1), hit.MatchedRanges[2]);
        }

        [Fact]
        public void FullName_IsNotUsedForHighlightRangesWhenSimpleNameMatches()
        {
            NavigationItem item = MatchingTestFactory.Symbol(
                "save_file",
                ItemKind.Method,
                @"C:\p\a.h",
                1,
                fullName: "storage::save_file");
            SearchIndex index = MatchingTestFactory.CreateIndex(item);

            SearchResult hit = Assert.Single(index.Search(MatchingTestFactory.Query("save"), MatchingTestFactory.NewOptions()));
            Assert.Equal(MatchKind.Prefix, hit.MatchKind);
            Assert.Equal(new TextRange(0, 4), Assert.Single(hit.MatchedRanges));
        }
    }
}

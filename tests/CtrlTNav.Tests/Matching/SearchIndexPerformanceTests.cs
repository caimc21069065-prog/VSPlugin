using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using CtrlTNav.Matching;
using Xunit;
using Xunit.Abstractions;

namespace CtrlTNav.Tests.Matching
{
    /// <summary>
    /// Performance tests for the search hot path. The architecture target is 100k items with a 6-character
    /// query in under 30 ms (NF2). The assertions use a much more generous bound so the suite is not flaky on
    /// slow/busy CI machines, but the measured numbers are printed so a regression is visible in the test log.
    /// </summary>
    [Collection("MatchingPerformance")]
    public sealed class SearchIndexPerformanceTests
    {
        private const int ItemCount = 100_000;

        private readonly ITestOutputHelper _output;

        public SearchIndexPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Search_100kItems_SixCharQuery_IsFastEnoughForLiveFiltering()
        {
            var buildStopwatch = Stopwatch.StartNew();
            var index = new SearchIndex();
            index.AddRange(CreateItems(ItemCount));
            buildStopwatch.Stop();

            SearchOptions options = new SearchOptions { MaxResults = 120 };
            SearchQuery query = SearchQuery.Parse("vector");

            // Warm up: the first search may build the item snapshot and JIT the matching path.
            for (int i = 0; i < 3; i++)
            {
                index.Search(query, options);
            }

            const int iterations = 20;
            var stopwatch = new Stopwatch();
            double total = 0;
            double min = double.MaxValue;
            double max = 0;
            int lastResultCount = 0;

            for (int i = 0; i < iterations; i++)
            {
                stopwatch.Restart();
                IReadOnlyList<SearchResult> results = index.Search(query, options);
                stopwatch.Stop();
                lastResultCount = results.Count;
                double elapsed = stopwatch.Elapsed.TotalMilliseconds;
                total += elapsed;
                min = Math.Min(min, elapsed);
                max = Math.Max(max, elapsed);
            }

            double average = total / iterations;
            _output.WriteLine("index build (100k items, AddRange): {0:F1} ms", buildStopwatch.Elapsed.TotalMilliseconds);
            _output.WriteLine("6-char query 'vector' on 100k items: avg {0:F3} ms, min {1:F3} ms, max {2:F3} ms, results {3}",
                average, min, max, lastResultCount);

            Assert.True(lastResultCount > 0, "the performance fixture must produce hits");
            Assert.True(average < 250.0, string.Format(CultureInfo.InvariantCulture, "average search took {0:F1} ms", average));
        }

        [Fact]
        public void Search_ShortTwoCharQuery_IsStillFast()
        {
            var index = new SearchIndex();
            index.AddRange(CreateItems(ItemCount));
            SearchOptions options = new SearchOptions { MaxResults = 120 };
            SearchQuery query = SearchQuery.Parse("ve");

            for (int i = 0; i < 3; i++)
            {
                index.Search(query, options);
            }

            const int iterations = 10;
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                index.Search(query, options);
            }

            stopwatch.Stop();
            double average = stopwatch.Elapsed.TotalMilliseconds / iterations;
            _output.WriteLine("2-char query 've' on 100k items: avg {0:F3} ms", average);
            Assert.True(average < 500.0, string.Format(CultureInfo.InvariantCulture, "average short-query search took {0:F1} ms", average));
        }

        [Fact]
        public void Search_EmptyQueryOn100kItems_IsFast()
        {
            var index = new SearchIndex();
            index.AddRange(CreateItems(ItemCount));
            SearchOptions options = new SearchOptions { MaxResults = 120 };

            for (int i = 0; i < 3; i++)
            {
                index.Search(SearchQuery.Empty, options);
            }

            const int iterations = 10;
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                index.Search(SearchQuery.Empty, options);
            }

            stopwatch.Stop();
            double average = stopwatch.Elapsed.TotalMilliseconds / iterations;
            _output.WriteLine("empty query on 100k items: avg {0:F3} ms", average);
            Assert.True(average < 500.0, string.Format(CultureInfo.InvariantCulture, "average browse search took {0:F1} ms", average));
        }

        [Fact]
        public void Search_WarmCache_DoesNotAllocatePerCandidate()
        {
            var index = new SearchIndex();
            index.AddRange(CreateItems(ItemCount));
            SearchOptions options = new SearchOptions { MaxResults = 120 };
            SearchQuery query = SearchQuery.Parse("vector");

            for (int i = 0; i < 3; i++)
            {
                index.Search(query, options);
            }

            const int iterations = 10;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                index.Search(query, options);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            long perSearch = (after - before) / iterations;
            _output.WriteLine("allocations per search (100k items, 120 results): {0:F1} KB", perSearch / 1024.0);

            // Only the result objects (at most MaxResults), their highlight ranges, the query lower-casing
            // and the bounded top-K heap may allocate: 512 KB is a very generous ceiling.
            Assert.True(perSearch < 512 * 1024, "allocation per search was " + perSearch + " bytes");
        }

        /// <summary>Builds a deterministic, realistically shaped 100k-item fixture.</summary>
        private static List<NavigationItem> CreateItems(int count)
        {
            string[] prefixes =
            {
                "Get", "Set", "Create", "Update", "Render", "Load", "Save", "Parse",
                "Build", "Destroy", "Process", "Validate", "Compute", "Find", "Apply", "Register",
            };
            string[] cores =
            {
                "Vector", "Matrix", "String", "Buffer", "Texture", "Shader", "Camera", "Material",
                "Scene", "Node", "Widget", "Layout", "Input", "Output", "Stream", "Parser",
                "Config", "Window", "Bitmap", "Renderer",
            };
            string[] suffixes =
            {
                string.Empty, "Helper", "Manager", "Factory", "Cache", "Iterator", "Context", "State", "Data", "Info",
            };

            var items = new List<NavigationItem>(count);
            for (int i = 0; i < count; i++)
            {
                string name = prefixes[i % prefixes.Length]
                    + cores[(i / 7) % cores.Length]
                    + suffixes[(i / 13) % suffixes.Length];
                if ((i % 9) == 0)
                {
                    name += (i % 100).ToString(CultureInfo.InvariantCulture);
                }

                ItemKind kind;
                switch (i % 10)
                {
                    case 0:
                    case 1:
                        kind = ItemKind.Class;
                        break;
                    case 2:
                        kind = ItemKind.Struct;
                        break;
                    case 3:
                        kind = ItemKind.Enum;
                        break;
                    case 4:
                    case 5:
                    case 6:
                        kind = ItemKind.Method;
                        break;
                    case 7:
                        kind = ItemKind.Field;
                        break;
                    case 8:
                        kind = ItemKind.Macro;
                        break;
                    default:
                        kind = ItemKind.Function;
                        break;
                }

                var item = new NavigationItem(
                    name,
                    kind,
                    @"C:\project\src\module" + (i % 37).ToString(CultureInfo.InvariantCulture)
                        + @"\file" + (i % 211).ToString(CultureInfo.InvariantCulture) + ".h",
                    (i % 4000) + 1)
                {
                    FullName = "ns" + (i % 11).ToString(CultureInfo.InvariantCulture) + "::" + name,
                    ProjectPath = @"C:\project\app.vcxproj",
                    ContainerName = "ns" + (i % 11).ToString(CultureInfo.InvariantCulture),
                };

                if ((i % 4) != 0)
                {
                    item.Flags |= ItemFlags.Public;
                }

                items.Add(item);
            }

            return items;
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using CtrlTNav.Matching;
using Xunit;

namespace CtrlTNav.Tests.Matching
{
    /// <summary>
    /// Thread-safety smoke tests: the contract is single-writer / many-reader. These tests run a writer and
    /// several readers concurrently and assert that no exception escapes and that search results stay sane.
    /// </summary>
    public sealed class SearchIndexConcurrencyTests
    {
        [Fact]
        public void ConcurrentReadersAndWriter_DoNotThrow()
        {
            var index = new SearchIndex();
            index.AddRange(CreateItems(600));

            var errors = new ConcurrentQueue<Exception>();
            using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(750)))
            {
                Task writer = Task.Run(() => RunWriter(index, stop.Token, errors));

                var readers = new Task[4];
                for (int i = 0; i < readers.Length; i++)
                {
                    int seed = i;
                    readers[i] = Task.Run(() => RunReader(index, seed, stop.Token, errors));
                }

                Task[] all = new Task[readers.Length + 1];
                all[0] = writer;
                Array.Copy(readers, 0, all, 1, readers.Length);

                Assert.True(Task.WaitAll(all, TimeSpan.FromSeconds(30)), "concurrent test workers did not finish");
            }

            Assert.Empty(errors);
            Assert.True(index.Count >= 0);
        }

        [Fact]
        public void ParallelSearches_WithNoWriter_ReturnIdenticalResults()
        {
            var index = new SearchIndex();
            index.AddRange(CreateItems(2000));

            SearchOptions options = MatchingTestFactory.NewOptions(40);
            SearchQuery query = MatchingTestFactory.Query("vec");

            string Expected()
            {
                IReadOnlyList<SearchResult> results = index.Search(query, options);
                var builder = new System.Text.StringBuilder();
                for (int i = 0; i < results.Count; i++)
                {
                    builder.Append(results[i].Item.GetIdentity()).Append('|').Append(results[i].Score).Append(';');
                }

                return builder.ToString();
            }

            string expected = Expected();
            var failures = new ConcurrentQueue<string>();

            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    IReadOnlyList<SearchResult> results = index.Search(query, options);
                    var builder = new System.Text.StringBuilder();
                    for (int i = 0; i < results.Count; i++)
                    {
                        builder.Append(results[i].Item.GetIdentity()).Append('|').Append(results[i].Score).Append(';');
                    }

                    if (!string.Equals(builder.ToString(), expected, StringComparison.Ordinal))
                    {
                        failures.Enqueue("concurrent search returned a different ranking");
                    }
                }
            });

            Assert.Empty(failures);
        }

        private static void RunWriter(SearchIndex index, CancellationToken token, ConcurrentQueue<Exception> errors)
        {
            try
            {
                int round = 0;
                while (!token.IsCancellationRequested)
                {
                    round++;
                    var dynamicItems = new[]
                    {
                        MatchingTestFactory.Symbol("Dynamic" + round, ItemKind.Method, @"C:\p\dynamic.h", round),
                        MatchingTestFactory.Symbol("DynamicHelper" + round, ItemKind.Class, @"C:\p\dynamic.h", round + 1),
                    };

                    index.AddOrUpdate(dynamicItems[0]);
                    index.AddRange(dynamicItems);
                    index.ReplaceFile(@"C:\p\file0.h", new[]
                    {
                        MatchingTestFactory.Symbol("ReplacedClass" + round, ItemKind.Class, @"C:\p\file0.h", 1),
                    });
                    index.RemoveFile(@"C:\p\file1.h");
                    index.RemoveFile(@"C:\p\file2.h");

                    if ((round % 20) == 0)
                    {
                        index.Clear();
                        index.AddRange(CreateItems(100));
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Enqueue(exception);
            }
        }

        private static void RunReader(SearchIndex index, int seed, CancellationToken token, ConcurrentQueue<Exception> errors)
        {
            string[] queries = { "item", "dyn", "class", "# item", "t item", "f:", "", "  ", "vec tor" };
            int maxResults = 25 + (seed * 10);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    for (int i = 0; i < queries.Length; i++)
                    {
                        IReadOnlyList<SearchResult> results = index.Search(
                            SearchQuery.Parse(queries[i]),
                            new SearchOptions { MaxResults = maxResults });

                        if (results == null)
                        {
                            throw new InvalidOperationException("Search returned null.");
                        }

                        for (int r = 0; r < results.Count; r++)
                        {
                            if (results[r].Item == null)
                            {
                                throw new InvalidOperationException("Search returned a result without an item.");
                            }
                        }
                    }

                    int count = index.Count;
                    if (count < 0)
                    {
                        throw new InvalidOperationException("Negative item count.");
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Enqueue(exception);
            }
        }

        private static List<NavigationItem> CreateItems(int count)
        {
            string[] cores = { "Vector", "Matrix", "String", "Buffer", "Texture", "Shader", "Camera", "Scene" };
            var items = new List<NavigationItem>(count);
            for (int i = 0; i < count; i++)
            {
                string name = "Item" + cores[i % cores.Length] + (i % 50);
                ItemKind kind = (i % 3) == 0 ? ItemKind.Class : (i % 3) == 1 ? ItemKind.Method : ItemKind.Field;
                items.Add(MatchingTestFactory.Symbol(
                    name,
                    kind,
                    @"C:\p\file" + (i % 10) + ".h",
                    i + 1,
                    fullName: "ns::" + name));
            }

            return items;
        }
    }
}

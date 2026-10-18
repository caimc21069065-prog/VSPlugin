using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CtrlTNav.Core;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Options;
using CtrlTNav.Core.Search;
using CtrlTNav.Indexing;
using CtrlTNav.Matching;

namespace CtrlTNav.Smoke
{
    /// <summary>
    /// End-to-end console harness: builds a real CppIndex over a folder or .sln, feeds a real SearchIndex,
    /// runs queries and reports timings. This is the "no Visual Studio required" proof that the indexing
    /// and search pipeline works against actual C++ code.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            var parsed = ParseArguments(args);
            if (parsed.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            var options = CtrlTNavOptions.CreateDefault();
            if (parsed.MaxFileSizeKb > 0)
            {
                options.MaxFileSizeKb = parsed.MaxFileSizeKb;
            }

            var log = new ConsoleLog();
            var index = new CppIndex(options, log);

            List<SourceFileInfo> files;
            var sw = Stopwatch.StartNew();
            if (parsed.Target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                files = SolutionFileParser.ParseSolution(parsed.Target).ToList();
                Console.WriteLine("Solution: " + parsed.Target);
            }
            else
            {
                files = SourceFileScanner.ScanDirectory(parsed.Target, options).ToList();
                Console.WriteLine("Directory: " + parsed.Target);
            }

            Console.WriteLine("Discovered " + files.Count + " candidate file(s) in " + sw.ElapsedMilliseconds + " ms");

            sw.Restart();
            index.BuildAsync(files).GetAwaiter().GetResult();
            sw.Stop();
            Console.WriteLine("Indexed " + index.FileCount + " file(s), " + index.ItemCount + " item(s) in "
                              + sw.ElapsedMilliseconds + " ms");

            var items = index.Snapshot();
            if (items.Count == 0)
            {
                Console.Error.WriteLine("Nothing indexed - check the path / extensions.");
                return 2;
            }

            var searchIndex = new SearchIndex();
            searchIndex.AddRange(items);
            var recent = new RecentUsageTracker(Path.Combine(Path.GetTempPath(), "ctrltnav-smoke-recent.dat"), 100);
            searchIndex.RecentUsage = recent;

            var searchOptions = options.ToSearchOptions(parsed.CurrentProjectPath);
            Console.WriteLine("Search index: " + searchIndex.Count + " item(s)");

            if (parsed.Queries.Count == 0)
            {
                foreach (var query in PickInterestingQueries(items))
                {
                    PrintResults(searchIndex, searchOptions, query);
                }
            }
            else
            {
                foreach (var query in parsed.Queries)
                {
                    PrintResults(searchIndex, searchOptions, query);
                }
            }

            if (parsed.Benchmark)
            {
                RunBenchmark(searchIndex, searchOptions);
                RunRecentUsageDemo(searchIndex, searchOptions);
            }

            index.Dispose();
            return 0;
        }

        private static IEnumerable<string> PickInterestingQueries(IReadOnlyList<NavigationItem> items)
        {
            var queries = new List<string> { "t ", "f ", "# " };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item.Kind == ItemKind.Method || item.Kind == ItemKind.Function)
                {
                    string name = item.Name;
                    if (name.Length > 4 && seen.Add(name.Substring(0, Math.Min(6, name.Length))))
                    {
                        queries.Add(name.Substring(0, Math.Min(6, name.Length)));
                    }
                }

                if (queries.Count >= 9)
                {
                    break;
                }
            }

            return queries;
        }

        private static void PrintResults(SearchIndex index, SearchOptions options, string query)
        {
            var sw = Stopwatch.StartNew();
            var results = index.Search(SearchQuery.Parse(query), options);
            sw.Stop();
            Console.WriteLine();
            Console.WriteLine("Query \"" + query + "\" -> " + results.Count + " result(s), " + sw.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
            int rank = 1;
            foreach (var result in results.Take(5))
            {
                Console.WriteLine("  " + rank.ToString().PadLeft(2) + ". [" + result.Score.ToString().PadLeft(5) + "] "
                                  + result.MatchKind.ToString().PadRight(11) + " "
                                  + result.Item.Kind.GetShortCode().PadRight(3)
                                  + Truncate(result.Item.FullName.Length > 0 ? result.Item.FullName : result.Item.Name, 40).PadRight(42)
                                  + " " + Path.GetFileName(result.Item.FilePath) + ":" + result.Item.Line);
                rank++;
            }
        }

        private static string Truncate(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        }

        private static void RunBenchmark(SearchIndex index, SearchOptions options)
        {
            Console.WriteLine();
            Console.WriteLine("=== Benchmark ===");
            string[] queries = { "get", "push", "std", "vec", "config", "render", "test", "init", "f " };
            const int iterations = 50;
            foreach (var query in queries)
            {
                var parsed = SearchQuery.Parse(query);
                index.Search(parsed, options); // warm up
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                {
                    index.Search(parsed, options);
                }

                sw.Stop();
                Console.WriteLine("  \"" + query + "\": " + (sw.Elapsed.TotalMilliseconds / iterations).ToString("F2")
                                  + " ms/query over " + index.Count + " items");
            }
        }

        private static void RunRecentUsageDemo(SearchIndex index, SearchOptions options)
        {
            Console.WriteLine();
            Console.WriteLine("=== Recent usage weighting demo ===");
            const string query = "a";
            var before = index.Search(SearchQuery.Parse(query), options).Take(3).Select(r => r.Item.Name).ToArray();
            Console.WriteLine("  before: " + string.Join(", ", before));
            var picks = index.Search(SearchQuery.Parse(query), options).Skip(4).Take(2).Select(r => r.Item).ToArray();
            foreach (var pick in picks)
            {
                if (index.RecentUsage != null)
                {
                    index.RecentUsage.RecordUsage(pick);
                }
            }

            var after = index.Search(SearchQuery.Parse(query), options).Take(3).Select(r => r.Item.Name).ToArray();
            Console.WriteLine("  after : " + string.Join(", ", after));
            Console.WriteLine("  (used: " + string.Join(", ", picks.Select(p => p.Name)) + ")");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("CtrlTNav.Smoke - end-to-end harness for the Ctrl+T Navigator engine");
            Console.WriteLine();
            Console.WriteLine("Usage: CtrlTNav.Smoke <directory|solution.sln> [queries...] [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --benchmark            run search latency benchmark");
            Console.WriteLine("  --project <path>       current project path used for the current-project boost");
            Console.WriteLine("  --max-file-size-kb N   skip files larger than N KB");
            Console.WriteLine("  --help                 show this help");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  CtrlTNav.Smoke ..\\tests\\CtrlTNav.Tests\\TestData \"t \" \"f \" \"push\" --benchmark");
        }

        private static ParsedArguments ParseArguments(string[] args)
        {
            var result = new ParsedArguments();
            if (args.Length == 0)
            {
                result.ShowHelp = true;
                return result;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--help":
                    case "-h":
                        result.ShowHelp = true;
                        return result;
                    case "--benchmark":
                        result.Benchmark = true;
                        continue;
                    case "--project":
                        if (i + 1 < args.Length)
                        {
                            result.CurrentProjectPath = args[++i];
                        }

                        continue;
                    case "--max-file-size-kb":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int kb))
                        {
                            result.MaxFileSizeKb = kb;
                        }

                        continue;
                }

                if (result.Target == null)
                {
                    result.Target = Path.GetFullPath(arg);
                }
                else
                {
                    result.Queries.Add(arg);
                }
            }

            if (result.Target == null)
            {
                result.ShowHelp = true;
            }

            return result;
        }

        private sealed class ParsedArguments
        {
            public string? Target { get; set; }

            public List<string> Queries { get; } = new List<string>();

            public bool Benchmark { get; set; }

            public bool ShowHelp { get; set; }

            public string? CurrentProjectPath { get; set; }

            public int MaxFileSizeKb { get; set; }
        }

        private sealed class ConsoleLog : ILog
        {
            public void Info(string message)
            {
                Console.Error.WriteLine("[info] " + message);
            }

            public void Warn(string message)
            {
                Console.Error.WriteLine("[warn] " + message);
            }

            public void Error(string message, Exception? exception = null)
            {
                Console.Error.WriteLine("[error] " + message + (exception != null ? " " + exception.Message : string.Empty));
            }
        }
    }
}

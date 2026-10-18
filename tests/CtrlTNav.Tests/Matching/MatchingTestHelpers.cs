using System;
using System.Collections.Generic;
using System.IO;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using CtrlTNav.Matching;

namespace CtrlTNav.Tests.Matching
{
    /// <summary>Small factories shared by the matching test classes.</summary>
    internal static class MatchingTestFactory
    {
        internal static NavigationItem Symbol(
            string name,
            ItemKind kind = ItemKind.Class,
            string filePath = @"C:\src\Project\Types.h",
            int line = 1,
            string fullName = "",
            string projectPath = "")
        {
            return new NavigationItem(name, kind, filePath, line)
            {
                FullName = fullName,
                ProjectPath = projectPath,
            };
        }

        internal static NavigationItem PublicSymbol(
            string name,
            ItemKind kind = ItemKind.Class,
            string filePath = @"C:\src\Project\Types.h",
            int line = 1,
            string fullName = "",
            string projectPath = "")
        {
            NavigationItem item = Symbol(name, kind, filePath, line, fullName, projectPath);
            item.Flags |= ItemFlags.Public;
            return item;
        }

        internal static NavigationItem FileItem(string filePath)
        {
            return new NavigationItem(Path.GetFileName(filePath), ItemKind.File, filePath);
        }

        internal static SearchIndex CreateIndex(params NavigationItem[] items)
        {
            var index = new SearchIndex();
            index.AddRange(items);
            return index;
        }

        internal static SearchQuery Query(string text)
        {
            return SearchQuery.Parse(text);
        }

        internal static SearchOptions NewOptions(int maxResults = 100)
        {
            return new SearchOptions { MaxResults = maxResults };
        }

        internal static IReadOnlyList<string> Names(IReadOnlyList<SearchResult> results)
        {
            var names = new List<string>(results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                names.Add(results[i].Item.Name);
            }

            return names;
        }

        internal static List<MatchKind> Kinds(IReadOnlyList<SearchResult> results)
        {
            var kinds = new List<MatchKind>(results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                kinds.Add(results[i].MatchKind);
            }

            return kinds;
        }
    }

    /// <summary>Captures log output so tests can verify the engines never silently swallow unexpected errors.</summary>
    internal sealed class RecordingLog : ILog
    {
        internal List<string> Errors { get; } = new List<string>();

        internal List<string> Warnings { get; } = new List<string>();

        internal List<string> Infos { get; } = new List<string>();

        public void Info(string message)
        {
            Infos.Add(message);
        }

        public void Warn(string message)
        {
            Warnings.Add(message);
        }

        public void Error(string message, Exception? exception = null)
        {
            Errors.Add(message + (exception == null ? string.Empty : " | " + exception.Message));
        }
    }

    /// <summary>Creates a unique temporary directory that is deleted on dispose.</summary>
    internal sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ctrltnav-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string File(string fileName)
        {
            return System.IO.Path.Combine(Path, fileName);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch (Exception)
            {
                // best effort cleanup; a leftover temp directory does not fail the test run
            }
        }
    }
}

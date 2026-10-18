using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CtrlTNav.Core.Model;
using CtrlTNav.Indexing;

namespace CtrlTNav.Tests.Indexing
{
    /// <summary>Shared helpers for the indexing test suite.</summary>
    internal static class IndexingTestData
    {
        public static string Root
        {
            get { return Path.Combine(AppContext.BaseDirectory, "TestData", "indexing"); }
        }

        public static string PathOf(string relativePath)
        {
            return Path.Combine(Root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        }

        public static string Read(string relativePath)
        {
            return File.ReadAllText(PathOf(relativePath));
        }

        /// <summary>Extracts symbols from a test-data file.</summary>
        public static IReadOnlyList<NavigationItem> Extract(string relativePath)
        {
            string fullPath = PathOf(relativePath);
            string content = File.ReadAllText(fullPath);
            return new CppSymbolExtractor().Extract(
                fullPath,
                content,
                "TestProject",
                PathOf("TestProject.vcxproj"),
                fullPath.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ? "c" : "cpp");
        }

        /// <summary>Independent 1-based line/column lookup used to validate extraction positions.</summary>
        public static KeyValuePair<int, int> PositionOf(string content, string token, int occurrence = 0)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            int index = -1;
            int from = 0;
            for (int attempt = 0; attempt <= occurrence; attempt++)
            {
                index = content.IndexOf(token, from, StringComparison.Ordinal);
                if (index < 0)
                {
                    throw new InvalidOperationException("Token not found: " + token + " (occurrence " + occurrence + ")");
                }

                from = index + 1;
            }

            int line = 1;
            int column = 1;
            for (int position = 0; position < index; position++)
            {
                char c = content[position];
                if (c == '\n')
                {
                    line++;
                    column = 1;
                }
                else if (c == '\r')
                {
                    if (position + 1 < content.Length && content[position + 1] == '\n')
                    {
                        continue;
                    }

                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new KeyValuePair<int, int>(line, column);
        }

        public static NavigationItem? Find(IReadOnlyList<NavigationItem> items, string name, ItemKind? kind = null)
        {
            for (int index = 0; index < items.Count; index++)
            {
                NavigationItem item = items[index];
                if (!string.Equals(item.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (kind.HasValue && item.Kind != kind.Value)
                {
                    continue;
                }

                return item;
            }

            return null;
        }

        public static NavigationItem Require(IReadOnlyList<NavigationItem> items, string name, ItemKind? kind = null)
        {
            NavigationItem? item = Find(items, name, kind);
            if (item == null)
            {
                throw new InvalidOperationException(
                    "Symbol not found: " + (kind.HasValue ? kind.Value + " " : string.Empty) + name +
                    Environment.NewLine + Describe(items));
            }

            return item;
        }

        /// <summary>Case-insensitive suffix assertion (kept local so the tests do not depend on newer xUnit overloads).</summary>
        public static void AssertEndsWith(string expectedSuffix, string actual)
        {
            if (actual == null || !actual.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Expected '" + actual + "' to end with '" + expectedSuffix + "'.");
            }
        }

        /// <summary>Case-insensitive substring-absence assertion.</summary>
        public static void AssertDoesNotContain(string unexpectedSubstring, string actual)
        {
            if (actual != null && actual.IndexOf(unexpectedSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException(
                    "Expected '" + actual + "' not to contain '" + unexpectedSubstring + "'.");
            }
        }

        /// <summary>
        /// Asserts that the symbol exists with the given kind and that its line/column point at the name
        /// token; the expected position is computed independently from <paramref name="prefix"/> + name.
        /// </summary>
        public static NavigationItem AssertSymbol(
            IReadOnlyList<NavigationItem> items,
            string content,
            string prefix,
            string name,
            ItemKind kind,
            string? expectedContainer = null,
            ItemFlags requiredFlags = ItemFlags.None)
        {
            NavigationItem item = Require(items, name, kind);
            string token = prefix + name;
            KeyValuePair<int, int> position = PositionOf(content, token);
            int expectedColumn = position.Value + prefix.Length;

            // Overloads/duplicate declarations (in-class declaration + out-of-class definition) share a
            // name and kind; pick the one whose position matches the token the expectation refers to.
            for (int index = 0; index < items.Count; index++)
            {
                NavigationItem candidate = items[index];
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal) && candidate.Kind == kind &&
                    candidate.Line == position.Key && candidate.Column == expectedColumn)
                {
                    item = candidate;
                    break;
                }
            }

            if (item.Line != position.Key)
            {
                throw new InvalidOperationException(
                    "Wrong line for " + kind + " " + name + ": expected " + position.Key + " but was " + item.Line +
                    " (" + Describe(items) + ")");
            }

            if (item.Column != expectedColumn)
            {
                throw new InvalidOperationException(
                    "Wrong column for " + kind + " " + name + ": expected " + expectedColumn + " but was " +
                    item.Column + " (" + Describe(items) + ")");
            }

            if (expectedContainer != null && !string.Equals(item.ContainerName, expectedContainer, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Wrong container for " + name + ": expected '" + expectedContainer + "' but was '" +
                    item.ContainerName + "'");
            }

            if (requiredFlags != ItemFlags.None && (item.Flags & requiredFlags) != requiredFlags)
            {
                throw new InvalidOperationException(
                    "Missing flags " + requiredFlags + " on " + name + "; actual flags: " + item.Flags);
            }

            return item;
        }

        public static string Describe(IReadOnlyList<NavigationItem> items)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Extracted " + items.Count + " symbols:");
            for (int index = 0; index < items.Count; index++)
            {
                NavigationItem item = items[index];
                builder.Append("  ").Append(item.Line).Append(':').Append(item.Column).Append(' ')
                    .Append(item.Kind).Append(' ').Append(item.FullName).Append(" [").Append(item.Flags).Append(']')
                    .AppendLine();
            }

            return builder.ToString();
        }

        /// <summary>Asserts that the extracted (kind, name) multiset is exactly the expected one.</summary>
        public static void AssertExactSymbols(
            IReadOnlyList<NavigationItem> items,
            string content,
            params ExpectedSymbol[] expected)
        {
            var actual = new List<string>(items.Count);
            for (int index = 0; index < items.Count; index++)
            {
                actual.Add(((int)items[index].Kind) + "|" + items[index].Name);
            }

            var wanted = new List<string>(expected.Length);
            for (int index = 0; index < expected.Length; index++)
            {
                wanted.Add(((int)expected[index].Kind) + "|" + expected[index].Name);
            }

            actual.Sort(StringComparer.Ordinal);
            wanted.Sort(StringComparer.Ordinal);
            string actualText = string.Join("\n", actual);
            string wantedText = string.Join("\n", wanted);
            if (!string.Equals(actualText, wantedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Symbol set mismatch." + Environment.NewLine +
                    "Expected:" + Environment.NewLine + wantedText + Environment.NewLine +
                    "Actual:" + Environment.NewLine + actualText + Environment.NewLine +
                    Describe(items));
            }

            for (int index = 0; index < expected.Length; index++)
            {
                ExpectedSymbol symbol = expected[index];
                if (symbol.SkipPositionCheck)
                {
                    if (symbol.Container != null || symbol.RequiredFlags != ItemFlags.None)
                    {
                        NavigationItem item = Require(items, symbol.Name, symbol.Kind);
                        if (symbol.Container != null &&
                            !string.Equals(item.ContainerName, symbol.Container, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "Wrong container for " + symbol.Name + ": expected '" + symbol.Container +
                                "' but was '" + item.ContainerName + "'");
                        }

                        if (symbol.RequiredFlags != ItemFlags.None &&
                            (item.Flags & symbol.RequiredFlags) != symbol.RequiredFlags)
                        {
                            throw new InvalidOperationException(
                                "Missing flags " + symbol.RequiredFlags + " on " + symbol.Name + "; actual: " +
                                item.Flags);
                        }
                    }

                    continue;
                }

                AssertSymbol(
                    items,
                    content,
                    symbol.Prefix,
                    symbol.Name,
                    symbol.Kind,
                    symbol.Container,
                    symbol.RequiredFlags);
            }
        }
    }

    internal sealed class ExpectedSymbol
    {
        public ExpectedSymbol(
            string prefix,
            string name,
            ItemKind kind,
            string? container = null,
            ItemFlags requiredFlags = ItemFlags.None,
            bool skipPositionCheck = false)
        {
            Prefix = prefix;
            Name = name;
            Kind = kind;
            Container = container;
            RequiredFlags = requiredFlags;
            SkipPositionCheck = skipPositionCheck;
        }

        public string Prefix { get; }

        public string Name { get; }

        public ItemKind Kind { get; }

        public string? Container { get; }

        public ItemFlags RequiredFlags { get; }

        public bool SkipPositionCheck { get; }
    }
}

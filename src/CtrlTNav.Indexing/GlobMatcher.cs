using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CtrlTNav.Indexing
{
    /// <summary>
    /// Minimal glob matcher used for the index exclude patterns.
    /// Supports <c>**</c> (any number of path segments), <c>*</c> (within one segment) and <c>?</c>
    /// (single non-separator character). Matching is case-insensitive because the extension targets Windows.
    /// </summary>
    public static class GlobMatcher
    {
        private const int MaxCachedPatterns = 512;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, Regex> Cache = new Dictionary<string, Regex>(StringComparer.Ordinal);

        /// <summary>
        /// True when the normalized path matches the glob pattern. A pattern that does not start with
        /// <c>**</c>/a root/separator is implicitly anchored at any path depth (so <c>build/**</c> also
        /// matches <c>C:\src\build\x.cpp</c>).
        /// </summary>
        public static bool IsMatch(string path, string pattern)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            Regex? regex = GetRegex(pattern);
            if (regex == null)
            {
                return false;
            }

            string normalizedPath = NormalizePath(path);
            try
            {
                return regex.IsMatch(normalizedPath);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>True when the path matches at least one exclude pattern.</summary>
        public static bool IsExcluded(string path, string[] patterns)
        {
            if (string.IsNullOrEmpty(path) || patterns == null || patterns.Length == 0)
            {
                return false;
            }

            for (int index = 0; index < patterns.Length; index++)
            {
                string pattern = patterns[index];
                if (string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                if (IsMatch(path, pattern.Trim()))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/');
        }

        private static Regex? GetRegex(string pattern)
        {
            lock (CacheLock)
            {
                Regex? cached;
                if (Cache.TryGetValue(pattern, out cached))
                {
                    return cached;
                }
            }

            Regex regex;
            try
            {
                regex = new Regex(
                    Translate(pattern),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                return null;
            }

            lock (CacheLock)
            {
                if (Cache.Count >= MaxCachedPatterns)
                {
                    Cache.Clear();
                }

                Cache[pattern] = regex;
            }

            return regex;
        }

        /// <summary>Translates a glob into an anchored regular expression.</summary>
        internal static string Translate(string pattern)
        {
            string normalized = pattern.Trim().Replace('\\', '/');
            if (normalized.Length == 0)
            {
                return "^$";
            }

            bool anchoredAtRoot = normalized[0] == '/' ||
                                  (normalized.Length > 1 && normalized[1] == ':') ||
                                  normalized.StartsWith("**", StringComparison.Ordinal);

            var builder = new StringBuilder(normalized.Length * 2 + 8);
            builder.Append('^');
            if (!anchoredAtRoot)
            {
                builder.Append("(?:.*/)?");
            }

            int index = 0;
            while (index < normalized.Length)
            {
                char c = normalized[index];
                if (c == '*')
                {
                    if (index + 1 < normalized.Length && normalized[index + 1] == '*')
                    {
                        index += 2;
                        // Collapse "**/" so it can also match zero path segments.
                        if (index < normalized.Length && normalized[index] == '/')
                        {
                            index++;
                            builder.Append("(?:.*/)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }

                        continue;
                    }

                    builder.Append("[^/]*");
                    index++;
                    continue;
                }

                if (c == '?')
                {
                    builder.Append("[^/]");
                    index++;
                    continue;
                }

                builder.Append(Regex.Escape(c.ToString()));
                index++;
            }

            builder.Append('$');
            return builder.ToString();
        }
    }
}

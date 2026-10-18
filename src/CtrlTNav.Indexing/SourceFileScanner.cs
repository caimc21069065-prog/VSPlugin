using System;
using System.Collections.Generic;
using System.IO;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Options;

namespace CtrlTNav.Indexing
{
    /// <summary>
    /// Enumerates C/C++ files on disk (used for include directories and loose-file indexing).
    /// Directory enumeration is resilient to access errors, long paths and symlink/junction loops.
    /// </summary>
    public static class SourceFileScanner
    {
        private const int MaxDirectories = 100000;
        private const int MaxFiles = 500000;
        private const int MaxDepth = 64;

        /// <summary>
        /// Recursively scans <paramref name="rootDirectory"/> and returns the supported, non-excluded files.
        /// The result is sorted by path so repeated scans are deterministic.
        /// </summary>
        public static IReadOnlyList<SourceFileInfo> ScanDirectory(string rootDirectory, CtrlTNavOptions options)
        {
            var result = new List<SourceFileInfo>();
            if (string.IsNullOrEmpty(rootDirectory))
            {
                return result;
            }

            CtrlTNavOptions effective = (options ?? CtrlTNavOptions.CreateDefault()).Normalize();
            string[] extensions = CppFileTypes.NormalizeExtensions(effective.GetSourceExtensions());
            string[] excludes = effective.GetExcludePatterns();

            string root;
            try
            {
                root = Path.GetFullPath(rootDirectory);
            }
            catch
            {
                return result;
            }

            if (!Directory.Exists(root))
            {
                return result;
            }

            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));
            int directoryCount = 0;

            while (stack.Count > 0 && result.Count < MaxFiles)
            {
                KeyValuePair<string, int> current = stack.Pop();
                string directory = current.Key;
                int depth = current.Value;
                directoryCount++;
                if (directoryCount > MaxDirectories)
                {
                    break;
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(directory);
                }
                catch
                {
                    continue;
                }

                for (int index = 0; index < files.Length; index++)
                {
                    string file = files[index];
                    // Excludes are matched relative to the scan root: patterns such as "**/bin/**"
                    // must not accidentally exclude the root because of its absolute location.
                    if (!IsSupportedFile(ToRelativePath(root, file), extensions, excludes))
                    {
                        continue;
                    }

                    result.Add(new SourceFileInfo(
                        file,
                        string.Empty,
                        string.Empty,
                        CppFileTypes.GetLanguage(file)));

                    if (result.Count >= MaxFiles)
                    {
                        break;
                    }
                }

                if (result.Count >= MaxFiles || depth >= MaxDepth)
                {
                    continue;
                }

                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(directory);
                }
                catch
                {
                    continue;
                }

                for (int index = 0; index < directories.Length; index++)
                {
                    string child = directories[index];
                    string relativeChild = ToRelativePath(root, child);
                    // A trailing separator lets patterns such as "**/build/**" exclude the directory itself.
                    if (GlobMatcher.IsExcluded(relativeChild + Path.DirectorySeparatorChar, excludes))
                    {
                        continue;
                    }

                    stack.Push(new KeyValuePair<string, int>(child, depth + 1));
                }
            }

            result.Sort((left, right) => string.Compare(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// True when <paramref name="path"/> has a configured C/C++ extension and is not excluded by
        /// the option's glob patterns.
        /// </summary>
        public static bool IsSupportedFile(string path, CtrlTNavOptions options)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            CtrlTNavOptions effective = (options ?? CtrlTNavOptions.CreateDefault()).Normalize();
            return IsSupportedFile(
                path,
                CppFileTypes.NormalizeExtensions(effective.GetSourceExtensions()),
                effective.GetExcludePatterns());
        }

        internal static bool IsSupportedFile(string path, string[] normalizedExtensions, string[] excludePatterns)
        {
            if (string.IsNullOrEmpty(path) || normalizedExtensions == null)
            {
                return false;
            }

            if (!CppFileTypes.IsSupportedExtension(path, normalizedExtensions))
            {
                return false;
            }

            if (excludePatterns != null && excludePatterns.Length > 0 && GlobMatcher.IsExcluded(path, excludePatterns))
            {
                return false;
            }

            return true;
        }

        private static string ToRelativePath(string root, string path)
        {
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                            root.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(prefix.Length);
            }

            return path;
        }
    }
}

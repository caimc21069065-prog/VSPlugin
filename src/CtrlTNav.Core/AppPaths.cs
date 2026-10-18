using System;
using System.Collections.Generic;
using System.IO;

namespace CtrlTNav.Core
{
    /// <summary>Well known locations used by the extension for its local state.</summary>
    public static class AppPaths
    {
        public const string ProductFolderName = "CtrlTNav";

        /// <summary>Returns <c>%LOCALAPPDATA%\CtrlTNav</c> and ensures it exists.</summary>
        public static string GetDataDirectory()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.GetTempPath();
            }

            string dir = Path.Combine(baseDir, ProductFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetRecentUsageFilePath()
        {
            return Path.Combine(GetDataDirectory(), "recent-usage.dat");
        }

        public static string GetLogFilePath()
        {
            return Path.Combine(GetDataDirectory(), "ctrltnav.log");
        }

        /// <summary>Cache file path for a solution; each solution gets its own cache file.</summary>
        public static string GetIndexCacheFilePath(string? solutionPath)
        {
            string key = "nosolution";
            if (!string.IsNullOrEmpty(solutionPath))
            {
                string name = Path.GetFileNameWithoutExtension(solutionPath) ?? "solution";
                key = SanitizeFileName(name) + "-" + StableHash(solutionPath);
            }

            return Path.Combine(GetDataDirectory(), "index-" + key + ".cache");
        }

        public static string GetIndexCacheDirectory()
        {
            string dir = Path.Combine(GetDataDirectory(), "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }

            return new string(chars);
        }

        /// <summary>Stable (process independent) FNV-1a hash used for cache file names.</summary>
        public static string StableHash(string text)
        {
            uint hash = 2166136261;
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= char.ToLowerInvariant(text[i]);
                hash *= 16777619;
            }

            return hash.ToString("x8");
        }
    }
}

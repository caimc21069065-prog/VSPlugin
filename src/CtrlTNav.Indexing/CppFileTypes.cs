using System;
using System.IO;

namespace CtrlTNav.Indexing
{
    /// <summary>
    /// Shared knowledge about which file extensions are C/C++ sources or headers.
    /// Kept internal because consumers pass <c>CtrlTNavOptions</c> extension lists.
    /// </summary>
    internal static class CppFileTypes
    {
        /// <summary>Header extensions used to set <c>ItemFlags.HeaderFile</c>.</summary>
        public static readonly string[] HeaderExtensions =
        {
            ".h", ".hpp", ".hxx", ".hh", ".h++", ".inl", ".ipp", ".tpp", ".tcc", ".cuh", ".ixx", ".cppm",
        };

        /// <summary>Default set of C/C++ extensions understood by the solution parser and directory scanner.</summary>
        public static readonly string[] DefaultExtensions =
        {
            ".h", ".hpp", ".hxx", ".hh", ".h++", ".inl", ".ipp", ".tpp", ".tcc", ".cuh", ".ixx", ".cppm",
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".m", ".mm",
        };

        public static string GetExtension(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetExtension(path) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        public static bool IsHeaderPath(string? path)
        {
            string extension = GetExtension(path);
            for (int i = 0; i < HeaderExtensions.Length; i++)
            {
                if (string.Equals(HeaderExtensions[i], extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Source language tag used by <c>NavigationItem.Language</c>.</summary>
        public static string GetLanguage(string? path)
        {
            string extension = GetExtension(path);
            if (string.Equals(extension, ".c", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".m", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".mm", StringComparison.OrdinalIgnoreCase))
            {
                return "c";
            }

            return "cpp";
        }

        public static bool IsSupportedExtension(string? path, string[] normalizedExtensions)
        {
            string extension = GetExtension(path);
            if (extension.Length == 0 || normalizedExtensions == null)
            {
                return false;
            }

            for (int i = 0; i < normalizedExtensions.Length; i++)
            {
                if (string.Equals(normalizedExtensions[i], extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static string[] NormalizeExtensions(string[]? extensions)
        {
            if (extensions == null || extensions.Length == 0)
            {
                return DefaultExtensions;
            }

            var result = new string[extensions.Length];
            int count = 0;
            for (int i = 0; i < extensions.Length; i++)
            {
                string extension = extensions[i];
                if (string.IsNullOrEmpty(extension))
                {
                    continue;
                }

                extension = extension.Trim();
                if (extension.Length == 0)
                {
                    continue;
                }

                if (extension[0] != '.')
                {
                    extension = "." + extension;
                }

                result[count++] = extension.ToLowerInvariant();
            }

            if (count == 0)
            {
                return DefaultExtensions;
            }

            if (count == result.Length)
            {
                return result;
            }

            var trimmed = new string[count];
            Array.Copy(result, trimmed, count);
            return trimmed;
        }
    }
}

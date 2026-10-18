using System;
using System.Collections.Generic;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;

namespace CtrlTNav.Core.Options
{
    /// <summary>
    /// Persisted settings of the extension. This is a plain POCO so that both the Visual Studio options
    /// page and the indexer/search pipeline can consume it without referencing Visual Studio assemblies.
    /// </summary>
    public sealed class CtrlTNavOptions
    {
        /// <summary>When true the extension binds Ctrl+T and replaces the native Go To All shortcut.</summary>
        public bool OverrideCtrlT { get; set; } = true;

        /// <summary>When true Ctrl+, opens the search window (native Ctrl+, opens Tools/Options).</summary>
        public bool UseCtrlCommaShortcut { get; set; } = true;

        /// <summary>Build the index in the background as soon as a solution is opened.</summary>
        public bool AutoStartIndexing { get; set; } = true;

        /// <summary>Index project files (true) or only files opened in the editor (false).</summary>
        public bool IndexSolutionFiles { get; set; } = true;

        /// <summary>Index C and C++ sources and headers.</summary>
        public bool IndexCppFiles { get; set; } = true;

        /// <summary>Also index headers that are only reachable through include directories.</summary>
        public bool IndexIncludeDirectories { get; set; } = false;

        /// <summary>Semicolon separated list of additional directories to scan for headers.</summary>
        public string AdditionalIncludeDirectories { get; set; } = string.Empty;

        /// <summary>Semicolon separated list of glob patterns excluded from indexing.</summary>
        public string ExcludePatterns { get; set; } = DefaultExcludePatterns;

        /// <summary>Semicolon separated list of file extensions that are treated as C/C++ sources.</summary>
        public string SourceExtensions { get; set; } = DefaultSourceExtensions;

        /// <summary>Files larger than this are skipped (generated/third-party blobs). 0 disables the limit.</summary>
        public int MaxFileSizeKb { get; set; } = 4096;

        /// <summary>Maximum number of search results shown.</summary>
        public int MaxResults { get; set; } = 120;

        /// <summary>Remember recently used symbols/files and rank them higher.</summary>
        public bool EnableRecentUsage { get; set; } = true;

        /// <summary>Number of recent entries kept.</summary>
        public int RecentUsageCapacity { get; set; } = 300;

        /// <summary>Show paths relative to the solution directory.</summary>
        public bool ShowRelativePaths { get; set; } = true;

        /// <summary>Show line numbers in the result list.</summary>
        public bool ShowLineNumbers { get; set; } = true;

        /// <summary>Close the search window when it loses focus.</summary>
        public bool CloseOnFocusLost { get; set; } = true;

        /// <summary>Show the splash of files/symbol counts in the footer.</summary>
        public bool ShowIndexStatus { get; set; } = true;

        public const string DefaultExcludePatterns = "**/build/**;**/out/**;**/bin/**;**/obj/**;**/.git/**;**/.vs/**;**/Generated/**;**/*.g.cs";

        public const string DefaultSourceExtensions = ".h;.hpp;.hxx;.hh;.h++;.inl;.ipp;.tpp;.tcc;.cuh;.c;.cc;.cpp;.cxx;.c++;.m;.mm;.ixx;.cppm";

        /// <summary>Clamps values into supported ranges.</summary>
        public CtrlTNavOptions Normalize()
        {
            CtrlTNavOptions clone = Clone();
            if (clone.MaxResults < 10)
            {
                clone.MaxResults = 10;
            }

            if (clone.MaxResults > 1000)
            {
                clone.MaxResults = 1000;
            }

            if (clone.MaxFileSizeKb < 0)
            {
                clone.MaxFileSizeKb = 0;
            }

            if (clone.RecentUsageCapacity < 10)
            {
                clone.RecentUsageCapacity = 10;
            }

            if (clone.RecentUsageCapacity > 5000)
            {
                clone.RecentUsageCapacity = 5000;
            }

            if (string.IsNullOrWhiteSpace(clone.SourceExtensions))
            {
                clone.SourceExtensions = DefaultSourceExtensions;
            }

            return clone;
        }

        public CtrlTNavOptions Clone()
        {
            return (CtrlTNavOptions)MemberwiseClone();
        }

        public static CtrlTNavOptions CreateDefault()
        {
            return new CtrlTNavOptions();
        }

        /// <summary>Splits a semicolon/newline separated option value into trimmed, non-empty entries.</summary>
        public static string[] SplitList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            var parts = value!.Split(new[] { ';', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length > 0)
                {
                    result.Add(part);
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Exclude globs applied to the index: the built-in defaults always apply, plus any patterns the
        /// user configured. This keeps generated/build folders out even when the list was customised.
        /// </summary>
        public string[] GetExcludePatterns()
        {
            string[] defaults = SplitList(DefaultExcludePatterns);
            string[] configured = SplitList(ExcludePatterns);
            if (configured.Length == 0)
            {
                return defaults;
            }

            var result = new List<string>(defaults.Length + configured.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < defaults.Length; index++)
            {
                if (seen.Add(defaults[index]))
                {
                    result.Add(defaults[index]);
                }
            }

            for (int index = 0; index < configured.Length; index++)
            {
                if (seen.Add(configured[index]))
                {
                    result.Add(configured[index]);
                }
            }

            return result.ToArray();
        }

        public string[] GetAdditionalIncludeDirectories()
        {
            return SplitList(AdditionalIncludeDirectories);
        }

        public string[] GetSourceExtensions()
        {
            string[] raw = SplitList(SourceExtensions);
            var result = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string extension = raw[i];
                if (!extension.StartsWith(".", StringComparison.Ordinal))
                {
                    extension = "." + extension;
                }

                result.Add(extension.ToLowerInvariant());
            }

            return result.ToArray();
        }

        /// <summary>Builds the search options snapshot for a query.</summary>
        public SearchOptions ToSearchOptions(string? currentProjectPath)
        {
            return new SearchOptions
            {
                MaxResults = MaxResults,
                IncludeFiles = IndexCppFiles,
                IncludeSymbols = true,
                BoostRecentlyUsed = EnableRecentUsage,
                BoostCurrentProject = true,
                BoostPublicSymbols = true,
                CurrentProjectPath = currentProjectPath,
            };
        }
    }
}

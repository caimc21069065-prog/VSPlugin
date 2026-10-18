using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CtrlTNav.Core.Options;
using Microsoft.VisualStudio.Shell;

namespace CtrlTNav.Vsix.Options
{
    /// <summary>
    /// Tools &gt; Options page for the extension. The page exposes every setting of
    /// <see cref="CtrlTNavOptions"/> with a category, display name, description and default value so that
    /// Visual Studio can persist it in the user's settings store and offer "Reset to default".
    /// </summary>
    /// <remarks>
    /// Visual Studio creates and persists this class; the package exposes the current state through
    /// <see cref="OptionsProvider"/>. When the user presses OK, <see cref="OnApply"/> converts the page
    /// into a fresh <see cref="CtrlTNavOptions"/> snapshot and raises <see cref="OptionsApplied"/>.
    /// </remarks>
    [Guid(CtrlTNavConstants.OptionsPageGuidString)]
    public sealed class CtrlTNavOptionsPage : DialogPage
    {
        internal const string ShortcutsCategory = "1. Shortcuts";
        internal const string IndexingCategory = "2. Indexing";
        internal const string SearchCategory = "3. Search and Results";
        internal const string DisplayCategory = "4. Display";

        /// <summary>Raised after the user applied changes with OK; the argument is a normalized snapshot.</summary>
        internal event EventHandler<CtrlTNavOptions>? OptionsApplied;

        // ---------------------------------------------------------------------------------------------
        // Shortcuts
        // ---------------------------------------------------------------------------------------------

        [Category(ShortcutsCategory)]
        [DisplayName("Override Ctrl+T")]
        [Description("When enabled, Ctrl+T opens the Ctrl+T Navigator search window. When disabled, Ctrl+T runs the native Edit.GoToAll command instead.")]
        [DefaultValue(true)]
        public bool OverrideCtrlT { get; set; } = true;

        [Category(ShortcutsCategory)]
        [DisplayName("Use Ctrl+, shortcut")]
        [Description("When enabled, Ctrl+, opens the search window. When disabled, Ctrl+, opens Tools > Options (the native behaviour).")]
        [DefaultValue(true)]
        public bool UseCtrlCommaShortcut { get; set; } = true;

        // ---------------------------------------------------------------------------------------------
        // Indexing
        // ---------------------------------------------------------------------------------------------

        [Category(IndexingCategory)]
        [DisplayName("Auto start indexing")]
        [Description("Build the C/C++ symbol index in the background as soon as a solution is opened.")]
        [DefaultValue(true)]
        public bool AutoStartIndexing { get; set; } = true;

        [Category(IndexingCategory)]
        [DisplayName("Index solution files")]
        [Description("Index every C/C++ file that belongs to the solution. When off, only files opened in the editor are indexed.")]
        [DefaultValue(true)]
        public bool IndexSolutionFiles { get; set; } = true;

        [Category(IndexingCategory)]
        [DisplayName("Index C/C++ files")]
        [Description("Index C and C++ sources and headers. Disable this to use the window purely as a recently-used file switcher.")]
        [DefaultValue(true)]
        public bool IndexCppFiles { get; set; } = true;

        [Category(IndexingCategory)]
        [DisplayName("Scan additional include directories")]
        [Description("Additionally scan the directories listed below for headers that are not part of any project.")]
        [DefaultValue(false)]
        public bool IndexIncludeDirectories { get; set; } = false;

        [Category(IndexingCategory)]
        [DisplayName("Additional include directories")]
        [Description("Semicolon separated list of extra directories to scan for C/C++ headers (used when 'Scan additional include directories' is enabled).")]
        [DefaultValue("")]
        public string AdditionalIncludeDirectories { get; set; } = string.Empty;

        [Category(IndexingCategory)]
        [DisplayName("Exclude patterns")]
        [Description("Semicolon separated glob patterns for files and directories that are never indexed, for example **/build/** or **/third_party/**.")]
        [DefaultValue(CtrlTNavOptions.DefaultExcludePatterns)]
        public string ExcludePatterns { get; set; } = CtrlTNavOptions.DefaultExcludePatterns;

        [Category(IndexingCategory)]
        [DisplayName("Source file extensions")]
        [Description("Semicolon separated list of file extensions that are treated as C/C++ sources (leading dot optional).")]
        [DefaultValue(CtrlTNavOptions.DefaultSourceExtensions)]
        public string SourceExtensions { get; set; } = CtrlTNavOptions.DefaultSourceExtensions;

        [Category(IndexingCategory)]
        [DisplayName("Maximum file size (KB)")]
        [Description("Files larger than this are skipped. Set to 0 for no limit.")]
        [DefaultValue(4096)]
        public int MaxFileSizeKb { get; set; } = 4096;

        // ---------------------------------------------------------------------------------------------
        // Search and results
        // ---------------------------------------------------------------------------------------------

        [Category(SearchCategory)]
        [DisplayName("Maximum results")]
        [Description("Maximum number of search results shown in the popup (10 - 1000).")]
        [DefaultValue(120)]
        public int MaxResults { get; set; } = 120;

        [Category(SearchCategory)]
        [DisplayName("Weight recently used items")]
        [Description("Remember recently opened files and symbols and rank them higher in the result list.")]
        [DefaultValue(true)]
        public bool EnableRecentUsage { get; set; } = true;

        [Category(SearchCategory)]
        [DisplayName("Recent usage capacity")]
        [Description("Number of recently used entries that are remembered (10 - 5000).")]
        [DefaultValue(300)]
        public int RecentUsageCapacity { get; set; } = 300;

        // ---------------------------------------------------------------------------------------------
        // Display
        // ---------------------------------------------------------------------------------------------

        [Category(DisplayCategory)]
        [DisplayName("Show relative paths")]
        [Description("Show result paths relative to the solution directory.")]
        [DefaultValue(true)]
        public bool ShowRelativePaths { get; set; } = true;

        [Category(DisplayCategory)]
        [DisplayName("Show line numbers")]
        [Description("Show the line number of each result.")]
        [DefaultValue(true)]
        public bool ShowLineNumbers { get; set; } = true;

        [Category(DisplayCategory)]
        [DisplayName("Close when focus is lost")]
        [Description("Close the search popup when it loses focus (for example after clicking into the editor).")]
        [DefaultValue(true)]
        public bool CloseOnFocusLost { get; set; } = true;

        [Category(DisplayCategory)]
        [DisplayName("Show index status in the footer")]
        [Description("Show the number of indexed files and symbols in the popup footer.")]
        [DefaultValue(true)]
        public bool ShowIndexStatus { get; set; } = true;

        /// <summary>
        /// Converts the page state into a plain options snapshot. Values are normalized (clamped, defaults
        /// restored for empty extension lists) by <see cref="CtrlTNavOptions.Normalize"/>.
        /// </summary>
        internal CtrlTNavOptions ToOptions()
        {
            var options = new CtrlTNavOptions
            {
                OverrideCtrlT = OverrideCtrlT,
                UseCtrlCommaShortcut = UseCtrlCommaShortcut,
                AutoStartIndexing = AutoStartIndexing,
                IndexSolutionFiles = IndexSolutionFiles,
                IndexCppFiles = IndexCppFiles,
                IndexIncludeDirectories = IndexIncludeDirectories,
                AdditionalIncludeDirectories = AdditionalIncludeDirectories ?? string.Empty,
                ExcludePatterns = string.IsNullOrWhiteSpace(ExcludePatterns) ? CtrlTNavOptions.DefaultExcludePatterns : ExcludePatterns,
                SourceExtensions = string.IsNullOrWhiteSpace(SourceExtensions) ? CtrlTNavOptions.DefaultSourceExtensions : SourceExtensions,
                MaxFileSizeKb = MaxFileSizeKb,
                MaxResults = MaxResults,
                EnableRecentUsage = EnableRecentUsage,
                RecentUsageCapacity = RecentUsageCapacity,
                ShowRelativePaths = ShowRelativePaths,
                ShowLineNumbers = ShowLineNumbers,
                CloseOnFocusLost = CloseOnFocusLost,
                ShowIndexStatus = ShowIndexStatus,
            };

            return options.Normalize();
        }

        /// <summary>Copies an options snapshot into the page (used when the package supplies defaults).</summary>
        internal void ApplyOptions(CtrlTNavOptions options)
        {
            if (options == null)
            {
                return;
            }

            CtrlTNavOptions normalized = options.Normalize();
            OverrideCtrlT = normalized.OverrideCtrlT;
            UseCtrlCommaShortcut = normalized.UseCtrlCommaShortcut;
            AutoStartIndexing = normalized.AutoStartIndexing;
            IndexSolutionFiles = normalized.IndexSolutionFiles;
            IndexCppFiles = normalized.IndexCppFiles;
            IndexIncludeDirectories = normalized.IndexIncludeDirectories;
            AdditionalIncludeDirectories = normalized.AdditionalIncludeDirectories;
            ExcludePatterns = normalized.ExcludePatterns;
            SourceExtensions = normalized.SourceExtensions;
            MaxFileSizeKb = normalized.MaxFileSizeKb;
            MaxResults = normalized.MaxResults;
            EnableRecentUsage = normalized.EnableRecentUsage;
            RecentUsageCapacity = normalized.RecentUsageCapacity;
            ShowRelativePaths = normalized.ShowRelativePaths;
            ShowLineNumbers = normalized.ShowLineNumbers;
            CloseOnFocusLost = normalized.CloseOnFocusLost;
            ShowIndexStatus = normalized.ShowIndexStatus;
        }

        /// <summary>Called by the settings framework when the user confirms (OK) or cancels the dialog.</summary>
        protected override void OnApply(DialogPage.PageApplyEventArgs e)
        {
            base.OnApply(e);

            if (e.ApplyBehavior != DialogPage.ApplyKind.Apply)
            {
                return;
            }

            try
            {
                CtrlTNavOptions options = ToOptions();
                OptionsApplied?.Invoke(this, options);
            }
            catch (Exception)
            {
                // The options page must never throw into the settings dialog.
            }
        }
    }
}

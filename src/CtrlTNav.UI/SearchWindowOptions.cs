using System;

namespace CtrlTNav.UI
{
    /// <summary>
    /// Presentation options of the search popup. The instance passed to
    /// <see cref="SearchWindowController.Create"/> is copied by the controller, so mutating it afterwards has
    /// no effect until <see cref="SearchWindowController.UpdateOptions"/> is called with a new instance.
    /// </summary>
    public sealed class SearchWindowOptions
    {
        /// <summary>
        /// Directory (normally the solution directory) used to render result files as relative paths.
        /// <c>null</c> or empty means paths are shown in full.
        /// </summary>
        public string? RelativePathBaseDirectory { get; set; }

        /// <summary>When true (default) the location column ends with <c>:line</c>.</summary>
        public bool ShowLineNumbers { get; set; } = true;

        /// <summary>When true (default) file paths are shown relative to <see cref="RelativePathBaseDirectory"/>.</summary>
        public bool ShowRelativePaths { get; set; } = true;

        /// <summary>When true (default) the popup hides itself as soon as it loses keyboard focus.</summary>
        public bool CloseOnFocusLost { get; set; } = true;

        /// <summary>Placeholder text shown while the search box is empty.</summary>
        public string Placeholder { get; set; } = "Go to everything (Ctrl+T)";

        /// <summary>Returns a deep copy of this instance.</summary>
        internal SearchWindowOptions Clone()
        {
            return (SearchWindowOptions)MemberwiseClone();
        }
    }
}

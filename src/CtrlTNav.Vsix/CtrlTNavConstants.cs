using System;

namespace CtrlTNav.Vsix
{
    /// <summary>
    /// Identifiers and constant strings shared by the Visual Studio integration code, the command table
    /// (<c>CtrlTNavPackage.vsct</c>), the package registration attributes and the VSIX manifest.
    /// The GUID strings here must stay in sync with <c>CtrlTNavPackage.vsct</c> and
    /// <c>source.extension.vsixmanifest</c>; they are the public identity of the extension.
    /// </summary>
    public static class CtrlTNavConstants
    {
        // ---------------------------------------------------------------------------------------------
        // GUIDs
        // ---------------------------------------------------------------------------------------------

        /// <summary>Package GUID (<c>guidCtrlTNavPackage</c> in the VSCT file).</summary>
        public const string PackageGuidString = "7cd65e41-7938-4826-a99a-0ec3b9bba331";

        /// <summary>Command set GUID (<c>guidCtrlTNavCmdSet</c> in the VSCT file).</summary>
        public const string CommandSetGuidString = "deff788a-7d39-4399-aee3-e8bb272d4bd0";

        /// <summary>Options page GUID, registered through <see cref="Microsoft.VisualStudio.Shell.ProvideOptionPageAttribute"/>.</summary>
        public const string OptionsPageGuidString = "906af635-e1aa-4b52-b9d3-1f2584b60e13";

        /// <summary>The standard Visual Studio 97 command set GUID, used as the key-binding editor scope.</summary>
        public const string VsStd97GuidString = "5efc7975-14bc-11cf-9b2b-00aa00573819";

        /// <summary>Shell main menu GUID (<c>guidSHLMainMenu</c>), owner of the built-in Edit/Tools menus.</summary>
        public const string ShellMainMenuGuidString = "d309f791-903f-11d0-9efc-00a0c911004f";

        /// <summary>Package GUID as a <see cref="Guid"/>.</summary>
        public static readonly Guid PackageGuid = new Guid(PackageGuidString);

        /// <summary>Command set GUID as a <see cref="Guid"/>; used for the <see cref="Microsoft.VisualStudio.Shell.OleMenuCommand"/> command IDs.</summary>
        public static readonly Guid CommandSetGuid = new Guid(CommandSetGuidString);

        /// <summary>Options page GUID as a <see cref="Guid"/>.</summary>
        public static readonly Guid OptionsPageGuid = new Guid(OptionsPageGuidString);

        // ---------------------------------------------------------------------------------------------
        // Command ids (must match CtrlTNavPackage.vsct)
        // ---------------------------------------------------------------------------------------------

        /// <summary>Primary command: open the Go to Everything window (Ctrl+T).</summary>
        public const int CommandIdGoToEverything = 0x0100;

        /// <summary>Alternative command: open the window / fall back to Tools.Options (Ctrl+,).</summary>
        public const int CommandIdGoToEverythingAlt = 0x0101;

        // ---------------------------------------------------------------------------------------------
        // Registration / display strings
        // ---------------------------------------------------------------------------------------------

        /// <summary>Product name shown in Help/About, Extensions Manager and the options page title.</summary>
        public const string ProductName = "Ctrl+T Navigator for C++";

        /// <summary>Short product description used by the installed-product registration.</summary>
        public const string ProductDescription = "ReSharper-style Go to Everything (Ctrl+T) for C/C++: fuzzy search over types, symbols and files with smart ranking and recent-usage weighting.";

        /// <summary>Options page category (top level node under Tools &gt; Options).</summary>
        public const string OptionsCategoryName = "Ctrl+T Navigator";

        /// <summary>Options page name (node under the category).</summary>
        public const string OptionsPageName = "General";

        /// <summary>Version reported by the installed-product registration and the VSIX manifest.</summary>
        public const string Version = "1.0.0";

        /// <summary>File name of the compiled command table resource (see <c>ProvideMenuResource</c>).</summary>
        public const string MenuResourceName = "Menus.ctmenu";

        /// <summary>Visual Studio command executed when the user turns the native Ctrl+T override off.</summary>
        public const string NativeGoToAllCommand = "Edit.GoToAll";

        /// <summary>Visual Studio command executed by the Ctrl+, alternative when <c>UseCtrlCommaShortcut</c> is off.</summary>
        public const string NativeOptionsCommand = "Tools.Options";

        /// <summary>Status bar / log prefix so that every diagnostic line can be filtered.</summary>
        public const string LogPrefix = "CtrlTNav: ";

        /// <summary>Relative folder inside the VSIX that holds the icon, licence and getting-started files.</summary>
        public const string ResourcesFolder = "Resources";
    }
}

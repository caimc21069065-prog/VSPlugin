using CtrlTNav.Core;

namespace CtrlTNav.UI
{
    /// <summary>
    /// Colour set used by the search popup. Visual Studio integration reads the active environment colours
    /// and maps them onto this WPF-free structure; the UI assembly converts each entry into brushes.
    /// Use <see cref="Dark"/> / <see cref="Light"/> for the built-in palettes.
    /// </summary>
    public sealed class SearchWindowTheme
    {
        /// <summary>Popup surface colour (may be translucent).</summary>
        public ArgbColor Background { get; set; }

        /// <summary>Primary text colour (result names, search text).</summary>
        public ArgbColor Foreground { get; set; }

        /// <summary>Secondary text colour (paths, footer, placeholder).</summary>
        public ArgbColor SubtleForeground { get; set; }

        /// <summary>Popup outline and separator colour.</summary>
        public ArgbColor Border { get; set; }

        /// <summary>Background of the selected result row.</summary>
        public ArgbColor SelectionBackground { get; set; }

        /// <summary>Foreground used on top of <see cref="SelectionBackground"/>.</summary>
        public ArgbColor SelectionForeground { get; set; }

        /// <summary>Accent colour: matched characters, filter badge, caret, progress strip.</summary>
        public ArgbColor Accent { get; set; }

        /// <summary>Background of the pill-shaped search input.</summary>
        public ArgbColor InputBackground { get; set; }

        /// <summary>Foreground for the neutral (file/unknown) kind badges.</summary>
        public ArgbColor BadgeForeground { get; set; }

        /// <summary>Foreground of the result counter in the header.</summary>
        public ArgbColor CountForeground { get; set; }

        /// <summary>Built-in dark palette tuned for the Visual Studio dark theme.</summary>
        public static SearchWindowTheme Dark()
        {
            return new SearchWindowTheme
            {
                Background = ArgbColor.FromUInt32(0xFF292C32),
                Foreground = ArgbColor.FromUInt32(0xFFE9ECF1),
                SubtleForeground = ArgbColor.FromUInt32(0xFF9FA8B4),
                Border = ArgbColor.FromUInt32(0xFF43474F),
                SelectionBackground = ArgbColor.FromUInt32(0xFF1E4C7A),
                SelectionForeground = ArgbColor.FromUInt32(0xFFFFFFFF),
                Accent = ArgbColor.FromUInt32(0xFF5FB2FF),
                InputBackground = ArgbColor.FromUInt32(0xFF34383F),
                BadgeForeground = ArgbColor.FromUInt32(0xFFB6C0CC),
                CountForeground = ArgbColor.FromUInt32(0xFF96A0AC),
            };
        }

        /// <summary>Built-in light palette tuned for the Visual Studio light theme.</summary>
        public static SearchWindowTheme Light()
        {
            return new SearchWindowTheme
            {
                Background = ArgbColor.FromUInt32(0xFFFFFFFF),
                Foreground = ArgbColor.FromUInt32(0xFF1F2328),
                SubtleForeground = ArgbColor.FromUInt32(0xFF67707B),
                Border = ArgbColor.FromUInt32(0xFFD9DEE5),
                SelectionBackground = ArgbColor.FromUInt32(0xFF0E6AC8),
                SelectionForeground = ArgbColor.FromUInt32(0xFFFFFFFF),
                Accent = ArgbColor.FromUInt32(0xFF0B63CE),
                InputBackground = ArgbColor.FromUInt32(0xFFF4F6F9),
                BadgeForeground = ArgbColor.FromUInt32(0xFF55606B),
                CountForeground = ArgbColor.FromUInt32(0xFF67707B),
            };
        }

        /// <summary>Returns a deep copy of this theme.</summary>
        public SearchWindowTheme Clone()
        {
            return new SearchWindowTheme
            {
                Background = Background,
                Foreground = Foreground,
                SubtleForeground = SubtleForeground,
                Border = Border,
                SelectionBackground = SelectionBackground,
                SelectionForeground = SelectionForeground,
                Accent = Accent,
                InputBackground = InputBackground,
                BadgeForeground = BadgeForeground,
                CountForeground = CountForeground,
            };
        }
    }
}

using System;
using System.Windows.Media;
using CtrlTNav.Core;
using CtrlTNav.Core.Model;

namespace CtrlTNav.UI.Internal
{
    /// <summary>
    /// Turns a <see cref="SearchWindowTheme"/> into frozen WPF brushes plus the derived colours the popup
    /// needs (hover, separators, keycaps, error text, scrollbar thumb, kind badges). The palette also decides
    /// whether the theme is dark, which selects the per-kind colour table and the shadow strength.
    /// </summary>
    internal sealed class ThemePalette
    {
        private static readonly ArgbColor Black = ArgbColor.FromRgb(0, 0, 0);
        private static readonly ArgbColor White = ArgbColor.FromRgb(255, 255, 255);

        private readonly Brush[] _kindForegrounds = new Brush[32];
        private readonly Brush[] _kindBadgeBackgrounds = new Brush[32];

        private ThemePalette(SearchWindowTheme theme)
        {
            Theme = theme;

            ArgbColor background = theme.Background;
            IsDark = Luminance(background) < 0.5;
            BackgroundColor = background;

            Background = ToBrush(background);
            Foreground = ToBrush(theme.Foreground);
            SubtleForeground = ToBrush(theme.SubtleForeground);
            Border = ToBrush(theme.Border);
            SelectionBackground = ToBrush(theme.SelectionBackground);
            SelectionForeground = ToBrush(theme.SelectionForeground);
            Accent = ToBrush(theme.Accent);
            InputBackground = ToBrush(theme.InputBackground);
            BadgeForeground = ToBrush(theme.BadgeForeground);
            CountForeground = ToBrush(theme.CountForeground);

            SelectionSubtleForeground = ToBrush(theme.SelectionForeground.WithAlpha(0xBE));
            HoverBackground = ToBrush(background.Blend(IsDark ? White : Black, IsDark ? 0.065 : 0.045));
            Separator = ToBrush(theme.Border.Blend(background, IsDark ? 0.45 : 0.35));
            KeycapBackground = ToBrush(background.Blend(IsDark ? White : Black, IsDark ? 0.10 : 0.055));
            KeycapBorder = ToBrush(theme.Border.Blend(background, 0.25));
            ScrollThumb = ToBrush(theme.SubtleForeground.WithAlpha(0x66));
            ErrorForeground = ToBrush(IsDark ? ArgbColor.FromUInt32(0xFFF48771) : ArgbColor.FromUInt32(0xFFC42B1C));
            SelectionBadgeBackground = ToBrush(theme.SelectionForeground.WithAlpha(0x33));
            EmptyGlyphBackground = ToBrush(background.Blend(IsDark ? White : Black, IsDark ? 0.06 : 0.045));

            ShadowColor = Colors.Black;
            ShadowOpacity = IsDark ? 0.55 : 0.20;
            ShadowBlurRadius = IsDark ? 26 : 22;
            ShadowDepth = 7;

            UiFont = new FontFamily("Segoe UI");
            GlyphFont = new FontFamily("Segoe MDL2 Assets");

            for (int i = 0; i < _kindForegrounds.Length; i++)
            {
                _kindForegrounds[i] = Brushes.Transparent;
                _kindBadgeBackgrounds[i] = Brushes.Transparent;
            }

            foreach (ItemKind kind in Enum.GetValues(typeof(ItemKind)))
            {
                ArgbColor color = GetKindColor(kind, IsDark);
                int index = (int)kind;
                if (index >= 0 && index < _kindForegrounds.Length)
                {
                    _kindForegrounds[index] = ToBrush(color);
                    _kindBadgeBackgrounds[index] = ToBrush(color.WithAlpha(IsDark ? (byte)0x2C : (byte)0x20));
                }
            }
        }

        public SearchWindowTheme Theme { get; }

        public bool IsDark { get; }

        public ArgbColor BackgroundColor { get; }

        public Brush Background { get; }

        public Brush Foreground { get; }

        public Brush SubtleForeground { get; }

        public Brush Border { get; }

        public Brush SelectionBackground { get; }

        public Brush SelectionForeground { get; }

        /// <summary>Selection foreground at ~75% alpha, used for the location column on the selected row.</summary>
        public Brush SelectionSubtleForeground { get; }

        public Brush Accent { get; }

        public Brush InputBackground { get; }

        public Brush BadgeForeground { get; }

        public Brush CountForeground { get; }

        public Brush HoverBackground { get; }

        public Brush Separator { get; }

        public Brush KeycapBackground { get; }

        public Brush KeycapBorder { get; }

        public Brush ScrollThumb { get; }

        public Brush ErrorForeground { get; }

        public Brush SelectionBadgeBackground { get; }

        public Brush EmptyGlyphBackground { get; }

        public Color ShadowColor { get; }

        public double ShadowOpacity { get; }

        public double ShadowBlurRadius { get; }

        public double ShadowDepth { get; }

        public FontFamily UiFont { get; }

        public FontFamily GlyphFont { get; }

        public static ThemePalette Create(SearchWindowTheme? theme)
        {
            return new ThemePalette(theme ?? SearchWindowTheme.Dark());
        }

        public Brush GetKindForeground(ItemKind kind)
        {
            int index = (int)kind;
            if (index >= 0 && index < _kindForegrounds.Length && _kindForegrounds[index] != Brushes.Transparent)
            {
                return _kindForegrounds[index];
            }

            return BadgeForeground;
        }

        public Brush GetKindBadgeBackground(ItemKind kind)
        {
            int index = (int)kind;
            if (index >= 0 && index < _kindBadgeBackgrounds.Length && _kindBadgeBackgrounds[index] != Brushes.Transparent)
            {
                return _kindBadgeBackgrounds[index];
            }

            return IsDark
                ? ToBrush(Theme.BadgeForeground.WithAlpha(0x24))
                : ToBrush(Theme.BadgeForeground.WithAlpha(0x1C));
        }

        public ArgbColor GetKindColor(ItemKind kind)
        {
            return GetKindColor(kind, IsDark);
        }

        private static ArgbColor GetKindColor(ItemKind kind, bool dark)
        {
            switch (kind)
            {
                case ItemKind.File:
                    return dark ? ArgbColor.FromUInt32(0xFF9AA6B2) : ArgbColor.FromUInt32(0xFF57606A);

                case ItemKind.Namespace:
                    return dark ? ArgbColor.FromUInt32(0xFF4FC1FF) : ArgbColor.FromUInt32(0xFF1163B6);

                case ItemKind.Interface:
                    return dark ? ArgbColor.FromUInt32(0xFFC9A2FF) : ArgbColor.FromUInt32(0xFF8250DF);

                case ItemKind.Class:
                case ItemKind.Struct:
                case ItemKind.Union:
                case ItemKind.TypeAlias:
                case ItemKind.Template:
                case ItemKind.Concept:
                    return dark ? ArgbColor.FromUInt32(0xFF4EC9B0) : ArgbColor.FromUInt32(0xFF267F99);

                case ItemKind.Enum:
                case ItemKind.EnumMember:
                    return dark ? ArgbColor.FromUInt32(0xFFE5B567) : ArgbColor.FromUInt32(0xFF9A6700);

                case ItemKind.Function:
                case ItemKind.Method:
                case ItemKind.Constructor:
                case ItemKind.Destructor:
                case ItemKind.Operator:
                    return dark ? ArgbColor.FromUInt32(0xFFDCDCAA) : ArgbColor.FromUInt32(0xFF795E26);

                case ItemKind.Property:
                case ItemKind.Field:
                case ItemKind.GlobalVariable:
                    return dark ? ArgbColor.FromUInt32(0xFF9CDCFE) : ArgbColor.FromUInt32(0xFF0550AE);

                case ItemKind.Macro:
                    return dark ? ArgbColor.FromUInt32(0xFFCE9178) : ArgbColor.FromUInt32(0xFFA31515);

                default:
                    return dark ? ArgbColor.FromUInt32(0xFF9AA6B2) : ArgbColor.FromUInt32(0xFF57606A);
            }
        }

        public static Color ToColor(ArgbColor color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        public static SolidColorBrush ToBrush(ArgbColor color)
        {
            var brush = new SolidColorBrush(ToColor(color));
            brush.Freeze();
            return brush;
        }

        public static double Luminance(ArgbColor color)
        {
            return ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CtrlTNav.UI.Internal
{
    /// <summary>Small helpers that build the primitives used by the popup chrome.</summary>
    internal static class ChromeFactory
    {
        public static DropShadowEffect CreateShadow(ThemePalette palette)
        {
            return new DropShadowEffect
            {
                Color = palette.ShadowColor,
                BlurRadius = palette.ShadowBlurRadius,
                ShadowDepth = palette.ShadowDepth,
                Direction = 270,
                Opacity = palette.ShadowOpacity,
                RenderingBias = RenderingBias.Performance,
            };
        }

        public static TextBlock Text(string text, double fontSize, Brush foreground, ThemePalette palette)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = foreground,
                FontFamily = palette.UiFont,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>Rounded "keycap" used in the footer shortcut hints, for example <c>Enter</c>.</summary>
        public static Border Keycap(string text, ThemePalette palette)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = palette.KeycapBackground,
                BorderBrush = palette.KeycapBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = palette.UiFont,
                    Foreground = palette.SubtleForeground,
                },
            };
        }

        /// <summary>Single borderless text run used for the footer shortcut labels.</summary>
        public static TextBlock HintLabel(string text, ThemePalette palette)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = palette.UiFont,
                Foreground = palette.SubtleForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
        }

        /// <summary>
        /// Builds a minimal <see cref="ListBoxItem"/> template: the popup paints selection/hover itself, so the
        /// container only needs to host the content presenter (and must not add system selection brushes).
        /// </summary>
        public static ControlTemplate CreateItemContainerTemplate()
        {
            var template = new ControlTemplate(typeof(ListBoxItem));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            template.VisualTree = presenter;
            return template;
        }

        public static Style CreateItemContainerStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.FocusableProperty, false));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateItemContainerTemplate()));
            style.Seal();
            return style;
        }

        public static void SetFont(Control control, ThemePalette palette)
        {
            control.FontFamily = palette.UiFont;
        }
    }
}

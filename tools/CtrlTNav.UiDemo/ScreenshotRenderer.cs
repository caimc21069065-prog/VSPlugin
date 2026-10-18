using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CtrlTNav.UI;
using CtrlTNav.UI.Internal;

namespace CtrlTNav.UiDemo
{
    /// <summary>
    /// Renders the real popup visual tree to PNG without creating a window or an HWND: the tree is measured,
    /// arranged and then rasterised with <see cref="RenderTargetBitmap"/> on the WPF dispatcher thread.
    /// </summary>
    internal static class ScreenshotRenderer
    {
        private const double BackdropWidth = 900;
        private const double BackdropHeight = 692;
        private const double PopupTopMargin = 46;
        private const double RenderScale = 2.0;

        public static void RenderAll(string outputDirectory, string query)
        {
            Directory.CreateDirectory(outputDirectory);

            string darkPath = Path.Combine(outputDirectory, "search-window-dark.png");
            string lightPath = Path.Combine(outputDirectory, "search-window-light.png");

            RenderSingle(darkPath, SearchWindowTheme.Dark(), query);
            RenderSingle(lightPath, SearchWindowTheme.Light(), query);

            Console.WriteLine("Wrote " + darkPath);
            Console.WriteLine("Wrote " + lightPath);
        }

        private static void RenderSingle(string path, SearchWindowTheme theme, string query)
        {
            var searchService = new FakeSearchService(SampleData.CreateItems());
            var navigationService = new FakeNavigationService();
            var options = new SearchWindowOptions
            {
                RelativePathBaseDirectory = SampleData.BaseDirectory,
                ShowLineNumbers = true,
                ShowRelativePaths = true,
                CloseOnFocusLost = false,
            };

            var view = new SearchPopupView(searchService, navigationService, theme, options);
            try
            {
                view.ApplyPreviewQuery(query, 0);

                var backdrop = new Grid
                {
                    Width = BackdropWidth,
                    Height = BackdropHeight,
                    Background = CreateBackdropBrush(theme),
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true,
                };

                FrameworkElement popup = view.Root;
                popup.HorizontalAlignment = HorizontalAlignment.Center;
                popup.VerticalAlignment = VerticalAlignment.Top;
                popup.Margin = new Thickness(0, PopupTopMargin, 0, 0);
                backdrop.Children.Add(popup);

                // Full layout pass before rasterising: RenderTargetBitmap does not force layout itself.
                backdrop.Measure(new Size(BackdropWidth, BackdropHeight));
                backdrop.Arrange(new Rect(0, 0, BackdropWidth, BackdropHeight));
                backdrop.UpdateLayout();
                view.AfterPreviewLayout();
                backdrop.UpdateLayout();

                var bitmap = new RenderTargetBitmap(
                    (int)Math.Round(BackdropWidth * RenderScale),
                    (int)Math.Round(BackdropHeight * RenderScale),
                    96.0 * RenderScale,
                    96.0 * RenderScale,
                    PixelFormats.Pbgra32);
                bitmap.Render(backdrop);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(path))
                {
                    encoder.Save(stream);
                }
            }
            finally
            {
                view.Dispose();
            }
        }

        private static Brush CreateBackdropBrush(SearchWindowTheme theme)
        {
            int average = (theme.Background.R + theme.Background.G + theme.Background.B) / 3;
            bool dark = average < 128;

            var brush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.3),
                Center = new Point(0.5, 0.3),
                RadiusX = 0.9,
                RadiusY = 1.05,
            };

            if (dark)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x24, 0x26, 0x2B), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x13, 0x14, 0x17), 1.0));
            }
            else
            {
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF8, 0xFA, 0xFD), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE1, 0xE5, 0xEC), 1.0));
            }

            brush.Freeze();
            return brush;
        }
    }
}

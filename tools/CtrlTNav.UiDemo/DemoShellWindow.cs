using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CtrlTNav.UI;

namespace CtrlTNav.UiDemo
{
    /// <summary>
    /// Dummy host window for the interactive mode: the popup is an owned window of this shell, exactly like it
    /// is an owned window of devenv in the real extension. Press Ctrl+T (or use the button) to open it.
    /// </summary>
    internal sealed class DemoShellWindow : Window
    {
        private static readonly Brush ShellBackground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x23));
        private static readonly Brush PrimaryText = new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF0));
        private static readonly Brush SecondaryText = new SolidColorBrush(Color.FromRgb(0x99, 0xA2, 0xAE));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5F, 0xB2, 0xFF));

        private readonly SearchWindowController _controller;
        private readonly string _initialQuery;

        public DemoShellWindow(SearchWindowController controller, string initialQuery)
        {
            _controller = controller;
            _initialQuery = initialQuery;

            Title = "Ctrl+T Navigator for C++ \u2014 search popup UI demo";
            Width = 780;
            Height = 480;
            MinWidth = 520;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = ShellBackground;
            Content = BuildContent();
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += OnLoaded;
        }

        private FrameworkElement BuildContent()
        {
            var panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(48, 0, 48, 0),
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Ctrl+T Navigator for C++",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryText,
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Go to everything \u2014 types, symbols and files in one popup.",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Foreground = SecondaryText,
                Margin = new Thickness(0, 8, 0, 28),
            });

            var button = new Button
            {
                Content = "Open the search popup   Ctrl+T",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Padding = new Thickness(16, 9, 16, 9),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Accent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1A)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
            };
            button.Click += (_, __) => OpenPopup();
            panel.Children.Add(button);

            panel.Children.Add(new TextBlock
            {
                Text = "Keyboard: \u2191 \u2193 navigate \u00B7 Enter open \u00B7 Tab filter \u00B7 Ctrl+C copy path \u00B7 Esc close",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = SecondaryText,
                Margin = new Thickness(0, 26, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Re-run with  --render artifacts\\screenshots  to regenerate the PNG mockups.",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = SecondaryText,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            return panel;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => OpenPopup()));
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.T && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OpenPopup();
                e.Handled = true;
            }
        }

        private void OpenPopup()
        {
            try
            {
                _controller.ShowSearch(_initialQuery);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "CtrlTNav demo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CtrlTNav.UI.Internal
{
    /// <summary>
    /// Thin top-level window hosting <see cref="SearchPopupView"/>: borderless, transparent, sized to its
    /// content, positioned near the top of the work area. It is created once and only shown/hidden afterwards
    /// so opening the popup costs no window-creation work (spec NF1).
    /// </summary>
    internal sealed class SearchWindow : Window
    {
        private readonly SearchPopupView _view;

        public SearchWindow(SearchPopupView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Width = _view.TotalWidth;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Content = _view.Root;
        }

        /// <summary>
        /// Makes the popup an owned window of the host (devenv) so it stays above Visual Studio without
        /// being topmost over unrelated applications. Falls back to a topmost window when no host window
        /// handle is available (for example the interactive demo).
        /// </summary>
        public void AttachToHostWindow()
        {
            try
            {
                IntPtr hostWindow = Process.GetCurrentProcess().MainWindowHandle;
                WindowInteropHelper helper = new WindowInteropHelper(this);
                if (hostWindow == IntPtr.Zero || helper.Handle == hostWindow)
                {
                    Topmost = true;
                    return;
                }

                helper.Owner = hostWindow;
                Topmost = false;
            }
            catch (Exception)
            {
                Topmost = true;
            }
        }

        /// <summary>Centres the popup horizontally and places it ~12% from the top of the work area.</summary>
        public void PositionNearTop()
        {
            try
            {
                Rect workArea = SystemParameters.WorkArea;

                // Narrow screens (or a docked side window) may not fit the default 720 DIP.
                _view.SetTargetWidth(Math.Min(SearchPopupView.PopupWidth, workArea.Width - 48));
                Width = _view.TotalWidth;

                double width = Width > 0 ? Width : _view.TotalWidth;
                double left = workArea.Left + ((workArea.Width - width) / 2.0);
                double top = workArea.Top + (workArea.Height * 0.12);
                double estimatedHeight = 620;

                if (workArea.Bottom - top < estimatedHeight)
                {
                    top = Math.Max(workArea.Top + 8, workArea.Bottom - estimatedHeight);
                }

                Left = left;
                Top = top;
            }
            catch (Exception)
            {
                // Position is cosmetic: leave the system default when the work area is unavailable.
            }
        }

        /// <summary>Updates the hosted content after the view rebuilt its visual tree (theme change).</summary>
        public void RefreshContent()
        {
            Content = _view.Root;
        }
    }
}

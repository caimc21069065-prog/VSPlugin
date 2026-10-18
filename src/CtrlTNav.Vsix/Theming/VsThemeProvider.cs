using System;
using CtrlTNav.Core;
using CtrlTNav.Core.Diagnostics;
using CtrlTNav.UI;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace CtrlTNav.Vsix.Theming
{
    /// <summary>
    /// Maps the current Visual Studio environment colours to the WPF-free
    /// <see cref="SearchWindowTheme"/> consumed by the popup, and raises
    /// <see cref="ThemeChanged"/> when the user switches theme (Dark / Light / Blue).
    /// </summary>
    /// <remarks>
    /// Every colour lookup is defensive: when the shell theming service is unavailable (for example
    /// during background package load or in unit-test like hosts) the provider falls back to the
    /// built-in dark or light palette based on the detected background luminance.
    /// </remarks>
    public sealed class VsThemeProvider : IDisposable
    {
        private readonly ILog _log;
        private readonly object _gate = new object();
        private bool _subscribed;
        private bool _disposed;

        public VsThemeProvider(ILog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Raised when Visual Studio reports a theme change. May be raised on a background thread.</summary>
        public event EventHandler? ThemeChanged;

        /// <summary>Starts listening for Visual Studio theme changes. Safe to call more than once.</summary>
        public void Initialize()
        {
            lock (_gate)
            {
                if (_subscribed || _disposed)
                {
                    return;
                }

                try
                {
                    VSColorTheme.ThemeChanged += OnVsThemeChanged;
                    _subscribed = true;
                }
                catch (Exception ex)
                {
                    // Basic colour mapping still works; only live theme switching is lost.
                    _log.Warn("Could not subscribe to Visual Studio theme changes: " + ex.Message);
                }
            }
        }

        private void OnVsThemeChanged(ThemeChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            EventHandler? handler = ThemeChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _log.Error("A theme-change subscriber failed.", ex);
            }
        }

        /// <summary>
        /// Builds the current theme. Must be called on the UI thread when the Visual Studio theming
        /// service is available; otherwise the fallback palette is returned.
        /// </summary>
        public SearchWindowTheme CreateTheme()
        {
            SearchWindowTheme fallback = GetFallbackTheme();
            try
            {
                ArgbColor background = ReadColor(EnvironmentColors.ToolWindowBackgroundColorKey, fallback.Background);
                ArgbColor foreground = ReadColor(EnvironmentColors.ToolWindowTextColorKey, fallback.Foreground);
                ArgbColor border = ReadColor(EnvironmentColors.ToolWindowBorderColorKey, fallback.Border);
                ArgbColor accent = ReadColor(EnvironmentColors.AccentMediumColorKey, fallback.Accent);
                ArgbColor selectionBackground = ReadColor(EnvironmentColors.SystemHighlightColorKey, fallback.SelectionBackground);
                ArgbColor selectionForeground = ReadColor(EnvironmentColors.SystemHighlightTextColorKey, fallback.SelectionForeground);
                ArgbColor inputBackground = ReadColor(EnvironmentColors.SearchBoxBackgroundColorKey, fallback.InputBackground);

                // Make sure the derived colours always contrast with the theme background.
                ArgbColor subtleForeground = foreground.Blend(background, 0.38);
                ArgbColor badgeForeground = selectionForeground.Blend(foreground, 0.35);
                ArgbColor countForeground = foreground.Blend(background, 0.30);

                var theme = new SearchWindowTheme
                {
                    Background = background,
                    Foreground = foreground,
                    SubtleForeground = subtleForeground,
                    Border = border,
                    SelectionBackground = selectionBackground,
                    SelectionForeground = selectionForeground,
                    Accent = accent,
                    InputBackground = inputBackground,
                    BadgeForeground = badgeForeground,
                    CountForeground = countForeground,
                };

                return theme;
            }
            catch (Exception ex)
            {
                _log.Warn("Reading Visual Studio theme colours failed; using the fallback palette: " + ex.Message);
                return fallback;
            }
        }

        private SearchWindowTheme GetFallbackTheme()
        {
            bool isDark = true;
            try
            {
                ArgbColor background = ReadColor(EnvironmentColors.ToolWindowBackgroundColorKey, default);
                isDark = background.A == 0 || GetLuminance(background) < 128;
            }
            catch
            {
                isDark = true;
            }

            return isDark ? SearchWindowTheme.Dark() : SearchWindowTheme.Light();
        }

        private static int GetLuminance(ArgbColor color)
        {
            // Rec. 601 luma; only used to pick between the two fallback palettes.
            return (int)((color.R * 299 + color.G * 587 + color.B * 114) / 1000);
        }

        private ArgbColor ReadColor(ThemeResourceKey key, ArgbColor fallback)
        {
            if (key == null)
            {
                return fallback;
            }

            try
            {
                System.Drawing.Color color = VSColorTheme.GetThemedColor(key);
                if (color.A == 0 && color.R == 0 && color.G == 0 && color.B == 0)
                {
                    return fallback;
                }

                return ArgbColor.FromArgb(color.A, color.R, color.G, color.B);
            }
            catch (Exception ex)
            {
                _log.Info("Theme colour lookup failed for " + key + ": " + ex.Message);
                return fallback;
            }
        }

        /// <summary>Current colour sanity helper used by the fallback logic; exposed for diagnostics.</summary>
        public bool IsDarkTheme()
        {
            try
            {
                ArgbColor background = ReadColor(EnvironmentColors.ToolWindowBackgroundColorKey, default);
                return background.A == 0 || GetLuminance(background) < 128;
            }
            catch
            {
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_subscribed)
                {
                    _subscribed = false;
                    try
                    {
                        VSColorTheme.ThemeChanged -= OnVsThemeChanged;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Could not unsubscribe from theme changes: " + ex.Message);
                    }
                }
            }
        }
    }
}

using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CtrlTNav.Core.Services;
using CtrlTNav.UI.Internal;

namespace CtrlTNav.UI
{
    /// <summary>
    /// Owns the WPF search popup and is the only UI type the Visual Studio integration talks to. The window
    /// and its complete visual tree are created eagerly on the dispatcher thread so showing it is a matter of
    /// a <c>Show()</c> call; every public member is safe to call from the VS UI thread and never throws into
    /// the dispatcher.
    /// </summary>
    public sealed class SearchWindowController : IDisposable
    {
        private const int DeactivateGraceMilliseconds = 250;

        private readonly ISearchService _searchService;
        private readonly INavigationService _navigationService;
        private readonly Dispatcher _dispatcher;

        private SearchWindowTheme _theme;
        private SearchWindowOptions _options;
        private SearchPopupView? _view;
        private SearchWindow? _window;
        private bool _disposed;
        private bool _isVisible;
        private int _deactivateGraceUntilTick;

        /// <summary>
        /// Creates the controller and its pre-built (hidden) popup. Must be called on a WPF dispatcher thread,
        /// normally the Visual Studio UI thread during package initialization.
        /// </summary>
        public static SearchWindowController Create(
            ISearchService searchService,
            INavigationService navigationService,
            SearchWindowTheme theme,
            SearchWindowOptions options)
        {
            if (searchService == null)
            {
                throw new ArgumentNullException(nameof(searchService));
            }

            if (navigationService == null)
            {
                throw new ArgumentNullException(nameof(navigationService));
            }

            return new SearchWindowController(searchService, navigationService, theme, options);
        }

        private SearchWindowController(
            ISearchService searchService,
            INavigationService navigationService,
            SearchWindowTheme theme,
            SearchWindowOptions options)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                throw new InvalidOperationException("SearchWindowController.Create must be called on a WPF (STA) UI thread.");
            }

            _searchService = searchService;
            _navigationService = navigationService;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _theme = (theme ?? SearchWindowTheme.Dark()).Clone();
            _options = (options ?? new SearchWindowOptions()).Clone();

            _view = new SearchPopupView(_searchService, _navigationService, _theme, _options);
            _view.HideRequested += OnHideRequested;
            _view.VisualTreeRebuilt += OnVisualTreeRebuilt;

            try
            {
                _searchService.IndexStateChanged += OnIndexStateChanged;
            }
            catch (Exception)
            {
                // A misbehaving service must not prevent the popup from being created.
            }

            CreateWindow();
        }

        private void CreateWindow()
        {
            if (_view == null)
            {
                return;
            }

            var window = new SearchWindow(_view);
            window.Deactivated += OnWindowDeactivated;
            window.PreviewKeyDown += OnWindowPreviewKeyDown;
            window.IsVisibleChanged += OnWindowIsVisibleChanged;
            window.Closed += OnWindowClosed;
            _window = window;
        }

        /// <summary>True while the popup is displayed. Safe to read from any thread.</summary>
        public bool IsVisible
        {
            get
            {
                SearchWindow? window = _window;
                if (window == null)
                {
                    return false;
                }

                try
                {
                    return _dispatcher.CheckAccess() ? window.IsVisible : _isVisible;
                }
                catch (Exception)
                {
                    return _isVisible;
                }
            }
        }

        /// <summary>
        /// Shows the popup, optionally prefilling the search box. The caret is placed in the search box and the
        /// text is selected, matching the ReSharper command palette behaviour.
        /// </summary>
        public void ShowSearch(string? presetText = null)
        {
            RunOnDispatcher(() =>
            {
                if (_view == null)
                {
                    return;
                }

                if (_window == null)
                {
                    // The user can close the popup with Alt+F4; the window cannot be shown again
                    // after Closed, so recreate it around the (still valid) view.
                    CreateWindow();
                }

                SearchWindow? window = _window;
                if (window == null)
                {
                    return;
                }

                _view.SetQueryText(presetText);
                _deactivateGraceUntilTick = Environment.TickCount + DeactivateGraceMilliseconds;
                window.PositionNearTop();

                if (!window.IsVisible)
                {
                    window.AttachToHostWindow();
                    window.Show();
                }
                else
                {
                    window.Activate();
                }

                _isVisible = true;
                _view.FocusSearchBox(true);
                _view.RunImmediateSearch();
            });
        }

        /// <summary>Hides the popup. The window instance is kept alive so the next show is instant.</summary>
        public void HideSearch()
        {
            RunOnDispatcher(() =>
            {
                _isVisible = false;
                if (_window == null)
                {
                    return;
                }

                if (_window.IsVisible)
                {
                    _view?.OnHidden();
                    _window.Hide();
                }
            });
        }

        /// <summary>Replaces the presentation options (path format, placeholder, focus behaviour).</summary>
        public void UpdateOptions(SearchWindowOptions options)
        {
            if (options == null)
            {
                return;
            }

            RunOnDispatcher(() =>
            {
                _options = options.Clone();
                _view?.UpdateOptions(_options);
            });
        }

        /// <summary>Applies a new colour theme, typically when the Visual Studio theme changed.</summary>
        public void ApplyTheme(SearchWindowTheme theme)
        {
            if (theme == null)
            {
                return;
            }

            RunOnDispatcher(() =>
            {
                _theme = theme.Clone();
                _view?.ApplyTheme(_theme);
            });
        }

        /// <summary>Closes and disposes the window. Safe to call from any thread.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (!_dispatcher.CheckAccess())
            {
                try
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Dispose));
                }
                catch (Exception)
                {
                    // Dispatcher already shutting down: nothing left to clean up.
                }

                return;
            }

            _disposed = true;
            _isVisible = false;

            try
            {
                _searchService.IndexStateChanged -= OnIndexStateChanged;
            }
            catch (Exception)
            {
                // Ignore services that throw while unsubscribing.
            }

            if (_window != null)
            {
                try
                {
                    _window.Deactivated -= OnWindowDeactivated;
                    _window.PreviewKeyDown -= OnWindowPreviewKeyDown;
                    _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
                    _window.Closed -= OnWindowClosed;
                }
                catch (Exception)
                {
                    // Ignore teardown races.
                }

                try
                {
                    _window.Content = null;
                    _window.Close();
                }
                catch (Exception)
                {
                    // Ignore teardown races.
                }

                _window = null;
            }

            if (_view != null)
            {
                try
                {
                    _view.HideRequested -= OnHideRequested;
                    _view.VisualTreeRebuilt -= OnVisualTreeRebuilt;
                    _view.Dispose();
                }
                catch (Exception)
                {
                    // Ignore teardown races.
                }

                _view = null;
            }
        }

        private void RunOnDispatcher(Action action)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_dispatcher.CheckAccess())
                {
                    SafeExecute(action);
                }
                else
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => SafeExecute(action)));
                }
            }
            catch (Exception)
            {
                // The dispatcher may be shutting down; the popup is going away with it.
            }
        }

        private void SafeExecute(Action action)
        {
            try
            {
                if (!_disposed)
                {
                    action();
                }
            }
            catch (Exception)
            {
                // Contract: no exception may escape into the Visual Studio dispatcher.
            }
        }

        private void OnHideRequested(object? sender, EventArgs e)
        {
            HideSearch();
        }

        private void OnVisualTreeRebuilt(object? sender, EventArgs e)
        {
            try
            {
                _window?.RefreshContent();
            }
            catch (Exception)
            {
                // Content swap is cosmetic; the old tree stays when the window is already closing.
            }
        }

        private void OnIndexStateChanged(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_dispatcher.CheckAccess())
                {
                    _view?.NotifyIndexStateChanged();
                }
                else
                {
                    _dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() =>
                        {
                            if (!_disposed)
                            {
                                _view?.NotifyIndexStateChanged();
                            }
                        }));
                }
            }
            catch (Exception)
            {
                // Index notifications are advisory.
            }
        }

        private void OnWindowDeactivated(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (Environment.TickCount < _deactivateGraceUntilTick)
            {
                return;
            }

            if (!_options.CloseOnFocusLost)
            {
                return;
            }

            HideSearch();
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_disposed || e.Handled)
            {
                return;
            }

            try
            {
                if (e.Key == Key.Escape)
                {
                    HideSearch();
                    e.Handled = true;
                }
            }
            catch (Exception)
            {
                // Never let keyboard handling throw into the dispatcher.
            }
        }

        private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _isVisible = _window != null && _window.IsVisible;
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _isVisible = false;
            if (_disposed)
            {
                return;
            }

            // The window was closed by the user (Alt+F4) or by the environment. Keep the view and
            // rebuild the window lazily on the next show.
            _view?.OnHidden();
            SearchWindow? closedWindow = sender as SearchWindow ?? _window;
            if (closedWindow == null)
            {
                return;
            }

            try
            {
                closedWindow.Deactivated -= OnWindowDeactivated;
                closedWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
                closedWindow.IsVisibleChanged -= OnWindowIsVisibleChanged;
                closedWindow.Closed -= OnWindowClosed;

                // Detach the view's visual tree so the same root can be hosted by a new window.
                closedWindow.Content = null;
            }
            catch (Exception)
            {
                // Ignore teardown races.
            }

            if (ReferenceEquals(_window, closedWindow))
            {
                _window = null;
            }
        }
    }
}

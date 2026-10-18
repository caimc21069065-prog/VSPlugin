using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CtrlTNav.Core;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Search;
using CtrlTNav.Core.Services;

namespace CtrlTNav.UI.Internal
{
    /// <summary>
    /// The complete popup visual tree plus its interaction logic. Deliberately independent of
    /// <see cref="Window"/> so the exact same tree can be laid out and rendered offscreen by the screenshot
    /// tool (and, in the future, unit tests) without creating an HWND.
    ///
    /// Structure:
    /// <code>
    /// root grid (transparent, provides room for the drop shadow)
    ///   +-- chrome border (720 DIP, rounded, border, shadow)
    ///         +-- header    : filter badge + large search box + result counter
    ///         +-- progress  : 2 px accent strip while the index is building
    ///         +-- separator
    ///         +-- results   : virtualized list box / empty state
    ///         +-- footer    : count + elapsed + index status, keyboard hints, navigation errors
    /// </code>
    /// </summary>
    internal sealed class SearchPopupView : IDisposable
    {
        internal const double PopupWidth = 720;
        internal const double MinPopupWidth = 480;
        internal const double ShadowMargin = 28;
        internal const double MaxListHeight = 420;
        internal const double EmptyStateMinHeight = 176;

        private const int DebounceMilliseconds = 15;
        private const int IndexRefreshDelayMilliseconds = 140;
        private const int ErrorDisplayMilliseconds = 8000;

        private readonly ISearchService _searchService;
        private readonly INavigationService _navigationService;
        private readonly Dispatcher _dispatcher;

        private SearchWindowTheme _theme;
        private SearchWindowOptions _options;
        private ThemePalette _palette;

        private Grid? _root;
        private Border? _chrome;
        private Border? _inputPill;
        private Border? _filterBadge;
        private TextBlock? _filterBadgeText;
        private TextBox? _searchBox;
        private TextBlock? _placeholder;
        private TextBlock? _countText;
        private Border? _progressStrip;
        private ListBox? _listBox;
        private Border? _emptyPanel;
        private TextBlock? _emptyGlyph;
        private TextBlock? _emptyTitle;
        private TextBlock? _emptyDetail;
        private Border? _scrollThumb;
        private ScrollViewer? _scrollViewer;
        private TextBlock? _statusText;
        private TextBlock? _footerCountText;
        private TextBlock? _timingText;
        private TextBlock? _errorText;

        private DispatcherTimer? _debounceTimer;
        private DispatcherTimer? _indexRefreshTimer;
        private DispatcherTimer? _messageTimer;

        private List<ResultRowData> _rows = new List<ResultRowData>(0);
        private SearchResponse? _lastResponse;
        private double _lastElapsedMs;
        private int _searchVersion;
        private string _queryText = string.Empty;
        private double _targetWidth = PopupWidth;
        private bool _suppressTextChanged;
        private bool _disposed;
        private bool _indexRefreshPending;

        internal SearchPopupView(
            ISearchService searchService,
            INavigationService navigationService,
            SearchWindowTheme theme,
            SearchWindowOptions options)
        {
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _theme = (theme ?? SearchWindowTheme.Dark()).Clone();
            _options = (options ?? new SearchWindowOptions()).Clone();
            _dispatcher = Dispatcher.CurrentDispatcher;
            _palette = ThemePalette.Create(_theme);

            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds),
            };
            _debounceTimer.Tick += OnDebounceTick;

            _indexRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(IndexRefreshDelayMilliseconds),
            };
            _indexRefreshTimer.Tick += OnIndexRefreshTick;

            _messageTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ErrorDisplayMilliseconds),
            };
            _messageTimer.Tick += OnMessageTimerTick;

            BuildVisualTree();
            UpdateQueryChrome();
            UpdateFooter(0);
        }

        /// <summary>Raised when the user asked to close the popup (Escape).</summary>
        internal event EventHandler? HideRequested;

        /// <summary>Raised when the visual tree was rebuilt (theme change) so the host can re-attach the root.</summary>
        internal event EventHandler? VisualTreeRebuilt;

        /// <summary>Root element hosted by the popup window (or measured/rendered by the screenshot tool).</summary>
        internal FrameworkElement Root
        {
            get { return _root ?? (FrameworkElement)new Grid(); }
        }

        internal double TotalWidth
        {
            get { return _targetWidth + (2 * ShadowMargin); }
        }

        /// <summary>Width of the visible popup surface, clamped by <see cref="MinPopupWidth"/>.</summary>
        internal double TargetWidth
        {
            get { return _targetWidth; }
        }

        /// <summary>
        /// Narrows the popup when the host monitor is too small for the default 720 DIP (never below 480 DIP).
        /// </summary>
        internal void SetTargetWidth(double width)
        {
            double clamped = Math.Max(MinPopupWidth, Math.Min(PopupWidth, width));
            if (Math.Abs(clamped - _targetWidth) < 0.5)
            {
                return;
            }

            _targetWidth = clamped;
            if (_chrome != null)
            {
                _chrome.Width = _targetWidth;
            }
        }

        internal TextBox? SearchBox
        {
            get { return _searchBox; }
        }

        // ----------------------------------------------------------------------------------------------
        // Public-ish surface used by SearchWindowController and the screenshot tool
        // ----------------------------------------------------------------------------------------------

        /// <summary>Replaces the whole palette and rebuilds the visual tree while preserving state.</summary>
        internal void ApplyTheme(SearchWindowTheme theme)
        {
            if (_disposed || theme == null)
            {
                return;
            }

            _theme = theme.Clone();
            int selectedIndex = _listBox != null ? _listBox.SelectedIndex : 0;
            string text = _queryText;

            BuildVisualTree();
            SetQueryTextSilent(text);

            // Row data carries a palette reference, so it must be rebuilt with the new colours.
            if (_lastResponse != null)
            {
                RebuildRows(_lastResponse);
            }

            if (_listBox != null)
            {
                if (_lastResponse == null)
                {
                    _listBox.ItemsSource = _rows;
                }

                if (_rows.Count > 0)
                {
                    _listBox.SelectedIndex = Math.Min(Math.Max(selectedIndex, 0), _rows.Count - 1);
                }
            }

            UpdateQueryChrome();
            UpdateCountText();
            UpdateEmptyState(_lastResponse);
            UpdateFooter(_lastElapsedMs);
            UpdateProgressStrip();
            QueueScrollIndicatorUpdate();

            VisualTreeRebuilt?.Invoke(this, EventArgs.Empty);
        }

        internal void UpdateOptions(SearchWindowOptions options)
        {
            if (_disposed)
            {
                return;
            }

            _options = (options ?? new SearchWindowOptions()).Clone();
            if (_placeholder != null)
            {
                _placeholder.Text = GetPlaceholderText();
            }

            int selectedIndex = _listBox != null ? _listBox.SelectedIndex : 0;
            if (_lastResponse != null)
            {
                RebuildRows(_lastResponse);
                if (_listBox != null)
                {
                    if (_rows.Count > 0)
                    {
                        _listBox.SelectedIndex = Math.Min(Math.Max(selectedIndex, 0), _rows.Count - 1);
                    }
                    else
                    {
                        _listBox.SelectedIndex = -1;
                    }
                }

                UpdateEmptyState(_lastResponse);
            }

            UpdateQueryChrome();
            UpdateCountText();
            QueueScrollIndicatorUpdate();
        }

        /// <summary>Replaces the search text without triggering the debounced search.</summary>
        internal void SetQueryText(string? text)
        {
            SetQueryTextSilent(text ?? string.Empty);
            UpdateQueryChrome();
            UpdateEmptyState(_lastResponse);
        }

        internal void FocusSearchBox(bool selectAll)
        {
            if (_disposed || _searchBox == null)
            {
                return;
            }

            try
            {
                _searchBox.Focus();
                Keyboard.Focus(_searchBox);
                if (selectAll)
                {
                    _searchBox.SelectAll();
                }
                else
                {
                    _searchBox.CaretIndex = _searchBox.Text != null ? _searchBox.Text.Length : 0;
                }
            }
            catch (Exception)
            {
                // Focus is best effort; the window still works with keyboard accelerators.
            }
        }

        /// <summary>Runs the query for the current text immediately (no debounce).</summary>
        internal void RunImmediateSearch()
        {
            CancelPendingSearches();
            StartSearch(_queryText);
        }

        /// <summary>Called by the host when the index state or item count changed.</summary>
        internal void NotifyIndexStateChanged()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                UpdateProgressStrip();
                UpdateFooter(_lastElapsedMs);
                if (_rows.Count == 0)
                {
                    UpdateEmptyState(_lastResponse);
                }

                bool ready = false;
                try
                {
                    ready = _searchService.IsIndexReady;
                }
                catch (Exception)
                {
                    // Status probing must never break the popup.
                }

                if (ready && _indexRefreshTimer != null)
                {
                    _indexRefreshPending = true;
                    _indexRefreshTimer.Stop();
                    _indexRefreshTimer.Start();
                }
            }
            catch (Exception)
            {
                // Ignore: index notifications are advisory.
            }
        }

        /// <summary>Called by the host after the popup was hidden.</summary>
        internal void OnHidden()
        {
            CancelPendingSearches();
            HideError();
        }

        /// <summary>
        /// Synchronous query used by the offscreen screenshot renderer: no timers, no background tasks,
        /// results applied immediately so a single Measure/Arrange pass is enough.
        /// </summary>
        internal void ApplyPreviewQuery(string? text, int selectedIndex = 0)
        {
            SetQueryTextSilent(text ?? string.Empty);
            UpdateQueryChrome();

            // Simulate the focused input field: the real popup receives keyboard focus when shown.
            if (_inputPill != null)
            {
                _inputPill.BorderBrush = _palette.Accent;
            }

            SearchResponse response;
            try
            {
                response = _searchService.Search(_queryText) ?? SearchResponse.CreateEmpty(ItemKindFilter.All, 0, 0);
            }
            catch (Exception)
            {
                response = SearchResponse.CreateEmpty(ItemKindFilter.All, 0, 0);
            }

            ApplyResponse(response, 0);
            if (_rows.Count > 0)
            {
                SelectIndex(Math.Min(Math.Max(selectedIndex, 0), _rows.Count - 1));
            }
        }

        /// <summary>Refreshes layout-dependent decorations (scrollbar thumb) after an offscreen layout pass.</summary>
        internal void AfterPreviewLayout()
        {
            try
            {
                _listBox?.UpdateLayout();
            }
            catch (Exception)
            {
                // Best effort.
            }

            UpdateScrollIndicator();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                _debounceTimer.Tick -= OnDebounceTick;
                _debounceTimer = null;
            }

            if (_indexRefreshTimer != null)
            {
                _indexRefreshTimer.Stop();
                _indexRefreshTimer.Tick -= OnIndexRefreshTick;
                _indexRefreshTimer = null;
            }

            if (_messageTimer != null)
            {
                _messageTimer.Stop();
                _messageTimer.Tick -= OnMessageTimerTick;
                _messageTimer = null;
            }

            _searchVersion++;
            _rows = new List<ResultRowData>(0);
            if (_listBox != null)
            {
                try
                {
                    _listBox.ItemsSource = null;
                }
                catch (Exception)
                {
                    // Ignore teardown races.
                }
            }

            _root = null;
            _chrome = null;
        }

        // ----------------------------------------------------------------------------------------------
        // Visual tree construction
        // ----------------------------------------------------------------------------------------------

        private void BuildVisualTree()
        {
            _palette = ThemePalette.Create(_theme);
            _scrollViewer = null;

            var root = new Grid
            {
                Background = Brushes.Transparent,
                SnapsToDevicePixels = true,
            };

            var chrome = new Border
            {
                Width = _targetWidth,
                Margin = new Thickness(ShadowMargin),
                CornerRadius = new CornerRadius(10),
                Background = _palette.Background,
                BorderBrush = _palette.Border,
                BorderThickness = new Thickness(1),
                Effect = ChromeFactory.CreateShadow(_palette),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // header
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // progress
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // content
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // footer
            chrome.Child = layout;

            FrameworkElement header = BuildHeader();
            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            _progressStrip = new Border
            {
                Height = 2,
                Margin = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(1),
                Background = _palette.Accent,
                Visibility = Visibility.Collapsed,
            };
            Grid.SetRow(_progressStrip, 1);
            layout.Children.Add(_progressStrip);

            FrameworkElement results = BuildResultsArea();
            Grid.SetRow(results, 2);
            layout.Children.Add(results);

            FrameworkElement footer = BuildFooter();
            Grid.SetRow(footer, 3);
            layout.Children.Add(footer);

            root.Children.Add(chrome);
            root.PreviewKeyDown += OnPreviewKeyDown;

            _root = root;
            _chrome = chrome;
        }

        private FrameworkElement BuildHeader()
        {
            var header = new Border
            {
                Padding = new Thickness(14, 14, 14, 12),
                Background = Brushes.Transparent,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pillGrid = new Grid();
            pillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _filterBadgeText = new TextBlock
            {
                Text = "ALL",
                FontFamily = _palette.UiFont,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _filterBadge = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 3, 7, 3),
                MinWidth = 36,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = _palette.Accent,
                Child = _filterBadgeText,
                ToolTip = "Filter: Tab / Shift+Tab cycles types, files and symbols",
            };
            _filterBadge.MouseLeftButtonUp += OnFilterBadgeClicked;
            Grid.SetColumn(_filterBadge, 0);
            pillGrid.Children.Add(_filterBadge);

            var inputGrid = new Grid();
            _searchBox = new TextBox
            {
                FontFamily = _palette.UiFont,
                FontSize = 19,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = _palette.Foreground,
                CaretBrush = _palette.Accent,
                SelectionBrush = _palette.Accent,
                SelectionOpacity = 0.35,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(2, 0, 0, 0),
                MinHeight = 40,
                MinWidth = 120,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = false,
                AcceptsTab = false,
                IsUndoEnabled = false,
            };
            SpellCheck.SetIsEnabled(_searchBox, false);
            _searchBox.TextChanged += OnSearchTextChanged;
            _searchBox.GotKeyboardFocus += OnSearchBoxFocusChanged;
            _searchBox.LostKeyboardFocus += OnSearchBoxFocusChanged;
            inputGrid.Children.Add(_searchBox);

            _placeholder = new TextBlock
            {
                Text = GetPlaceholderText(),
                FontFamily = _palette.UiFont,
                FontSize = 17,
                Foreground = _palette.SubtleForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                IsHitTestVisible = false,
            };
            inputGrid.Children.Add(_placeholder);
            Grid.SetColumn(inputGrid, 1);
            pillGrid.Children.Add(inputGrid);

            _inputPill = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = _palette.InputBackground,
                BorderBrush = _palette.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 12, 0),
                MinHeight = 42,
                Child = pillGrid,
            };
            Grid.SetColumn(_inputPill, 0);
            grid.Children.Add(_inputPill);

            _countText = new TextBlock
            {
                Text = string.Empty,
                FontFamily = _palette.UiFont,
                FontSize = 11.5,
                Foreground = _palette.CountForeground,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 2, 0),
            };
            Grid.SetColumn(_countText, 1);
            grid.Children.Add(_countText);

            header.Child = grid;
            return header;
        }

        private FrameworkElement BuildResultsArea()
        {
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            var separator = new Border
            {
                Height = 1,
                Background = _palette.Separator,
                Margin = new Thickness(14, 0, 14, 0),
            };
            Grid.SetRow(separator, 0);
            content.Children.Add(separator);

            var host = new Grid();
            host.Margin = new Thickness(0, 4, 0, 4);

            _listBox = new ListBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = _palette.Foreground,
                MaxHeight = MaxListHeight,
                Padding = new Thickness(0, 4, 0, 4),
                Focusable = false,
                IsTextSearchEnabled = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                ItemContainerStyle = ChromeFactory.CreateItemContainerStyle(),
                ItemTemplate = CreateRowTemplate(),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_listBox, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_listBox, ScrollBarVisibility.Hidden);
            ScrollViewer.SetCanContentScroll(_listBox, true);
            VirtualizingPanel.SetIsVirtualizing(_listBox, true);
            VirtualizingPanel.SetVirtualizationMode(_listBox, VirtualizationMode.Recycling);
            _listBox.PreviewMouseLeftButtonDown += OnListMouseDown;
            _listBox.SizeChanged += OnListSizeChanged;
            _listBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrollChanged));
            host.Children.Add(_listBox);

            _scrollThumb = new Border
            {
                Width = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = _palette.ScrollThumb,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 6, 5, 0),
            };
            host.Children.Add(_scrollThumb);

            _emptyPanel = new Border
            {
                Visibility = Visibility.Collapsed,
                MinHeight = EmptyStateMinHeight,
                Padding = new Thickness(30),
            };

            var emptyStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var glyphBorder = new Border
            {
                Width = 50,
                Height = 50,
                CornerRadius = new CornerRadius(25),
                Background = _palette.EmptyGlyphBackground,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            _emptyGlyph = new TextBlock
            {
                Text = "\uE721",
                FontFamily = _palette.GlyphFont,
                FontSize = 20,
                Foreground = _palette.SubtleForeground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyphBorder.Child = _emptyGlyph;
            emptyStack.Children.Add(glyphBorder);

            _emptyTitle = new TextBlock
            {
                FontFamily = _palette.UiFont,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _palette.Foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
            };
            emptyStack.Children.Add(_emptyTitle);

            _emptyDetail = new TextBlock
            {
                FontFamily = _palette.UiFont,
                FontSize = 11.5,
                Foreground = _palette.SubtleForeground,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                Margin = new Thickness(0, 6, 0, 0),
            };
            emptyStack.Children.Add(_emptyDetail);

            _emptyPanel.Child = emptyStack;
            host.Children.Add(_emptyPanel);

            Grid.SetRow(host, 1);
            content.Children.Add(host);
            return content;
        }

        private FrameworkElement BuildFooter()
        {
            var footer = new Border
            {
                BorderBrush = _palette.Separator,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 7, 12, 8),
                Background = Brushes.Transparent,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _footerCountText = new TextBlock
            {
                FontFamily = _palette.UiFont,
                FontSize = 11,
                Foreground = _palette.CountForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Text = string.Empty,
            };
            left.Children.Add(_footerCountText);

            _timingText = new TextBlock
            {
                FontFamily = _palette.UiFont,
                FontSize = 11,
                Foreground = _palette.CountForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Text = string.Empty,
            };
            left.Children.Add(_timingText);

            _statusText = new TextBlock
            {
                FontFamily = _palette.UiFont,
                FontSize = 11,
                Foreground = _palette.SubtleForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 270,
                Text = string.Empty,
            };
            left.Children.Add(_statusText);

            _errorText = new TextBlock
            {
                FontFamily = _palette.UiFont,
                FontSize = 11,
                Foreground = _palette.ErrorForeground,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420,
                Visibility = Visibility.Collapsed,
            };
            left.Children.Add(_errorText);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var hints = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            AddHint(hints, "\u2191\u2193", "navigate");
            AddHint(hints, "Enter", "open");
            AddHint(hints, "Tab", "filter");
            AddHint(hints, "Esc", "close");
            hints.Children[hints.Children.Count - 1].SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            Grid.SetColumn(hints, 1);
            grid.Children.Add(hints);

            footer.Child = grid;
            return footer;
        }

        private void AddHint(StackPanel panel, string key, string label)
        {
            panel.Children.Add(ChromeFactory.Keycap(key, _palette));
            panel.Children.Add(ChromeFactory.HintLabel(label, _palette));
        }

        private static DataTemplate CreateRowTemplate()
        {
            var template = new DataTemplate { DataType = typeof(ResultRowData) };
            var factory = new FrameworkElementFactory(typeof(SearchResultRowView));
            factory.SetBinding(
                SearchResultRowView.IsSelectedProperty,
                new System.Windows.Data.Binding("IsSelected")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor,
                        typeof(ListBoxItem),
                        1),
                });
            template.VisualTree = factory;
            return template;
        }

        // ----------------------------------------------------------------------------------------------
        // Search pipeline
        // ----------------------------------------------------------------------------------------------

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_disposed || _suppressTextChanged)
            {
                return;
            }

            try
            {
                _queryText = _searchBox != null ? _searchBox.Text ?? string.Empty : string.Empty;
                UpdateQueryChrome();
                HideError();
                ScheduleSearch();
            }
            catch (Exception ex)
            {
                ShowError("Search failed: " + ex.Message);
            }
        }

        private void OnDebounceTick(object? sender, EventArgs e)
        {
            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
            }

            StartSearch(_queryText);
        }

        private void OnIndexRefreshTick(object? sender, EventArgs e)
        {
            if (_indexRefreshTimer != null)
            {
                _indexRefreshTimer.Stop();
            }

            if (!_indexRefreshPending || _disposed)
            {
                return;
            }

            _indexRefreshPending = false;
            StartSearch(_queryText);
        }

        private void OnMessageTimerTick(object? sender, EventArgs e)
        {
            if (_messageTimer != null)
            {
                _messageTimer.Stop();
            }

            HideError();
            UpdateFooter(_lastElapsedMs);
        }

        private void ScheduleSearch()
        {
            if (_debounceTimer == null)
            {
                return;
            }

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void CancelPendingSearches()
        {
            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
            }

            if (_indexRefreshTimer != null)
            {
                _indexRefreshTimer.Stop();
            }

            _indexRefreshPending = false;
        }

        private void StartSearch(string rawText)
        {
            if (_disposed)
            {
                return;
            }

            // Monotonic version: results that arrive after a newer keystroke are discarded.
            int version = ++_searchVersion;
            FireAndForget(SearchAndApplyAsync(rawText, version));
        }

        private async Task SearchAndApplyAsync(string rawText, int version)
        {
            var stopwatch = Stopwatch.StartNew();
            SearchResponse? response = null;
            Exception? failure = null;

            try
            {
                response = await Task.Run(() =>
                {
                    try
                    {
                        return _searchService.Search(rawText);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        return null;
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            stopwatch.Stop();

            if (_disposed || version != _searchVersion)
            {
                return;
            }

            Dispatcher dispatcher = _dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            Action apply = () =>
            {
                try
                {
                    if (_disposed || version != _searchVersion)
                    {
                        return;
                    }

                    if (failure != null || response == null)
                    {
                        ShowError("Search failed: " + (failure != null ? failure.Message : "no response"));
                        return;
                    }

                    ApplyResponse(response, elapsedMs);
                }
                catch (Exception ex)
                {
                    // Contract: an exception raised while applying results must never reach the dispatcher.
                    ShowError("Could not display results: " + ex.Message);
                }
            };

            try
            {
                if (dispatcher.CheckAccess())
                {
                    apply();
                }
                else
                {
                    _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, apply);
                }
            }
            catch (Exception)
            {
                // The dispatcher may be shutting down when the popup is torn down with Visual Studio.
            }
        }

        private static void FireAndForget(Task task)
        {
            task.ContinueWith(
                t =>
                {
                    // Observe the exception so it cannot become an unobserved task exception.
                    _ = t.Exception;
                },
                TaskScheduler.Default);
        }

        private void ApplyResponse(SearchResponse response, double roundTripMs)
        {
            if (_disposed)
            {
                return;
            }

            _lastResponse = response;
            double elapsed = response.ElapsedMs > 0.01 ? response.ElapsedMs : roundTripMs;
            _lastElapsedMs = elapsed;

            RebuildRows(response);

            HideError();
            UpdateCountText();
            UpdateEmptyState(response);
            UpdateFooter(elapsed);
            UpdateProgressStrip();

            if (_listBox != null)
            {
                if (_rows.Count > 0)
                {
                    SelectIndex(0);
                }
                else
                {
                    _listBox.SelectedIndex = -1;
                }
            }

            QueueScrollIndicatorUpdate();
        }

        private void RebuildRows(SearchResponse response)
        {
            var rows = new List<ResultRowData>(response.Results.Count);
            for (int i = 0; i < response.Results.Count; i++)
            {
                SearchResult? result = response.Results[i];
                if (result == null || result.Item == null)
                {
                    continue;
                }

                try
                {
                    rows.Add(new ResultRowData(result, _palette, _options));
                }
                catch (Exception)
                {
                    // A single malformed item must not hide the rest of the results.
                }
            }

            _rows = rows;
            if (_listBox != null)
            {
                _listBox.ItemsSource = rows;
            }
        }

        // ----------------------------------------------------------------------------------------------
        // Chrome refresh
        // ----------------------------------------------------------------------------------------------

        private string GetPlaceholderText()
        {
            string placeholder = _options.Placeholder;
            return string.IsNullOrEmpty(placeholder) ? "Go to everything (Ctrl+T)" : placeholder;
        }

        private void SetQueryTextSilent(string text)
        {
            _queryText = text ?? string.Empty;
            _suppressTextChanged = true;
            try
            {
                if (_searchBox != null)
                {
                    _searchBox.Text = _queryText;
                    _searchBox.CaretIndex = _queryText.Length;
                }
            }
            catch (Exception)
            {
                // Text assignment is not allowed to fail the popup.
            }
            finally
            {
                _suppressTextChanged = false;
            }
        }

        private void UpdateQueryChrome()
        {
            if (_placeholder != null)
            {
                _placeholder.Visibility = _queryText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            SearchQuery query = SearchQuery.Parse(_queryText);
            string label = GetFilterLabel(query.Filter);
            if (_filterBadgeText != null)
            {
                _filterBadgeText.Text = label;
            }

            if (_filterBadge != null)
            {
                ArgbColor color = GetFilterColor(query.Filter);
                _filterBadge.Background = ThemePalette.ToBrush(color);
                if (_filterBadgeText != null)
                {
                    _filterBadgeText.Foreground = ThemePalette.ToBrush(
                        _palette.IsDark ? ArgbColor.FromUInt32(0xFF14161A) : ArgbColor.FromUInt32(0xFFFFFFFF));
                }
            }
        }

        private ArgbColor GetFilterColor(ItemKindFilter filter)
        {
            switch (filter)
            {
                case ItemKindFilter.Types:
                    return _palette.GetKindColor(ItemKind.Class);
                case ItemKindFilter.Files:
                    return _palette.GetKindColor(ItemKind.File);
                case ItemKindFilter.Symbols:
                    return _palette.GetKindColor(ItemKind.Function);
                case ItemKindFilter.Members:
                    return _palette.GetKindColor(ItemKind.Field);
                default:
                    return _theme.Accent;
            }
        }

        private static string GetFilterLabel(ItemKindFilter filter)
        {
            switch (filter)
            {
                case ItemKindFilter.Types:
                    return "TYPES";
                case ItemKindFilter.Files:
                    return "FILES";
                case ItemKindFilter.Symbols:
                    return "SYMBOLS";
                case ItemKindFilter.Members:
                    return "MEMBERS";
                default:
                    return "ALL";
            }
        }

        private static string GetFilterPrefix(ItemKindFilter filter)
        {
            switch (filter)
            {
                case ItemKindFilter.Types:
                    return "t ";
                case ItemKindFilter.Files:
                    return "f ";
                case ItemKindFilter.Symbols:
                    return "#";
                case ItemKindFilter.Members:
                    return "m ";
                default:
                    return string.Empty;
            }
        }

        private void UpdateCountText()
        {
            int count = _lastResponse != null ? _lastResponse.Results.Count : _rows.Count;
            string text = FormatCount(count);
            if (_countText != null)
            {
                _countText.Text = text;
            }

            if (_footerCountText != null)
            {
                _footerCountText.Text = text;
            }
        }

        private static string FormatCount(int count)
        {
            if (count == 1)
            {
                return "1 result";
            }

            return count.ToString("N0", CultureInfo.InvariantCulture) + " results";
        }

        private void UpdateEmptyState(SearchResponse? response)
        {
            if (_listBox == null || _emptyPanel == null)
            {
                return;
            }

            bool hasRows = _rows.Count > 0;
            _listBox.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
            _emptyPanel.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;

            if (hasRows)
            {
                return;
            }

            IndexState state = IndexState.NotStarted;
            try
            {
                state = _searchService.IndexState;
            }
            catch (Exception)
            {
                // Ignore.
            }

            if (state == IndexState.Building || state == IndexState.NotStarted)
            {
                if (_emptyGlyph != null)
                {
                    _emptyGlyph.Text = "\uE895";
                }

                SetEmptyText("Indexing your solution\u2026", BuildIndexingDetail());
                return;
            }

            if (_queryText.Length == 0)
            {
                if (_emptyGlyph != null)
                {
                    _emptyGlyph.Text = "\uE721";
                }

                SetEmptyText(
                    "Search types, symbols and files",
                    "Start typing to search the whole solution. Use the t, f and # prefixes to narrow the results.");
                return;
            }

            if (_emptyGlyph != null)
            {
                _emptyGlyph.Text = "\uE721";
            }

            SetEmptyText(
                "No results for \u201C" + _queryText + "\u201D",
                "Try a shorter query, a different filter, or check that the solution index has finished building.");
        }

        private void SetEmptyText(string title, string detail)
        {
            if (_emptyTitle != null)
            {
                _emptyTitle.Text = title;
            }

            if (_emptyDetail != null)
            {
                _emptyDetail.Text = detail;
            }
        }

        private string BuildIndexingDetail()
        {
            try
            {
                IndexProgress progress = _searchService.Progress;
                if (progress.FilesTotal > 0)
                {
                    int percent = (int)((progress.FilesProcessed * 100.0) / progress.FilesTotal);
                    return "Indexed " + progress.FilesProcessed.ToString("N0", CultureInfo.InvariantCulture) +
                           " of " + progress.FilesTotal.ToString("N0", CultureInfo.InvariantCulture) +
                           " files (" + percent + "%). Results appear as files are parsed.";
                }

                return "Results appear as soon as the first files are parsed.";
            }
            catch (Exception)
            {
                return "Results appear as soon as the first files are parsed.";
            }
        }

        private void UpdateFooter(double elapsedMs)
        {
            if (_footerCountText != null)
            {
                _footerCountText.Text = FormatCount(_lastResponse != null ? _lastResponse.Results.Count : _rows.Count);
            }

            if (_timingText != null)
            {
                _timingText.Text = elapsedMs > 0 ? "\u00B7 " + FormatElapsed(elapsedMs) : string.Empty;
            }

            if (_statusText != null && _errorText != null && _errorText.Visibility != Visibility.Visible)
            {
                _statusText.Text = GetStatusText();
            }
        }

        private string GetStatusText()
        {
            try
            {
                string status = _searchService.StatusText;
                if (!string.IsNullOrEmpty(status))
                {
                    return status;
                }

                return _searchService.IndexState == IndexState.Ready ? "Ready" : "Indexing\u2026";
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string FormatElapsed(double milliseconds)
        {
            if (milliseconds < 0.95)
            {
                return "<1 ms";
            }

            if (milliseconds < 10)
            {
                return milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
            }

            return ((int)Math.Round(milliseconds)).ToString("N0", CultureInfo.InvariantCulture) + " ms";
        }

        private void UpdateProgressStrip()
        {
            if (_progressStrip == null)
            {
                return;
            }

            bool building = false;
            try
            {
                IndexState state = _searchService.IndexState;
                building = state == IndexState.Building || state == IndexState.NotStarted;
            }
            catch (Exception)
            {
                // Ignore.
            }

            if (building)
            {
                if (_progressStrip.Visibility != Visibility.Visible)
                {
                    _progressStrip.Visibility = Visibility.Visible;
                    var animation = new DoubleAnimation(0.35, 1.0, new Duration(TimeSpan.FromMilliseconds(650)))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                    };
                    _progressStrip.BeginAnimation(UIElement.OpacityProperty, animation);
                }
            }
            else
            {
                _progressStrip.BeginAnimation(UIElement.OpacityProperty, null);
                _progressStrip.Opacity = 1;
                _progressStrip.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowError(string message)
        {
            if (_disposed || _errorText == null)
            {
                return;
            }

            _errorText.Text = message;
            _errorText.Visibility = Visibility.Visible;
            if (_statusText != null)
            {
                _statusText.Visibility = Visibility.Collapsed;
            }

            if (_timingText != null)
            {
                _timingText.Visibility = Visibility.Collapsed;
            }

            if (_messageTimer != null)
            {
                _messageTimer.Stop();
                _messageTimer.Start();
            }
        }

        private void HideError()
        {
            if (_errorText == null)
            {
                return;
            }

            _errorText.Visibility = Visibility.Collapsed;
            if (_statusText != null)
            {
                _statusText.Visibility = Visibility.Visible;
            }

            if (_timingText != null)
            {
                _timingText.Visibility = Visibility.Visible;
            }

            if (_messageTimer != null)
            {
                _messageTimer.Stop();
            }
        }

        // ----------------------------------------------------------------------------------------------
        // Selection / navigation
        // ----------------------------------------------------------------------------------------------

        private ResultRowData? SelectedRow
        {
            get
            {
                if (_listBox == null)
                {
                    return null;
                }

                int index = _listBox.SelectedIndex;
                if (index < 0 || index >= _rows.Count)
                {
                    return null;
                }

                return _rows[index];
            }
        }

        private void SelectIndex(int index)
        {
            if (_listBox == null)
            {
                return;
            }

            if (_rows.Count == 0)
            {
                _listBox.SelectedIndex = -1;
                return;
            }

            if (index < 0)
            {
                index = 0;
            }

            if (index >= _rows.Count)
            {
                index = _rows.Count - 1;
            }

            if (_listBox.SelectedIndex != index)
            {
                _listBox.SelectedIndex = index;
            }

            try
            {
                _listBox.ScrollIntoView(_rows[index]);
            }
            catch (Exception)
            {
                // ScrollIntoView can race with a container reset; harmless.
            }

            UpdateScrollIndicator();
        }

        private void MoveSelection(int delta)
        {
            if (_rows.Count == 0)
            {
                return;
            }

            int current = _listBox != null ? _listBox.SelectedIndex : -1;
            if (current < 0)
            {
                current = 0;
            }

            SelectIndex(current + delta);
        }

        private int GetPageSize()
        {
            double viewport = _listBox != null ? _listBox.ActualHeight : MaxListHeight;
            int rows = (int)Math.Floor(viewport / SearchResultRowView.RowPitch) - 1;
            return rows < 1 ? 1 : rows;
        }

        private void NavigateSelected()
        {
            ResultRowData? row = SelectedRow;
            if (row == null)
            {
                return;
            }

            bool navigated = false;
            string? error = null;
            try
            {
                navigated = _navigationService.Navigate(row.Item, out error);
            }
            catch (Exception ex)
            {
                navigated = false;
                error = ex.Message;
            }

            if (!navigated)
            {
                ShowError(string.IsNullOrEmpty(error) ? "Could not open " + row.Item.Name : error!);
                return;
            }

            try
            {
                _searchService.RecordUsage(row.Item);
            }
            catch (Exception)
            {
                // Usage tracking is a nicety: never block the navigation on it.
            }

            EventHandler? handler = HideRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void CopySelectedLocation()
        {
            ResultRowData? row = SelectedRow;
            if (row == null)
            {
                return;
            }

            string text = row.Item.FilePath ?? string.Empty;
            if (text.Length > 0 && row.Item.Line > 0)
            {
                text += ":" + row.Item.Line.ToString(CultureInfo.InvariantCulture);
            }

            if (text.Length == 0)
            {
                text = row.Name;
            }

            try
            {
                Clipboard.SetText(text);
                ShowTransientStatus("Copied " + text);
            }
            catch (Exception ex)
            {
                ShowError("Could not copy the path: " + ex.Message);
            }
        }

        private void ShowTransientStatus(string message)
        {
            if (_disposed || _statusText == null || _errorText == null)
            {
                return;
            }

            _statusText.Text = message;
            _statusText.Visibility = Visibility.Visible;
            _errorText.Visibility = Visibility.Collapsed;
            if (_messageTimer != null)
            {
                _messageTimer.Stop();
                _messageTimer.Start();
            }
        }

        private void CycleFilter(bool reverse)
        {
            SearchQuery query = SearchQuery.Parse(_queryText);
            ItemKindFilter[] order =
            {
                ItemKindFilter.All,
                ItemKindFilter.Types,
                ItemKindFilter.Files,
                ItemKindFilter.Symbols,
            };

            int current = Array.IndexOf(order, query.Filter);
            if (current < 0)
            {
                current = 0;
            }

            int next = reverse
                ? ((current + order.Length - 1) % order.Length)
                : ((current + 1) % order.Length);

            string text = GetFilterPrefix(order[next]) + query.Text;
            SetQueryTextSilent(text);
            UpdateQueryChrome();
            FocusSearchBox(false);
            RunImmediateSearch();
        }

        // ----------------------------------------------------------------------------------------------
        // Input handling
        // ----------------------------------------------------------------------------------------------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                switch (e.Key)
                {
                    case Key.Escape:
                        RaiseHideRequested();
                        e.Handled = true;
                        return;

                    case Key.Enter:
                        NavigateSelected();
                        e.Handled = true;
                        return;

                    case Key.Up:
                        MoveSelection(-1);
                        e.Handled = true;
                        return;

                    case Key.Down:
                        MoveSelection(1);
                        e.Handled = true;
                        return;

                    case Key.PageUp:
                        MoveSelection(-GetPageSize());
                        e.Handled = true;
                        return;

                    case Key.PageDown:
                        MoveSelection(GetPageSize());
                        e.Handled = true;
                        return;

                    case Key.Home:
                        SelectIndex(0);
                        e.Handled = true;
                        return;

                    case Key.End:
                        SelectIndex(_rows.Count - 1);
                        e.Handled = true;
                        return;

                    case Key.Tab:
                        CycleFilter(shift);
                        e.Handled = true;
                        return;

                    case Key.C:
                        if (control)
                        {
                            CopySelectedLocation();
                            e.Handled = true;
                        }

                        return;

                    default:
                        return;
                }
            }
            catch (Exception ex)
            {
                ShowError("Unexpected error: " + ex.Message);
            }
        }

        private void OnFilterBadgeClicked(object sender, MouseButtonEventArgs e)
        {
            try
            {
                CycleFilter((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            }
            catch (Exception ex)
            {
                ShowError("Unexpected error: " + ex.Message);
            }

            e.Handled = true;
        }

        private void OnSearchBoxFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_inputPill == null)
            {
                return;
            }

            bool focused = _searchBox != null && _searchBox.IsKeyboardFocused;
            _inputPill.BorderBrush = focused ? _palette.Accent : _palette.Border;
            _inputPill.BorderThickness = new Thickness(1);
        }

        private void OnListMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || _listBox == null)
            {
                return;
            }

            try
            {
                DependencyObject? source = e.OriginalSource as DependencyObject;
                if (source == null)
                {
                    return;
                }

                ListBoxItem? container = ItemsControl.ContainerFromElement(_listBox, source) as ListBoxItem;
                if (container == null)
                {
                    return;
                }

                int index = _listBox.ItemContainerGenerator.IndexFromContainer(container);
                if (index < 0)
                {
                    return;
                }

                SelectIndex(index);
                e.Handled = true;

                if (e.ClickCount >= 2)
                {
                    NavigateSelected();
                }

                // Keep the caret where the user is typing: clicking a row must not move focus out of the box.
                if (_searchBox != null)
                {
                    _searchBox.Focus();
                }
            }
            catch (Exception ex)
            {
                ShowError("Unexpected error: " + ex.Message);
            }
        }

        private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateScrollIndicator();
        }

        private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateScrollIndicator();
        }

        private void RaiseHideRequested()
        {
            EventHandler? handler = HideRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // ----------------------------------------------------------------------------------------------
        // Scroll indicator
        // ----------------------------------------------------------------------------------------------

        private void QueueScrollIndicatorUpdate()
        {
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateScrollIndicator));
            }
            catch (Exception)
            {
                // Dispatcher may already be gone during teardown.
            }
        }

        private void UpdateScrollIndicator()
        {
            if (_scrollThumb == null || _listBox == null || _disposed)
            {
                return;
            }

            try
            {
                if (_rows.Count == 0 || _listBox.Visibility != Visibility.Visible)
                {
                    _scrollThumb.Visibility = Visibility.Collapsed;
                    return;
                }

                double viewport = _listBox.ActualHeight - _listBox.Padding.Top - _listBox.Padding.Bottom;
                if (viewport <= 8)
                {
                    _scrollThumb.Visibility = Visibility.Collapsed;
                    return;
                }

                double extent = (_rows.Count * SearchResultRowView.RowPitch) + _listBox.Padding.Top + _listBox.Padding.Bottom;
                if (extent <= viewport + 1)
                {
                    _scrollThumb.Visibility = Visibility.Collapsed;
                    return;
                }

                ScrollViewer? viewer = FindScrollViewer();
                double offset = viewer != null ? viewer.VerticalOffset : 0;
                double maxOffset = Math.Max(1, extent - viewport);

                double thumbHeight = Math.Max(30, viewport * (viewport / extent));
                double travel = Math.Max(0, viewport - thumbHeight);
                double top = _listBox.Padding.Top + (travel * Math.Min(1, Math.Max(0, offset / maxOffset)));

                _scrollThumb.Height = thumbHeight;
                _scrollThumb.Margin = new Thickness(0, 4 + top, 5, 0);
                _scrollThumb.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                // A missed scrollbar refresh is purely cosmetic.
            }
        }

        private ScrollViewer? FindScrollViewer()
        {
            if (_scrollViewer != null)
            {
                return _scrollViewer;
            }

            if (_listBox == null)
            {
                return null;
            }

            _scrollViewer = FindVisualChild<ScrollViewer>(_listBox);
            return _scrollViewer;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                {
                    return typed;
                }

                T? nested = FindVisualChild<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}

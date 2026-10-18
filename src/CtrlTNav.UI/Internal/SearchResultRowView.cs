using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CoreTextRange = CtrlTNav.Core.Model.TextRange;

namespace CtrlTNav.UI.Internal
{
    /// <summary>
    /// One result row. The popup draws selection and hover itself, so this element owns its complete look:
    /// a rounded background, a coloured kind badge, the symbol name with the fuzzy-matched characters
    /// emphasised, and a right-aligned <c>relative\path:line</c> column.
    /// </summary>
    internal sealed class SearchResultRowView : Border
    {
        /// <summary>Row height including the vertical margin: the list assumes a uniform row height.</summary>
        public const double RowPitch = 30;

        private const double RowHeight = 28;

        /// <summary>Bound to the ancestor <see cref="ListBoxItem"/> so the row follows list selection.</summary>
        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
            "IsSelected",
            typeof(bool),
            typeof(SearchResultRowView),
            new FrameworkPropertyMetadata(false, OnIsSelectedChanged));

        private TextBlock? _nameText;
        private TextBlock? _locationText;
        private Border? _badge;
        private TextBlock? _badgeText;
        private List<Run>? _highlightRuns;

        public SearchResultRowView()
        {
            Height = RowHeight;
            Margin = new Thickness(6, 1, 6, 1);
            CornerRadius = new CornerRadius(6);
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;
            Focusable = false;
            DataContextChanged += OnRowDataContextChanged;
        }

        public bool IsSelected
        {
            get { return (bool)GetValue(IsSelectedProperty); }
            set { SetValue(IsSelectedProperty, value); }
        }

        private void OnRowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            BuildContent();
            UpdateChrome();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == IsMouseOverProperty)
            {
                UpdateChrome();
            }
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SearchResultRowView)d).UpdateChrome();
        }

        private void BuildContent()
        {
            _nameText = null;
            _locationText = null;
            _badge = null;
            _badgeText = null;
            _highlightRuns = null;

            ResultRowData? data = DataContext as ResultRowData;
            if (data == null)
            {
                Child = null;
                ToolTip = null;
                return;
            }

            ThemePalette palette = data.Palette;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _badgeText = new TextBlock
            {
                Text = data.BadgeText,
                FontFamily = palette.UiFont,
                FontSize = data.BadgeText.Length > 1 ? 8.5 : 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };

            _badge = new Border
            {
                Width = 22,
                Height = 21,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = _badgeText,
            };
            Grid.SetColumn(_badge, 0);
            grid.Children.Add(_badge);

            _nameText = new TextBlock
            {
                FontFamily = palette.UiFont,
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };
            BuildNameRuns(_nameText, data);
            Grid.SetColumn(_nameText, 1);
            grid.Children.Add(_nameText);

            _locationText = new TextBlock
            {
                Text = data.Location,
                FontFamily = palette.UiFont,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxWidth = 288,
                Margin = new Thickness(14, 0, 12, 0),
            };
            Grid.SetColumn(_locationText, 2);
            grid.Children.Add(_locationText);

            Child = grid;
            ToolTip = data.ToolTipText;
        }

        private void BuildNameRuns(TextBlock block, ResultRowData data)
        {
            block.Inlines.Clear();
            string name = data.Name;
            if (name.Length == 0)
            {
                return;
            }

            var highlighted = new bool[name.Length];
            IReadOnlyList<CoreTextRange> ranges = data.Highlights;
            for (int i = 0; i < ranges.Count; i++)
            {
                CoreTextRange range = ranges[i];
                int end = Math.Min(range.End, highlighted.Length);
                for (int c = Math.Max(0, range.Start); c < end; c++)
                {
                    highlighted[c] = true;
                }
            }

            var highlightRuns = new List<Run>(2);
            int index = 0;
            while (index < name.Length)
            {
                bool isHighlight = highlighted[index];
                int start = index;
                while (index < name.Length && highlighted[index] == isHighlight)
                {
                    index++;
                }

                var run = new Run(name.Substring(start, index - start))
                {
                    FontWeight = isHighlight ? FontWeights.Bold : FontWeights.Normal,
                };
                if (isHighlight)
                {
                    highlightRuns.Add(run);
                }

                block.Inlines.Add(run);
            }

            _highlightRuns = highlightRuns.Count > 0 ? highlightRuns : null;
        }

        private void UpdateChrome()
        {
            ResultRowData? data = DataContext as ResultRowData;
            if (data == null)
            {
                Background = Brushes.Transparent;
                return;
            }

            ThemePalette palette = data.Palette;
            bool selected = IsSelected;
            bool hovered = !selected && IsMouseOver;

            Background = selected
                ? palette.SelectionBackground
                : hovered
                    ? palette.HoverBackground
                    : Brushes.Transparent;

            if (_nameText != null)
            {
                _nameText.Foreground = selected ? palette.SelectionForeground : palette.Foreground;
            }

            if (_highlightRuns != null)
            {
                Brush brush = selected ? palette.SelectionForeground : palette.Accent;
                for (int i = 0; i < _highlightRuns.Count; i++)
                {
                    _highlightRuns[i].Foreground = brush;
                }
            }

            if (_locationText != null)
            {
                _locationText.Foreground = selected ? palette.SelectionSubtleForeground : palette.SubtleForeground;
            }

            if (_badge != null)
            {
                _badge.Background = selected ? palette.SelectionBadgeBackground : palette.GetKindBadgeBackground(data.Item.Kind);
            }

            if (_badgeText != null)
            {
                _badgeText.Foreground = selected ? palette.SelectionForeground : palette.GetKindForeground(data.Item.Kind);
            }
        }
    }
}

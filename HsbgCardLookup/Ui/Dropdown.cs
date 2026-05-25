using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Compact single-select dropdown (button + popover), modeled on the website's filter
    /// dropdowns. Options can carry an icon (tier/tribe). null value = the "All" option.
    /// </summary>
    public sealed class Dropdown : Border
    {
        public sealed class Item
        {
            public string Value;
            public string Label;
            public string IconPath;
            public Item(string value, string label, string iconPath = null) { Value = value; Label = label; IconPath = iconPath; }
        }

        private readonly OverlayBase _owner;
        private readonly Popup _popup;
        private readonly DockPanel _buttonContent;
        private readonly Action<string> _onChange;
        private readonly string _allLabel;
        private readonly List<Item> _items;
        private readonly double _iconSize;
        private string _selected;   // null = all

        public Dropdown(OverlayBase owner, string allLabel, List<Item> items, Action<string> onChange, double iconSize = 22)
        {
            _owner = owner;
            _allLabel = allLabel;
            _items = items;
            _onChange = onChange;
            _iconSize = iconSize;

            CornerRadius = new CornerRadius(7);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(12, 5, 10, 5);
            Margin = new Thickness(0, 0, 7, 0);
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Stretch;   // match the search bar's height

            _buttonContent = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            var chevron = new Path
            {
                Data = Geometry.Parse("M6 9l6 6 6-6"),
                Stroke = UiKit.TextSecondary,
                StrokeThickness = 2,
                Stretch = Stretch.Uniform,
                Width = 11,
                Height = 11,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(chevron, Dock.Right);
            _buttonContent.Children.Add(chevron);
            Child = _buttonContent;

            _popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                HorizontalOffset = 0,
                VerticalOffset = 4
            };
            _popup.Child = BuildPopupContent();
            // Guard the owner's focus-loss auto-hide while the popup is open. Increment BEFORE
            // opening (so it beats any deactivation), decrement once on close. Exactly balanced.
            _popup.Closed += (s, e) => _owner?.EndPopup();

            MouseLeftButtonUp += (s, e) => { if (!_popup.IsOpen) { _owner?.BeginPopup(); _popup.IsOpen = true; } };

            SetSelected(null);
        }

        private FrameworkElement BuildPopupContent()
        {
            var list = new StackPanel();
            list.Children.Add(OptionRow(null, _allLabel, null));
            foreach (var it in _items)
                list.Children.Add(OptionRow(it.Value, it.Label, it.IconPath));

            return new Border
            {
                Background = new LinearGradientBrush(UiKit.PanelBg2, UiKit.PanelBg, 90),
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                MinWidth = 150,
                Child = new ScrollViewer
                {
                    MaxHeight = 360,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = list
                }
            };
        }

        private Border OptionRow(string value, string label, string iconPath)
        {
            var dock = new DockPanel { LastChildFill = true };
            var icon = UiKit.IconImage(iconPath, _iconSize);
            if (icon != null)
            {
                icon.Margin = new Thickness(0, 0, 9, 0);
                DockPanel.SetDock(icon, Dock.Left);
                dock.Children.Add(icon);
            }
            dock.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = UiKit.TextPrimary,
                FontSize = 17,
                VerticalAlignment = VerticalAlignment.Center
            });

            var row = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(12, 9, 16, 9),
                Cursor = Cursors.Hand,
                Child = dock
            };
            row.MouseEnter += (s, e) => row.Background = UiKit.Br(UiKit.RowBgHover);
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (s, e) =>
            {
                _popup.IsOpen = false;
                SetSelected(value);
                _onChange?.Invoke(value);
            };
            return row;
        }

        public void SetSelected(string value)
        {
            _selected = value;
            bool active = value != null;
            Background = active ? UiKit.Br(UiKit.PanelActive) : UiKit.Br(UiKit.RowBg);
            BorderBrush = active ? UiKit.AccentBrush : UiKit.StrokeBrush;

            // Rebuild button face: optional icon + label, keeping the chevron docked right.
            _buttonContent.Children.Clear();
            var chevron = new Path
            {
                Data = Geometry.Parse("M6 9l6 6 6-6"),
                Stroke = active ? UiKit.AccentBrush : UiKit.TextSecondary,
                StrokeThickness = 2, Stretch = Stretch.Uniform, Width = 13, Height = 13,
                Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(chevron, Dock.Right);
            _buttonContent.Children.Add(chevron);

            // When a filter is set, offer a one-click clear "✕" just left of the chevron.
            if (active)
            {
                var clear = UiKit.ClearButton(() => { SetSelected(null); _onChange?.Invoke(null); }, "Clear filter", 13);
                clear.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(clear, Dock.Right);
                _buttonContent.Children.Add(clear);
            }

            string iconPath = null, label = _allLabel;
            if (active)
            {
                var it = _items.Find(x => x.Value == value);
                if (it != null) { iconPath = it.IconPath; label = it.Label; }
            }
            var icon = UiKit.IconImage(iconPath, _iconSize);
            if (icon != null)
            {
                icon.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(icon, Dock.Left);
                _buttonContent.Children.Add(icon);
            }
            _buttonContent.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = active ? UiKit.AccentBrush : UiKit.TextPrimary,
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }
}

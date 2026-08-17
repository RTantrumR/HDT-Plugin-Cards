using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using HsbgCardLookup.Data;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Shared visual language + widget factories for the overlay variants. Operates on the
    /// real <see cref="BgCard"/> model.
    /// </summary>
    internal static class UiKit
    {
        // Palette
        public static readonly Color PanelBg     = Color.FromArgb(0xF2, 0x10, 0x14, 0x1C); // ~95% opaque
        public static readonly Color PanelBg2    = Color.FromArgb(0xF2, 0x16, 0x1C, 0x28);
        public static readonly Color FieldBg     = Color.FromRgb(0x0A, 0x0D, 0x14);
        public static readonly Color RowBg       = Color.FromRgb(0x1A, 0x21, 0x30);
        public static readonly Color RowBgHover  = Color.FromRgb(0x24, 0x2E, 0x42);
        public static readonly Color PanelActive = Color.FromArgb(0x2B, 0xE8, 0xB5, 0x4B); // gold tint
        public static readonly Color Stroke      = Color.FromRgb(0x39, 0x47, 0x5E);
        public static readonly Color Accent      = Color.FromRgb(0xE8, 0xB5, 0x4B);

        public static readonly Brush TextPrimary   = Frozen(Color.FromRgb(0xF2, 0xF5, 0xFA));
        public static readonly Brush TextSecondary = Frozen(Color.FromRgb(0xB6, 0xC1, 0xD2));
        public static readonly Brush TextMuted     = Frozen(Color.FromRgb(0x76, 0x83, 0x96));
        public static readonly Brush AccentBrush   = Frozen(Accent);
        public static readonly Brush StrokeBrush   = Frozen(Stroke);

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static SolidColorBrush Br(Color c) => new SolidColorBrush(c);

        public static Color TierColor(int tier)
        {
            switch (tier)
            {
                case 1: return Color.FromRgb(0x8A, 0x8F, 0x98);
                case 2: return Color.FromRgb(0x4C, 0xAF, 0x50);
                case 3: return Color.FromRgb(0x42, 0x9A, 0xE0);
                case 4: return Color.FromRgb(0x9B, 0x59, 0xD6);
                case 5: return Color.FromRgb(0xE0, 0x7A, 0x3C);
                case 6: return Color.FromRgb(0xD9, 0x43, 0x4E);
                default: return Color.FromRgb(0x8A, 0x8F, 0x98);
            }
        }

        // ---- Text helpers ----

        public static TextBlock Title(string text, double size = 22) => new TextBlock
        {
            Text = text, Foreground = TextPrimary, FontSize = size, FontWeight = FontWeights.Bold
        };

        public static TextBlock Label(string text, double size = 13, Brush brush = null) => new TextBlock
        {
            Text = text, Foreground = brush ?? TextSecondary, FontSize = size, TextWrapping = TextWrapping.Wrap
        };

        public static string Pretty(string cardType)
        {
            if (string.IsNullOrEmpty(cardType)) return "";
            var s = cardType.Replace('_', ' ');
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>Stats string, or null when the card has none worth showing
        /// (only minions have attack/health; heroes show HP; spells/hero powers/etc. show nothing).</summary>
        public static string StatsText(BgCard c)
        {
            if (c.CardType == "minion" && c.Attack.HasValue && c.Health.HasValue)
                return c.Attack + "/" + c.Health;
            if ((c.CardType == "hero" || c.IsHero) && c.Health.HasValue)
                return c.Health + " HP";
            return null;
        }

        public static string SubtitleFor(BgCard c)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(c.PrimaryTribe)) parts.Add(c.PrimaryTribe);
            else if (!string.IsNullOrEmpty(c.CardType)) parts.Add(Pretty(c.CardType));

            var stats = StatsText(c);
            if (stats != null) parts.Add(stats);
            return string.Join("  ·  ", parts);
        }

        // ---- Widgets ----

        public static Border Panel(UIElement child, double corner = 14) => new Border
        {
            Background = Br(PanelBg),
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(corner),
            Child = child
        };

        /// <summary>Search field with a magnifier glyph + watermark. Returns the wrapping
        /// Border and hands back the inner TextBox via <paramref name="box"/>.</summary>
        public static Border SearchField(string placeholder, out TextBox box, double fontSize = 18, UIElement trailing = null)
        {
            var glyph = new TextBlock
            {
                Text = "🔍",
                Foreground = TextMuted,
                FontSize = fontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0)
            };

            box = new TextBox
            {
                FontSize = fontSize,
                Background = Brushes.Transparent,
                Foreground = TextPrimary,
                BorderThickness = new Thickness(0),
                CaretBrush = TextPrimary,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinWidth = 80
            };

            var watermark = new TextBlock
            {
                Text = placeholder,
                Foreground = TextMuted,
                FontSize = fontSize,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                // Nudge the placeholder slightly right of the TextBox's caret origin so the blinking
                // caret sits just *before* the first letter instead of overlapping it.
                Margin = new Thickness(4, 0, 0, 0)
            };
            var theBox = box;
            box.TextChanged += (s, e) =>
                watermark.Visibility = string.IsNullOrEmpty(theBox.Text) ? Visibility.Visible : Visibility.Collapsed;

            var boxGrid = new Grid();
            boxGrid.Children.Add(watermark);
            boxGrid.Children.Add(box);

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            if (trailing != null)
            {
                DockPanel.SetDock(trailing, Dock.Right);
                row.Children.Add(trailing);
            }
            row.Children.Add(boxGrid);

            return new Border
            {
                Background = Br(FieldBg),
                BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 8, 9),
                Child = row
            };
        }

        /// <summary>Small red corner "✕" for the top drag strip: closes the overlay on click. Meant
        /// to be overlaid AFTER the strip (it's a sibling, so its clicks never start a drag).</summary>
        public static Border CornerCloseButton(Action onClose, string tooltip)
        {
            var normal = Br(Color.FromRgb(0xC9, 0x55, 0x55));
            var hover = Br(Color.FromRgb(0xFF, 0x6B, 0x6B));
            var glyph = new TextBlock
            {
                Text = "✕", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = normal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var b = new Border
            {
                Width = 26, Height = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 6, 0), Child = glyph, ToolTip = tooltip
            };
            b.MouseEnter += (s, e) => glyph.Foreground = hover;
            b.MouseLeave += (s, e) => glyph.Foreground = normal;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClose?.Invoke(); };
            return b;
        }

        /// <summary>
        /// A small clickable "✕" clear button. Brightens on hover; swallows the click so it
        /// won't bubble to a parent (e.g. a dropdown that would otherwise reopen). The caller
        /// controls visibility (typically shown only when there's something to clear).
        /// </summary>
        public static Border ClearButton(Action onClear, string tooltip = "Clear", double fontSize = 15)
        {
            var glyph = new TextBlock
            {
                Text = "✕", FontSize = fontSize, Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            };
            var b = new Border
            {
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Padding = new Thickness(5, 2, 5, 2), VerticalAlignment = VerticalAlignment.Center,
                Child = glyph, ToolTip = tooltip
            };
            b.MouseEnter += (s, e) => glyph.Foreground = TextPrimary;
            b.MouseLeave += (s, e) => glyph.Foreground = TextMuted;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClear?.Invoke(); };
            return b;
        }

        /// <summary>A lightbulb glyph (smart-search indicator), filled+glowing when on.</summary>
        public static Path BulbPath(bool on)
        {
            var brush = on ? AccentBrush : TextMuted;
            var p = new Path
            {
                Data = Geometry.Parse("M9 18h6M10 22h4M12 2a7 7 0 0 1 4 12.7V17H8v-2.3A7 7 0 0 1 12 2z"),
                Stroke = brush,
                StrokeThickness = 1.7,
                Fill = on ? Br(Color.FromArgb(0x55, 0xE8, 0xB5, 0x4B)) : Brushes.Transparent,
                Stretch = Stretch.Uniform,
                Width = 20,
                Height = 20
            };
            if (on) p.Effect = new DropShadowEffect { Color = Accent, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7 };
            return p;
        }

        /// <summary>Tier icon image (public/tiers/Tier{n}.png), or null if unavailable.</summary>
        public static Image TierIcon(int? tier, double height)
        {
            if (!tier.HasValue) return null;
            var bmp = ImageCache.Load(CardStore.TierIconPath(tier.Value), (int)Math.Ceiling(height * 2.5));
            if (bmp == null) return null;
            return new Image { Source = bmp, Height = height, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        }

        /// <summary>Render card text (with &lt;b&gt;/&lt;i&gt; tags and newlines) into a TextBlock's inlines.</summary>
        public static void SetCardText(TextBlock tb, string text)
        {
            tb.Inlines.Clear();
            if (string.IsNullOrEmpty(text)) return;

            bool bold = false, italic = false;
            var sb = new StringBuilder();
            Action flush = () =>
            {
                if (sb.Length == 0) return;
                Inline run = new Run(sb.ToString());
                if (bold) run = new Bold(run);
                if (italic) run = new Italic(run);
                tb.Inlines.Add(run);
                sb.Clear();
            };

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<')
                {
                    int close = text.IndexOf('>', i);
                    if (close < 0) { sb.Append(c); continue; }
                    string tag = text.Substring(i + 1, close - i - 1).Trim().ToLowerInvariant();
                    flush();
                    if (tag == "b") bold = true;
                    else if (tag == "/b") bold = false;
                    else if (tag == "i") italic = true;
                    else if (tag == "/i") italic = false;
                    else if (tag == "br" || tag == "br/" || tag == "br /") tb.Inlines.Add(new LineBreak());
                    // any other tag is stripped
                    i = close;
                }
                else if (c == '\n') { flush(); tb.Inlines.Add(new LineBreak()); }
                else if (c == '\r') { /* skip */ }
                else sb.Append(c);
            }
            flush();
        }

        /// <summary>A thin, themed ScrollBar style to apply to a window's (or element's) resources.</summary>
        public static Style ThinScrollBarStyle(int width = 7)
        {
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
                "<Setter Property='Width' Value='" + width + "'/>" +
                "<Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ScrollBar'>" +
                "<Grid Background='{TemplateBinding Background}'>" +
                "<Track Name='PART_Track' IsDirectionReversed='True'>" +
                "<Track.Thumb><Thumb MinHeight='28'><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Border CornerRadius='3' Background='#6E7890' Margin='2,0,1,0'/>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0'/></Track.IncreaseRepeatButton>" +
                "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0'/></Track.DecreaseRepeatButton>" +
                "</Track></Grid></ControlTemplate>" +
                "</Setter.Value></Setter></Style>";
            return (Style)XamlReader.Parse(xaml);
        }

        /// <summary>Result-row thumbnail: the real card art (cached), falling back to the
        /// card's initial when art isn't available locally.</summary>
        public static Border Thumb(BgCard c, double size)
        {
            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x2A, 0x33, 0x46), Color.FromRgb(0x16, 0x1C, 0x28), 90),
                BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(1),
                ClipToBounds = true
            };

            var bmp = ImageCache.Load(CardStore.ResolveImagePath(c, false), (int)Math.Ceiling(size * 2.5));
            if (bmp != null)
            {
                // UniformToFill = fill the square and crop, so the card art reads as an icon
                // rather than a tiny letterboxed whole card. Bias upward toward the art.
                border.Child = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill,
                    VerticalAlignment = VerticalAlignment.Top
                };
            }
            else
            {
                string initial = string.IsNullOrEmpty(c.Name) ? "?" : c.Name.Substring(0, 1);
                border.Child = new TextBlock
                {
                    Text = initial,
                    Foreground = TextSecondary,
                    FontSize = size * 0.42,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            return border;
        }

        /// <summary>A small icon Image from a file path, or null if it can't load.</summary>
        public static Image IconImage(string path, double size)
        {
            var bmp = ImageCache.Load(path, (int)Math.Ceiling(size * 2));
            if (bmp == null) return null;
            return new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.Uniform };
        }

        /// <summary>Tier badge, or a muted dash when the card has no tavern tier.</summary>
        public static UIElement TierElement(int? tier, double size = 22)
        {
            if (!tier.HasValue)
            {
                return new Border
                {
                    Width = size, Height = size,
                    CornerRadius = new CornerRadius(size / 2),
                    Background = Br(RowBg),
                    Child = new TextBlock
                    {
                        Text = "–", Foreground = TextMuted, FontSize = size * 0.5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }
            return new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = Br(TierColor(tier.Value)),
                Child = new TextBlock
                {
                    Text = tier.Value.ToString(),
                    Foreground = Brushes.Black, FontSize = size * 0.55, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        public static Border Chip(string text, bool active = false)
        {
            return new Border
            {
                Background = active ? Br(Accent) : Br(RowBg),
                BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = text, FontSize = 12, Foreground = active ? Brushes.Black : TextSecondary }
            };
        }

        /// <summary>A clickable filter chip (optional icon + label) that toggles its own
        /// active state and reports the new state via <paramref name="onToggle"/>.</summary>
        public static Border ToggleChip(string label, string iconPath, bool active, Action<bool> onToggle)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = IconImage(iconPath, 16);
            if (icon != null) { icon.Margin = new Thickness(0, 0, 5, 0); content.Children.Add(icon); }
            var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(text);

            var chip = new Border
            {
                BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Child = content
            };

            bool state = active;
            Action apply = () =>
            {
                chip.Background = state ? Br(Accent) : Br(RowBg);
                text.Foreground = state ? Brushes.Black : TextSecondary;
            };
            apply();
            chip.MouseLeftButtonUp += (s, e) => { state = !state; apply(); onToggle(state); };
            return chip;
        }

        /// <summary>A clickable result row (card-art thumb + name + subtitle + tier icon).</summary>
        public static Border ResultRow(BgCard c, double thumbSize, Action<BgCard> onClick)
        {
            var dock = new DockPanel { LastChildFill = true };

            var thumb = Thumb(c, thumbSize);
            thumb.Margin = new Thickness(0, 0, 12, 0);
            if (c.Tier.HasValue)
                thumb.Effect = new DropShadowEffect { Color = TierColor(c.Tier.Value), BlurRadius = 11, ShadowDepth = 0, Opacity = 0.55 };
            DockPanel.SetDock(thumb, Dock.Left);
            dock.Children.Add(thumb);

            // Tier as the real icon (no arabic number).
            var tier = TierIcon(c.Tier, thumbSize * 0.52);
            if (tier != null)
            {
                tier.Margin = new Thickness(10, 0, 2, 0);
                DockPanel.SetDock(tier, Dock.Right);
                dock.Children.Add(tier);
            }

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = c.Name, Foreground = TextPrimary, FontSize = 17, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = SubtitleFor(c), Foreground = TextSecondary, FontSize = 14, Margin = new Thickness(0, 2, 0, 0)
            });
            dock.Children.Add(info);

            var row = new Border
            {
                Background = Br(RowBg),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 7),
                Child = dock
            };

            if (onClick != null)
            {
                row.Cursor = Cursors.Hand;
                row.MouseEnter += (s, e) => row.Background = Br(RowBgHover);
                row.MouseLeave += (s, e) => row.Background = Br(RowBg);
                row.MouseLeftButtonUp += (s, e) => onClick(c);
            }
            return row;
        }

        /// <summary>Rebuild a results container from a list, capped, with a "+N more" footer.</summary>
        public static void PopulateResults(Panel container, IList<BgCard> cards, double thumbSize,
            Action<BgCard> onClick, int cap, string emptyHint)
        {
            container.Children.Clear();
            if (cards == null || cards.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = emptyHint, Foreground = TextMuted, FontSize = 13,
                    Margin = new Thickness(2, 8, 0, 0), TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            int shown = Math.Min(cap, cards.Count);
            for (int i = 0; i < shown; i++)
                container.Children.Add(ResultRow(cards[i], thumbSize, onClick));

            if (cards.Count > shown)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "+ " + (cards.Count - shown) + " more — refine your search",
                    Foreground = TextMuted, FontSize = 12, Margin = new Thickness(2, 4, 0, 0)
                });
            }
        }

        /// <summary>
        /// A virtualized results ListBox: only visible rows are realized, so big result sets
        /// don't decode hundreds of card PNGs at once. Set <c>ItemsSource</c> to a list of
        /// BgCard; <paramref name="onSelect"/> fires on selection (click or arrow keys).
        /// </summary>
        public static ListBox CreateResultsList(double thumbSize, Action<BgCard> onSelect)
        {
            var thumbConv = new ThumbConverter((int)Math.Ceiling(thumbSize * 1.3));
            var tierConv = new TierIconConverter(64);
            var subConv = new SubtitleConverter();

            var dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(DockPanel.LastChildFillProperty, true);

            var thumbBorder = new FrameworkElementFactory(typeof(Border));
            thumbBorder.SetValue(FrameworkElement.WidthProperty, thumbSize);
            thumbBorder.SetValue(FrameworkElement.HeightProperty, thumbSize);
            thumbBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            thumbBorder.SetValue(Border.ClipToBoundsProperty, true);
            thumbBorder.SetValue(Border.BackgroundProperty, Br(RowBgHover));
            thumbBorder.SetValue(DockPanel.DockProperty, Dock.Left);
            thumbBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
            var thumbImg = new FrameworkElementFactory(typeof(Image));
            thumbImg.SetValue(Image.StretchProperty, Stretch.UniformToFill);
            thumbImg.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            thumbImg.SetBinding(Image.SourceProperty, new Binding { Converter = thumbConv });
            thumbBorder.AppendChild(thumbImg);
            dock.AppendChild(thumbBorder);

            var tierImg = new FrameworkElementFactory(typeof(Image));
            tierImg.SetValue(FrameworkElement.HeightProperty, thumbSize * 0.5);
            tierImg.SetValue(Image.StretchProperty, Stretch.Uniform);
            tierImg.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            tierImg.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 2, 0));
            tierImg.SetValue(DockPanel.DockProperty, Dock.Right);
            tierImg.SetBinding(Image.SourceProperty, new Binding { Converter = tierConv });
            dock.AppendChild(tierImg);

            var info = new FrameworkElementFactory(typeof(StackPanel));
            info.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetValue(TextBlock.ForegroundProperty, TextPrimary);
            name.SetValue(TextBlock.FontSizeProperty, 16.0);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            info.AppendChild(name);
            var sub = new FrameworkElementFactory(typeof(TextBlock));
            sub.SetValue(TextBlock.ForegroundProperty, TextSecondary);
            sub.SetValue(TextBlock.FontSizeProperty, 13.5);
            sub.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
            sub.SetBinding(TextBlock.TextProperty, new Binding { Converter = subConv });
            info.AppendChild(sub);
            dock.AppendChild(info);

            var dt = new DataTemplate(typeof(BgCard)) { VisualTree = dock };

            var lb = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemTemplate = dt,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemContainerStyle = ListItemStyle()
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Disabled);
            ScrollViewer.SetCanContentScroll(lb, true);
            VirtualizingStackPanel.SetIsVirtualizing(lb, true);
            VirtualizingStackPanel.SetVirtualizationMode(lb, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(lb, ScrollUnit.Pixel);   // smooth, normal-looking scrollbar
            lb.SelectionChanged += (s, e) => { if (lb.SelectedItem is BgCard c) onSelect?.Invoke(c); };
            return lb;
        }

        /// <summary>
        /// A virtualized card GRID. ItemsSource must be the rows (each an IList&lt;BgCard&gt; of
        /// up to <paramref name="columns"/> cards) — chunk the results yourself. The vertical
        /// VirtualizingStackPanel virtualizes the rows, so only visible rows decode art.
        /// Each cell shows the card art at <paramref name="artWidth"/>px + the name underneath.
        /// </summary>
        public static ListBox CreateCardGrid(int columns, int decodeWidth, Action<BgCard> onSelect)
        {
            var flat = FlatButtonTemplate();

            var row = new FrameworkElementFactory(typeof(UniformGrid));
            row.SetValue(UniformGrid.ColumnsProperty, columns);
            for (int i = 0; i < columns; i++)
                row.AppendChild(GridCell(i, decodeWidth, flat, onSelect));
            var dt = new DataTemplate { VisualTree = row };

            var lb = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemTemplate = dt,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemContainerStyle = GridItemStyle()
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Disabled);
            ScrollViewer.SetCanContentScroll(lb, true);
            VirtualizingStackPanel.SetIsVirtualizing(lb, true);
            VirtualizingStackPanel.SetVirtualizationMode(lb, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(lb, ScrollUnit.Pixel);   // smooth, normal-looking scrollbar
            return lb;
        }

        private static FrameworkElementFactory GridCell(int index, int decodeWidth,
            ControlTemplate flat, Action<BgCard> onSelect)
        {
            string path = "[" + index + "]";   // this cell's slot within the row item (an IList<BgCard>)
            var cell = new FrameworkElementFactory(typeof(Button));
            cell.SetValue(Button.TemplateProperty, flat);
            cell.SetValue(Button.CursorProperty, Cursors.Hand);
            // Non-focusable: clicking still fires, but won't grab focus and scroll the row into
            // view (which otherwise moves the card out from under the cursor before mouse-up).
            cell.SetValue(UIElement.FocusableProperty, false);
            cell.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
            // Empty slots (partial last row) collapse so they aren't clickable dead boxes.
            cell.SetBinding(UIElement.VisibilityProperty,
                new Binding(path) { Converter = new NullToCollapsedConverter(), FallbackValue = Visibility.Collapsed });
            cell.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var row = ((FrameworkElement)s).DataContext as System.Collections.Generic.IList<BgCard>;
                if (row != null && index < row.Count && row[index] != null) onSelect?.Invoke(row[index]);
            }));

            var sp = new FrameworkElementFactory(typeof(StackPanel));
            var img = new FrameworkElementFactory(typeof(Image));
            img.SetValue(Image.StretchProperty, Stretch.Uniform);          // fill the column width
            img.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            // Art loads asynchronously (downloaded WebP, disk-cached) via the attached behavior,
            // which is virtualization-safe: a recycled cell restarts its load and drops stale ones.
            img.SetValue(ArtImage.DecodeProperty, decodeWidth);
            img.SetBinding(ArtImage.CardProperty, new Binding(path));
            sp.AppendChild(img);
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding(path + ".Name"));
            name.SetValue(TextBlock.ForegroundProperty, TextPrimary);
            name.SetValue(TextBlock.FontSizeProperty, 15.0);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            name.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            name.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            name.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0));
            sp.AppendChild(name);
            cell.AppendChild(sp);
            return cell;
        }

        private static ControlTemplate FlatButtonTemplate()
        {
            const string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Border x:Name='bd' Background='Transparent' CornerRadius='8' Padding='3'>" +
                "<ContentPresenter HorizontalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#242E42'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>";
            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        private static Style GridItemStyle()
        {
            const string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'>" +
                "<Setter Property='Margin' Value='0'/>" +
                "<Setter Property='Padding' Value='0'/>" +
                "<Setter Property='Focusable' Value='False'/>" +
                "<Setter Property='HorizontalContentAlignment' Value='Stretch'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ListBoxItem'><ContentPresenter/></ControlTemplate>" +
                "</Setter.Value></Setter></Style>";
            return (Style)XamlReader.Parse(xaml);
        }

        private static Style ListItemStyle()
        {
            const string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'>" +
                "<Setter Property='Margin' Value='0,0,0,7'/>" +
                "<Setter Property='Cursor' Value='Hand'/>" +
                "<Setter Property='HorizontalContentAlignment' Value='Stretch'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ListBoxItem'>" +
                "<Border x:Name='bd' Background='#1A2130' CornerRadius='8' Padding='10' SnapsToDevicePixels='True'>" +
                "<ContentPresenter HorizontalAlignment='Stretch'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#242E42'/></Trigger>" +
                "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='#26344A'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>" +
                "</Setter.Value></Setter></Style>";
            return (Style)XamlReader.Parse(xaml);
        }

        public static TextBlock VariantTag(string hotkey, string label)
        {
            return new TextBlock
            {
                Text = hotkey + "  ·  " + label + "   ·   Esc / hotkey to close",
                Foreground = TextMuted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right
            };
        }
    }
}

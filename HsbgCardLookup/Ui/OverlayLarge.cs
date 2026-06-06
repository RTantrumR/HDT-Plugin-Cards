using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Shapes;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Hotkey;
using HsbgCardLookup.Net;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// F3 — full browser. Search + filter dropdowns share the top row; results are a virtualized
    /// 3-column card grid (150px art + name); the detail pane is card art + golden toggle
    /// (minions) + related cards.
    /// </summary>
    public sealed class OverlayLarge : OverlayBase
    {
        private const int GridColumns = 3;

        private readonly CardStore _store;
        private readonly PluginConfig _config;
        private readonly HotkeyManager _hotkey;
        private readonly FloatingCardManager _floating;
        private readonly Action _openSettings;   // opens the SettingsWindow (from the corner link)
        private readonly string _version;        // shown small in the footer corner

        // Drag-out state for the detail art (distinguishes a click → website from a drag → floating card).
        private Point _artDown;
        private bool _artDragging;
        private FloatingCard _dragCard;

        // Drag-out state for the results grid (distinguishes a click → select from a drag → floating card).
        private Point _gridDown;
        private bool _gridDragging;
        private BgCard _gridDownCard;
        private BitmapSource _gridDownArt;
        private double _gridDownWidth;
        private FloatingCard _gridDragCard;

        private readonly TextBox _search;
        private ListBox _grid;
        private TextBlock _hint;
        private TextBlock _status;
        private Border _notice;            // update banner (auto-update success / restart / failed)
        private Border _bulbHost;
        private Border _clearSearch;
        private Border _searchField;                          // for the "active input" border tint

        // Notification bell (manual patch-notes notices), right of search.
        private Border _bell;
        private Border _bellBadge;
        private TextBlock _bellCount;
        private Path _bellGlyph;
        private Popup _bellPopup;
        private List<PluginNotice> _notices;
        private bool _smart = true;
        private readonly DispatcherTimer _debounce;

        // Filters
        private string _type, _tribe, _spellSchool, _trinketTier;
        private int? _tier;
        private Dropdown _tribeDd, _spellDd, _trinketDd;

        // Detail
        private BgCard _selected;
        private bool _golden;
        private Image _art;
        private Border _goldenBtn;
        private TextBlock _goldenLabel;
        private TextBlock _relatedHeader;
        private UniformGrid _relatedPanel;
        private Popup _helpPopup;       // keybinds & tips, anchored to the detail toolbar's "?"
        private Border _helpAnchor;

        public OverlayLarge(CardStore store, PluginConfig config, HotkeyManager hotkey, FloatingCardManager floating,
                            Action openSettings, string version) : base(880, 800)
        {
            _store = store;
            _config = config;
            _hotkey = hotkey;
            _floating = floating;
            _openSettings = openSettings;
            _version = version;

            try { Resources[typeof(ScrollBar)] = UiKit.ThinScrollBarStyle(); } catch { }

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Refresh(); };

            var grid = new Grid { Margin = new Thickness(20, 22, 20, 18) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // search + filters
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // body (gets the freed footer space)

            // ── Top row: search (fill) + filter dropdowns (right) ──
            var topRow = new DockPanel { LastChildFill = true };
            var dds = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(10, 0, 0, 0) };
            BuildDropdownsInto(dds);
            DockPanel.SetDock(dds, Dock.Right);
            topRow.Children.Add(dds);
            // Bell sits between the search box and the filter dropdowns (docked right, after dds, so
            // it lands to the left of the filters). Hidden until there's an unread notification.
            var bell = BuildBell();
            DockPanel.SetDock(bell, Dock.Right);
            topRow.Children.Add(bell);
            _bulbHost = new Border
            {
                Cursor = Cursors.Hand, Padding = new Thickness(4), Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Smart search (Tab): parse t3, 5/5, tribes, keywords. Off = plain text."
            };
            _bulbHost.MouseLeftButtonUp += (s, e) => ToggleSmart();
            // Clear-search "✕" sits just left of the lamp; visible only when there's text.
            _clearSearch = UiKit.ClearButton(() => { _search.Clear(); FocusSearch(); }, "Clear search");
            _clearSearch.Visibility = Visibility.Collapsed;
            var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            trailing.Children.Add(_clearSearch);
            trailing.Children.Add(_bulbHost);
            _searchField = UiKit.SearchField("Search: name, tribe, keyword, t3, 5/5…", out _search, 18, trailing);
            topRow.Children.Add(_searchField);
            Grid.SetRow(topRow, 0);
            grid.Children.Add(topRow);

            // ── Body: card grid | detail ──
            var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(312) });

            _grid = UiKit.CreateCardGrid(GridColumns, 256, OnCardClicked);   // decode 256 for crisp downscale
            // Optional drag-out from grid cells (off by default). Preview handlers so we can start a
            // drag before the cell's Click selects; capture suppresses the select when a drag happens.
            _grid.PreviewMouseLeftButtonDown += Grid_Down;
            _grid.PreviewMouseMove += Grid_Move;
            _grid.PreviewMouseLeftButtonUp += Grid_Up;
            _hint = new TextBlock
            {
                Foreground = UiKit.TextMuted, FontSize = 14, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed
            };
            var gridArea = new Grid();
            gridArea.Children.Add(_grid);
            gridArea.Children.Add(_hint);

            _status = new TextBlock { Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(2, 0, 0, 8) };
            var resultsCol = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(_status, Dock.Top);
            resultsCol.Children.Add(_status);
            resultsCol.Children.Add(gridArea);
            Grid.SetColumn(resultsCol, 0);
            body.Children.Add(resultsCol);

            var detail = BuildDetail();
            Grid.SetColumn(detail, 1);
            body.Children.Add(detail);

            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            // Update banner sits above everything, collapsed until there's something to say.
            _notice = new Border { Visibility = Visibility.Collapsed };
            var withNotice = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_notice, Dock.Top);
            withNotice.Children.Add(_notice);
            withNotice.Children.Add(grid);

            SetRoot(UiKit.Panel(withNotice));

            _search.TextChanged += (s, e) =>
            {
                _debounce.Stop(); _debounce.Start();
                _clearSearch.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Collapsed : Visibility.Visible;
            };
            // The overlay takes real keyboard focus (via OverlayBase's thread-attach trick) without
            // taking the OS foreground, so the search box gets native text input (any language/IME).
            // Focus it + show the active indicator on each open.
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { FocusSearch(); SetInputActive(true); }
                else SetInputActive(false);
                _floating?.OnAppVisibilityChanged(IsVisible);   // hide/restore floating cards with the app
            };
            PreviewKeyDown += OnKey;

            UpdateBulb();
            ApplyTypeContext();
            Refresh();
        }

        private void BuildDropdownsInto(StackPanel dds)
        {
            var typeItems = _store.Types.Select(t => new Dropdown.Item(t, UiKit.Pretty(t))).ToList();
            dds.Children.Add(new Dropdown(this, "All Types", typeItems, v =>
            {
                _type = v;
                if (v != "minion") { _tribe = null; _tribeDd?.SetSelected(null); }
                if (v != "spell") { _spellSchool = null; _spellDd?.SetSelected(null); }
                if (v != "trinket") { _trinketTier = null; _trinketDd?.SetSelected(null); }
                ApplyTypeContext();
                Refresh();
            }));

            var tierItems = _store.Tiers.Select(t => new Dropdown.Item(t.ToString(), "Tier " + t, CardStore.TierIconPath(t))).ToList();
            dds.Children.Add(new Dropdown(this, "All Tiers", tierItems, v => { _tier = v == null ? (int?)null : int.Parse(v); Refresh(); }, 26));

            var tribeItems = _store.Tribes.Select(t => new Dropdown.Item(t, t, CardStore.TribeIconPath(t))).ToList();
            _tribeDd = new Dropdown(this, "All Tribes", tribeItems, v => { _tribe = v; Refresh(); }, 22);
            dds.Children.Add(_tribeDd);

            var spellItems = _store.SpellSchools.Select(s => new Dropdown.Item(s, s)).ToList();
            _spellDd = new Dropdown(this, "All Schools", spellItems, v => { _spellSchool = v; Refresh(); });
            dds.Children.Add(_spellDd);

            var trinketItems = new List<Dropdown.Item>
            {
                new Dropdown.Item("lesser", "Lesser"),
                new Dropdown.Item("greater", "Greater"),
            };
            _trinketDd = new Dropdown(this, "All Trinkets", trinketItems, v => { _trinketTier = v; Refresh(); });
            dds.Children.Add(_trinketDd);
        }

        private void ApplyTypeContext()
        {
            if (_tribeDd != null) _tribeDd.Visibility = _type == "minion" ? Visibility.Visible : Visibility.Collapsed;
            if (_spellDd != null) _spellDd.Visibility = _type == "spell" ? Visibility.Visible : Visibility.Collapsed;
            if (_trinketDd != null) _trinketDd.Visibility = _type == "trinket" ? Visibility.Visible : Visibility.Collapsed;
        }

        private UIElement BuildDetail()
        {
            var stack = new StackPanel();

            _art = new Image
            {
                Stretch = Stretch.Uniform,
                MaxWidth = 300,
                MaxHeight = 350,   // a bit shorter so a minion's Golden button + first related row fit without scroll
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "Click to open on hsbg.cards · drag out for a floating card"
            };
            // Detail portrait: a click deep-links to the website; a past-threshold drag pulls the card
            // out as a free-floating raw card (FloatingCard) that follows the cursor until mouse-up.
            _art.MouseLeftButtonDown += Art_Down;
            _art.MouseMove += Art_Move;
            _art.MouseLeftButtonUp += Art_Up;
            stack.Children.Add(_art);

            _goldenLabel = new TextBlock { Text = "☆ Golden", FontSize = 15, Foreground = UiKit.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
            _goldenBtn = new Border
            {
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(18),
                Background = UiKit.Br(UiKit.RowBg),
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Child = _goldenLabel
            };
            _goldenBtn.MouseLeftButtonUp += (s, e) => ToggleGolden();
            stack.Children.Add(_goldenBtn);

            _relatedHeader = new TextBlock
            {
                Text = "RELATED", Foreground = UiKit.AccentBrush, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 6), Visibility = Visibility.Collapsed
            };
            stack.Children.Add(_relatedHeader);
            _relatedPanel = new UniformGrid();   // Columns set per related-count in RenderDetail
            stack.Children.Add(_relatedPanel);

            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = stack,
                Margin = new Thickness(0, 0, 0, 0)
            };
            sv.Resources[typeof(ScrollBar)] = UiKit.ThinScrollBarStyle(5);   // extra-thin here

            // Detail column = scrollable card content + a small fixed toolbar pinned at the bottom
            // (settings gear · help · version), so it stays put regardless of scroll.
            var col = new DockPanel { LastChildFill = true };
            var toolbar = BuildDetailToolbar();
            DockPanel.SetDock(toolbar, Dock.Bottom);
            col.Children.Add(toolbar);
            col.Children.Add(sv);
            return col;
        }

        // Bottom toolbar of the detail pane: version (left), then a help "?" and a settings gear (right).
        private UIElement BuildDetailToolbar()
        {
            var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 8, 2, 0) };

            var version = new TextBlock
            {
                Text = "v" + (_version ?? "?"),
                Foreground = UiKit.TextMuted, FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(version, Dock.Left);
            bar.Children.Add(version);

            // Right side: gear (far right) then help (left of it).
            var gear = IconButton("⚙", "Settings", () => _openSettings?.Invoke());   // ⚙
            DockPanel.SetDock(gear, Dock.Right);
            bar.Children.Add(gear);

            _helpAnchor = HelpButton();
            DockPanel.SetDock(_helpAnchor, Dock.Right);
            bar.Children.Add(_helpAnchor);

            return bar;
        }

        // A muted icon glyph that brightens on hover and runs an action on click.
        private static Border IconButton(string glyph, string tip, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = glyph, FontSize = 16, Foreground = UiKit.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var b = new Border
            {
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Child = tb, ToolTip = tip
            };
            b.MouseEnter += (s, e) => tb.Foreground = UiKit.AccentBrush;
            b.MouseLeave += (s, e) => tb.Foreground = UiKit.TextMuted;
            b.MouseLeftButtonUp += (s, e) => onClick();
            return b;
        }

        // Circular "?" help button (toggles the keybinds/tips popup).
        private Border HelpButton()
        {
            var q = new TextBlock
            {
                Text = "?", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = UiKit.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var circle = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Margin = new Thickness(6, 0, 0, 0), Child = q, ToolTip = "Keybinds & tips"
            };
            circle.MouseEnter += (s, e) => { q.Foreground = UiKit.AccentBrush; circle.BorderBrush = UiKit.AccentBrush; };
            circle.MouseLeave += (s, e) => { if (_helpPopup == null || !_helpPopup.IsOpen) { q.Foreground = UiKit.TextMuted; circle.BorderBrush = UiKit.StrokeBrush; } };
            circle.MouseLeftButtonUp += (s, e) => ToggleHelpPopup();
            return circle;
        }

        private void ToggleHelpPopup()
        {
            if (_helpPopup == null)
            {
                _helpPopup = new Popup
                {
                    PlacementTarget = _helpAnchor,
                    Placement = PlacementMode.Top,
                    StaysOpen = false,
                    AllowsTransparency = true,
                    PopupAnimation = PopupAnimation.Fade,
                    VerticalOffset = -6
                };
                _helpPopup.Closed += (s, e) => EndPopup();   // release the overlay's focus-loss guard
            }
            if (_helpPopup.IsOpen) { _helpPopup.IsOpen = false; return; }
            _helpPopup.Child = BuildHelpContent();
            BeginPopup();
            _helpPopup.IsOpen = true;
        }

        private FrameworkElement BuildHelpContent()
        {
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            list.Children.Add(HelpHeader("KEYBINDS"));
            list.Children.Add(HelpRow("Open / close overlay", _config.BrowserKey));
            list.Children.Add(HelpRow("Smart search on / off", "Tab"));
            list.Children.Add(HelpRow("Open first result", "Enter"));
            list.Children.Add(HelpRow("Close", "Esc"));
            list.Children.Add(HelpRow("Toggle golden", _config.GoldenKey));
            list.Children.Add(HelpRow("Focus search box", _config.FocusKey));
            list.Children.Add(HelpRow("Open card on hsbg.cards", "click art"));

            list.Children.Add(HelpHeader("TIPS"));
            list.Children.Add(HelpNote("Smart search: try t3 · 5/5 · a tribe or keyword · cost 2"));
            list.Children.Add(HelpNote("Drag a card out of the art to pin it on screen — drag its top-right corner to resize, right-click to dismiss."));
            list.Children.Add(HelpNote("Trinkets/anomaly HUD and grid drag-out are toggled in Settings (⚙)."));

            return new Border
            {
                Background = new LinearGradientBrush(UiKit.PanelBg2, UiKit.PanelBg, 90),
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Width = 300,
                Child = new ScrollViewer
                {
                    MaxHeight = 440,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = list
                }
            };
        }

        private static TextBlock HelpHeader(string t) => new TextBlock
        {
            Text = t, Foreground = UiKit.AccentBrush, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(14, 11, 14, 6)
        };

        private static UIElement HelpRow(string label, string key)
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(14, 2, 14, 2) };
            var chip = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock { Text = KeyDisplay(key), Foreground = UiKit.TextPrimary, FontSize = 11.5 }
            };
            DockPanel.SetDock(chip, Dock.Right);
            dock.Children.Add(chip);
            dock.Children.Add(new TextBlock
            {
                Text = label, Foreground = UiKit.TextSecondary, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center
            });
            return dock;
        }

        private static TextBlock HelpNote(string t) => new TextBlock
        {
            Text = t, Foreground = UiKit.TextMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 3, 14, 3)
        };

        private static string KeyDisplay(string ks) =>
            string.IsNullOrEmpty(ks) || ks == "None" ? "—" : ks;

        // Window-level key handling. The search box holds real keyboard focus, so it natively consumes
        // character keys (any language). Esc/Tab/Enter always act here; the letter-bound Golden and
        // Focus keys act ONLY when the search box isn't focused (so typing 'g'/'s' in a query still
        // works) — focus leaves the box when you click a card or press Enter.
        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; return; }
            if (e.Key == Key.Tab) { ToggleSmart(); e.Handled = true; return; }
            if (e.Key == Key.Enter) { SelectFirstResult(); e.Handled = true; return; }

            bool searchFocused = _search.IsKeyboardFocused;

            // Focus key (default S): return keyboard focus to the search box.
            if (e.Key == _config.FocusKeyParsed && !searchFocused)
            {
                FocusSearch();
                e.Handled = true;
                return;
            }

            // Golden key (default G): toggle golden art of the selected minion.
            if (e.Key == _config.GoldenKeyParsed && !searchFocused
                && _selected != null && _selected.CardType == "minion" && _selected.HasGoldenDiff)
            {
                ToggleGolden();
                e.Handled = true;
            }
        }

        private void SelectFirstResult()
        {
            var first = (_grid?.Items != null && _grid.Items.Count > 0)
                ? (_grid.Items[0] as System.Collections.Generic.IList<BgCard>)?.FirstOrDefault(c => c != null)
                : null;
            if (first != null) OnCardClicked(first);
        }

        // Focus the search box on open (deferred so it lands after the window's thread-attach focus
        // grab), selecting existing text so the user can immediately type a fresh query.
        private void FocusSearch()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _search.Focus(); Keyboard.Focus(_search); _search.SelectAll(); } catch { }
            }), DispatcherPriority.Input);
        }

        // Accent border on the search box while the overlay is open (the real OS caret shows now
        // that the window takes focus, so no fake caret is needed).
        private void SetInputActive(bool active)
        {
            try
            {
                if (_searchField == null) return;
                _searchField.BorderBrush = active ? UiKit.AccentBrush : UiKit.StrokeBrush;
                _searchField.BorderThickness = new Thickness(active ? 1.5 : 1);
            }
            catch { }
        }

        /// <summary>Re-run the current query/browse (e.g. after the Show-Duos setting changes).</summary>
        public void RefreshPool() => Refresh();

        // ── Notification bell (manual patch-notes notices) ──

        private UIElement BuildBell()
        {
            _bellGlyph = new Path
            {
                // Feather "bell": body + clapper.
                Data = Geometry.Parse("M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9 M13.73 21a2 2 0 0 1-3.46 0"),
                Stroke = UiKit.TextMuted,
                StrokeThickness = 1.8,
                Stretch = Stretch.Uniform,
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _bellCount = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            _bellBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x3A)),
                CornerRadius = new CornerRadius(7), MinWidth = 14, Height = 14,
                Padding = new Thickness(3, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0), Child = _bellCount, Visibility = Visibility.Collapsed
            };
            var box = new Grid { Width = 26, Height = 24 };
            box.Children.Add(_bellGlyph);
            box.Children.Add(_bellBadge);

            _bell = new Border
            {
                Cursor = Cursors.Hand, Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                Child = box, Visibility = Visibility.Collapsed,
                ToolTip = "Notifications"
            };
            _bell.MouseEnter += (s, e) => _bellGlyph.Stroke = UiKit.AccentBrush;
            _bell.MouseLeave += (s, e) => { if (_bellPopup == null || !_bellPopup.IsOpen) _bellGlyph.Stroke = UiKit.TextMuted; };

            _bellPopup = new Popup
            {
                PlacementTarget = _bell,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                VerticalOffset = 6
            };
            // Guard the overlay's focus-loss auto-hide while the popup is open, and mark everything
            // shown as seen on close (the user has now viewed the panel → badge clears next refresh).
            _bellPopup.Closed += (s, e) =>
            {
                _ownerEndPopup();
                MarkShownSeen();
                UpdateBell();
            };

            _bell.MouseLeftButtonUp += (s, e) => ToggleBellPopup();
            return _bell;
        }

        // BeginPopup/EndPopup live on OverlayBase; tiny wrappers so the lambda above reads cleanly.
        private void _ownerEndPopup() => EndPopup();

        /// <summary>Receive the notices discovered at load; refresh the bell.</summary>
        public void SetNotices(List<PluginNotice> notices) { _notices = notices; UpdateBell(); }

        // Fresh + active notices, newest first — what the popup lists.
        private List<PluginNotice> Shown() =>
            (_notices ?? new List<PluginNotice>())
                .Where(n => n != null && n.Active && IsFresh(n))
                .OrderByDescending(n => n.Id).ToList();

        private List<PluginNotice> Unread() =>
            Shown().Where(n => n.Id > _config.PatchNoticeLastSeenId).ToList();

        private static bool IsFresh(PluginNotice n)
        {
            if (string.IsNullOrEmpty(n.Date)) return true;   // no date → treat as current
            if (DateTime.TryParse(n.Date, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
                return (DateTime.UtcNow - d.ToUniversalTime()).TotalDays <= 7;
            return true;
        }

        private void UpdateBell()
        {
            if (_bell == null) return;
            var unread = Unread();
            // Keep the bell visible while the popup is open even after marking seen, so the popup
            // (anchored to the bell) doesn't lose its placement target mid-view.
            bool keepForPopup = _bellPopup != null && _bellPopup.IsOpen;
            if (unread.Count == 0 && !keepForPopup) { _bell.Visibility = Visibility.Collapsed; return; }
            _bell.Visibility = Visibility.Visible;
            _bellBadge.Visibility = unread.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _bellCount.Text = unread.Count > 9 ? "9+" : unread.Count.ToString();
        }

        private void MarkShownSeen()
        {
            var shown = Shown();
            if (shown.Count == 0) return;
            int maxId = shown.Max(n => n.Id);
            if (maxId > _config.PatchNoticeLastSeenId)
            {
                _config.PatchNoticeLastSeenId = maxId;
                _config.Save();
            }
        }

        // First click opens the panel listing notifications; clicking a row there opens its link.
        private void ToggleBellPopup()
        {
            if (_bellPopup == null) return;
            if (_bellPopup.IsOpen) { _bellPopup.IsOpen = false; return; }
            _bellPopup.Child = BuildBellPopupContent();
            BeginPopup();
            _bellPopup.IsOpen = true;
        }

        private FrameworkElement BuildBellPopupContent()
        {
            var list = new StackPanel();
            list.Children.Add(new TextBlock
            {
                Text = "NOTIFICATIONS",
                Foreground = UiKit.AccentBrush, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(14, 11, 14, 6)
            });

            var shown = Shown();
            if (shown.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = "No new notifications.", Foreground = UiKit.TextMuted, FontSize = 13,
                    Margin = new Thickness(14, 2, 14, 12)
                });
            else
                foreach (var n in shown) list.Children.Add(NotificationRow(n));

            return new Border
            {
                Background = new LinearGradientBrush(UiKit.PanelBg2, UiKit.PanelBg, 90),
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Width = 340,
                Child = new ScrollViewer
                {
                    MaxHeight = 380,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = list
                }
            };
        }

        private Border NotificationRow(PluginNotice n)
        {
            bool unread = n.Id > _config.PatchNoticeLastSeenId;
            var col = new StackPanel();

            var titleRow = new DockPanel { LastChildFill = true };
            if (unread)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x3A)),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
                };
                DockPanel.SetDock(dot, Dock.Left);
                titleRow.Children.Add(dot);
            }
            titleRow.Children.Add(new TextBlock
            {
                Text = n.Title ?? "Notification",
                Foreground = UiKit.TextPrimary, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            col.Children.Add(titleRow);

            if (!string.IsNullOrEmpty(n.Description))
                col.Children.Add(new TextBlock
                {
                    Text = n.Description, Foreground = UiKit.TextSecondary, FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
                });

            string when = FormatNoticeDate(n.Date);
            var meta = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 0) };
            if (!string.IsNullOrEmpty(when))
            {
                var dateText = new TextBlock { Text = when, Foreground = UiKit.TextMuted, FontSize = 11 };
                DockPanel.SetDock(dateText, Dock.Left);
                meta.Children.Add(dateText);
            }
            var open = new TextBlock
            {
                Text = "Open →", Foreground = UiKit.AccentBrush, FontSize = 11.5, FontWeight = FontWeights.SemiBold
            };
            DockPanel.SetDock(open, Dock.Right);
            meta.Children.Add(open);
            col.Children.Add(meta);

            var row = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(14, 9, 14, 9),
                Cursor = Cursors.Hand,
                Child = col
            };
            row.MouseEnter += (s, e) => row.Background = UiKit.Br(UiKit.RowBgHover);
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (s, e) => OpenNotice(n);
            return row;
        }

        private static string FormatNoticeDate(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d.ToLocalTime().ToString("MMM d, yyyy")
                : null;
        }

        private void OpenNotice(PluginNotice n)
        {
            try
            {
                string url = string.IsNullOrEmpty(n.Url)
                    ? AssetClient.SiteBase + "/patch-notes?utm_source=hdt&utm_medium=plugin&utm_campaign=patch-notes"
                    : n.Url;
                Process.Start(url);
            }
            catch { /* launching the browser is best-effort */ }
            // This notice is now seen; closing the popup (StaysOpen=false) finalizes via MarkShownSeen.
            if (n.Id > _config.PatchNoticeLastSeenId)
            {
                _config.PatchNoticeLastSeenId = n.Id;
                _config.Save();
            }
            if (_bellPopup != null) _bellPopup.IsOpen = false;
        }

        /// <summary>Show a dismissible banner at the top of the overlay (auto-update status). An
        /// error notice gets a red tint and, when <paramref name="url"/> is set, a clickable
        /// manual-install link.</summary>
        public void SetUpdateNotice(string message, bool isError, string url)
        {
            if (_notice == null) return;
            if (string.IsNullOrEmpty(message)) { _notice.Visibility = Visibility.Collapsed; return; }

            var content = new DockPanel { LastChildFill = true, Margin = new Thickness(14, 9, 8, 9) };
            var dismiss = UiKit.ClearButton(() => { _notice.Visibility = Visibility.Collapsed; }, "Dismiss");
            DockPanel.SetDock(dismiss, Dock.Right);
            content.Children.Add(dismiss);

            var text = new StackPanel { Orientation = Orientation.Vertical };
            text.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = UiKit.TextPrimary,
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(url))
            {
                var link = new TextBlock
                {
                    Text = url,
                    Foreground = UiKit.AccentBrush,
                    FontSize = 13,
                    Cursor = Cursors.Hand,
                    TextDecorations = TextDecorations.Underline,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                link.MouseLeftButtonUp += (s, e) => { try { Process.Start(url); } catch { } };
                text.Children.Add(link);
            }
            content.Children.Add(text);

            _notice.Child = content;
            _notice.Background = isError
                ? new SolidColorBrush(Color.FromRgb(0x4A, 0x1F, 0x1F))
                : UiKit.Br(UiKit.PanelActive);
            _notice.BorderBrush = isError ? new SolidColorBrush(Color.FromRgb(0x7A, 0x33, 0x33)) : UiKit.AccentBrush;
            _notice.BorderThickness = new Thickness(0, 0, 0, 1);
            _notice.Visibility = Visibility.Visible;
        }

        /// <summary>Open the selected card's page on hsbg.cards (UTM-tagged; content = slug for
        /// per-card click attribution). Best-effort — never throws into the UI.</summary>
        private static void OpenOnWebsite(BgCard card)
        {
            if (card == null || string.IsNullOrEmpty(card.Slug)) return;
            try
            {
                string slug = Uri.EscapeDataString(card.Slug);
                Process.Start("https://hsbg.cards/card/" + slug
                    + "?utm_source=hdt&utm_medium=plugin&utm_campaign=clickDetail&utm_content=" + slug);
            }
            catch { /* launching the default browser is best-effort */ }
        }

        // ── Detail-art drag-out → floating card ──────────────────────────────────────────────────
        // Capture on mouse-down; if the pointer moves past the system drag threshold we spawn a
        // FloatingCard and keep it under the cursor (the captured MouseMove keeps firing past the
        // window edge) until mouse-up. A press with no drag falls through to OpenOnWebsite.

        private void Art_Down(object sender, MouseButtonEventArgs e)
        {
            _artDown = e.GetPosition(this);
            _artDragging = false;
            _dragCard = null;
            _art.CaptureMouse();
        }

        private void Art_Move(object sender, MouseEventArgs e)
        {
            if (!_art.IsMouseCaptured) return;
            if (!_config.DragFromDetail) return;   // detail drag-out disabled → a press stays a click
            if (!_artDragging)
            {
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - _artDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _artDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _artDragging = true;
                // Spawn at the detail-pane's on-screen size so it doesn't appear blown up.
                _dragCard = _floating?.Spawn(_art.Source as BitmapSource, _art.ActualWidth);
            }
            _dragCard?.CenterOnCursor();
        }

        private void Art_Up(object sender, MouseButtonEventArgs e)
        {
            bool dragged = _artDragging;
            if (_art.IsMouseCaptured) _art.ReleaseMouseCapture();
            _artDragging = false;
            _dragCard = null;
            if (!dragged) OpenOnWebsite(_selected);   // plain click → website (unchanged)
        }

        // ── Results-grid drag-out → floating card (opt-in) ───────────────────────────────────────
        // We don't handle the down (so a plain click still selects via the cell's Click). On a
        // past-threshold move we capture the grid — which suppresses the pending cell click — spawn a
        // FloatingCard from the card under the cursor, and follow until mouse-up. Spawns from the cell's
        // visible (~256px) thumb for instant feedback, then upgrades to native-res art (UpgradeToNative).

        private void Grid_Down(object sender, MouseButtonEventArgs e)
        {
            _gridDragging = false;
            _gridDragCard = null;
            _gridDownCard = null;
            _gridDownArt = null;
            if (!_config.DragFromGrid) return;

            var img = FindCellImage(e.OriginalSource as DependencyObject);
            if (img == null) return;
            _gridDownCard = ArtImage.GetCard(img);
            // Prefer native art if it's already cached; else the visible thumb (upgraded to native
            // asynchronously once it loads — see Grid_Move).
            _gridDownArt = CardArt.GetSync(_gridDownCard, false, 0) ?? (img.Source as BitmapSource);
            _gridDownWidth = img.ActualWidth;
            _gridDown = e.GetPosition(this);
        }

        private void Grid_Move(object sender, MouseEventArgs e)
        {
            if (!_config.DragFromGrid) return;
            if (!_gridDragging)
            {
                if (_gridDownCard == null || _gridDownArt == null || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - _gridDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _gridDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _gridDragging = true;
                _grid.CaptureMouse();   // suppresses the cell's Click so a drag doesn't also select
                _gridDragCard = _floating?.Spawn(_gridDownArt, _gridDownWidth);
                UpgradeToNative(_gridDragCard, _gridDownCard);
            }
            _gridDragCard?.CenterOnCursor();
        }

        private void Grid_Up(object sender, MouseButtonEventArgs e)
        {
            bool dragged = _gridDragging;
            if (_grid.IsMouseCaptured) _grid.ReleaseMouseCapture();
            _gridDragging = false;
            _gridDragCard = null;
            _gridDownCard = null;
            _gridDownArt = null;
            if (dragged) e.Handled = true;   // we consumed this gesture as a drag, not a select
        }

        // Grid cells render a downscaled (~256px) thumb; once the full-res art loads, swap it into the
        // floating card so a scaled-up card stays crisp (the detail-pane drag already starts native).
        private static void UpgradeToNative(FloatingCard card, BgCard data)
        {
            if (card == null || data == null) return;
            CardArt.LoadAsync(data, false, 0).ContinueWith(t =>
            {
                var bmp = t.Result;
                if (bmp == null) return;
                card.Dispatcher.BeginInvoke(new Action(() => card.SetArt(bmp)));
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        // Walk up from the hit element to the cell's Button, then find its card Image (the one carrying
        // the ArtImage.Card attached property). Returns null when the press wasn't on a card cell.
        private static Image FindCellImage(DependencyObject hit)
        {
            var btn = FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(hit);
            return btn == null ? null : FindDescendantImage(btn);
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T))
                d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : null;
            return d as T;
        }

        private static Image FindDescendantImage(DependencyObject root)
        {
            if (root is Image im && ArtImage.GetCard(im) != null) return im;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var found = FindDescendantImage(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private void ToggleSmart() { _smart = !_smart; UpdateBulb(); Refresh(); }
        private void UpdateBulb() => _bulbHost.Child = UiKit.BulbPath(_smart);
        private void ToggleGolden() { _golden = !_golden; RenderDetail(); }

        private void Refresh()
        {
            string q = _search.Text?.Trim() ?? "";
            var pool = _store.Current.Where(PassesFilters).ToList();
            bool fuzzy = false;
            List<BgCard> cards;
            if (q.Length == 0)
                // Default/browse order: regular minions, then token/hero-power minions; regular
                // spells, then spellcraft/other spells; then heroes, hero powers, quests, rewards,
                // trinkets, anomalies. Within a group: tier then name. (See CardStore.BrowseRank.)
                cards = pool.OrderBy(CardStore.BrowseRank)
                            .ThenBy(c => c.Tier ?? 99)
                            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            else
            {
                var r = _smart ? SearchEngine.Smart(pool, q) : SearchEngine.Simple(pool, q);
                cards = r.Cards;
                fuzzy = r.IsFuzzy;
            }

            _status.Text = cards.Count + " result" + (cards.Count == 1 ? "" : "s") + (fuzzy ? " (fuzzy)" : "");

            if (cards.Count > 0)
            {
                _hint.Visibility = Visibility.Collapsed;
                _grid.ItemsSource = Chunk(cards, GridColumns);
                Select(cards[0]);
            }
            else
            {
                _grid.ItemsSource = null;
                bool noFilters = q.Length == 0 && _type == null && _tier == null && _tribe == null && _spellSchool == null;
                _hint.Text = noFilters ? "Start typing, or pick filters…" : "No matches. Try a different query or fewer filters.";
                _hint.Visibility = Visibility.Visible;
                _selected = null;
                ClearDetail();
            }
        }

        private static List<List<BgCard>> Chunk(List<BgCard> cards, int n)
        {
            var rows = new List<List<BgCard>>();
            for (int i = 0; i < cards.Count; i += n)
                rows.Add(cards.GetRange(i, Math.Min(n, cards.Count - i)));
            return rows;
        }

        private bool PassesFilters(BgCard c)
        {
            if (!_config.ShowDuos && c.IsDuosOnly) return false;
            if (_type != null && c.CardType != _type) return false;
            if (_tier.HasValue && !(c.Tier.HasValue && c.Tier.Value == _tier.Value)) return false;
            if (_tribe != null)
            {
                var mt = c.MinionTypes ?? new List<string>();
                bool ok = _tribe == "Neutral" ? (c.CardType == "minion" && mt.Count == 0) : mt.Contains(_tribe);
                if (!ok) return false;
            }
            if (_spellSchool != null && !string.Equals(c.SpellSchool, _spellSchool, StringComparison.OrdinalIgnoreCase)) return false;
            if (_trinketTier != null && !string.Equals(c.TrinketTier, _trinketTier, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private void Select(BgCard card)
        {
            if (card == null) return;
            _selected = card;
            _golden = false;
            RenderDetail();
        }

        // Invoked when the user actually clicks a card (grid or related). Selects it AND moves
        // keyboard focus off the search box, so the Golden (G) hotkey toggles golden instead of
        // typing 'g' into the search. (Auto-select after a search uses Select() and keeps focus.)
        private void OnCardClicked(BgCard card)
        {
            Select(card);
            _grid.Focus();
        }

        private void ClearDetail()
        {
            _art.Source = null;
            _goldenBtn.Visibility = Visibility.Collapsed;
            _relatedHeader.Visibility = Visibility.Collapsed;
            _relatedPanel.Children.Clear();
        }

        private void RenderDetail()
        {
            var c = _selected;
            if (c == null) { ClearDetail(); return; }

            bool goldenOn = _golden && c.CardType == "minion";
            _art.Source = null;
            var artNow = CardArt.GetSync(c, goldenOn, 0);   // memory / dev PNG / disk (full, native)
            if (artNow != null)
            {
                _art.Source = artNow;
            }
            else
            {
                // Not cached yet — fetch the full WebP in the background and apply only if the user
                // is still on this card and the same golden state when it arrives.
                var token = c;
                bool g = goldenOn;
                CardArt.LoadAsync(c, goldenOn, 0).ContinueWith(t =>
                {
                    var bmp = t.Result;
                    if (bmp == null) return;
                    _art.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        bool stillGold = _golden && _selected != null && _selected.CardType == "minion";
                        if (ReferenceEquals(_selected, token) && stillGold == g) _art.Source = bmp;
                    }));
                }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
            }

            bool canGold = c.CardType == "minion" && c.HasGoldenDiff;
            _goldenBtn.Visibility = canGold ? Visibility.Visible : Visibility.Collapsed;
            _goldenLabel.Text = (_golden ? "★ Golden" : "☆ Golden") + "  (" + _config.GoldenKey + ")";
            _goldenLabel.Foreground = _golden ? UiKit.AccentBrush : UiKit.TextSecondary;
            _goldenBtn.Background = _golden ? UiKit.Br(UiKit.PanelActive) : UiKit.Br(UiKit.RowBg);
            _goldenBtn.BorderBrush = _golden ? UiKit.AccentBrush : UiKit.StrokeBrush;

            _relatedPanel.Children.Clear();
            var related = _store.RelatedCards(c);
            _relatedHeader.Visibility = related.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // Two layouts only: 1-2 related -> 2 columns (so a lone card is sized like a pair),
            // 3+ -> 3 columns (wraps into rows).
            _relatedPanel.Columns = related.Count <= 2 ? 2 : 3;
            foreach (var r in related)
                _relatedPanel.Children.Add(RelatedItem(r));
        }

        private UIElement RelatedItem(BgCard card)
        {
            var stack = new StackPanel();   // fills its UniformGrid cell
            var thumb = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,   // fill the column width
                MaxHeight = 200
            };
            ArtImage.SetDecode(thumb, 256);
            ArtImage.SetCard(thumb, card);   // async load (cached WebP), same path as the grid
            stack.Children.Add(thumb);
            stack.Children.Add(new TextBlock
            {
                Text = card.Name, Foreground = UiKit.TextSecondary, FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });
            // Equal spacing between cells.
            var border = new Border { Child = stack, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(6) };
            border.MouseLeftButtonUp += (s, e) => OnCardClicked(card);
            return border;
        }
    }
}

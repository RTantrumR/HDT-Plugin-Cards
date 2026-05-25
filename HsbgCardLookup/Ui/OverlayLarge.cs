using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
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

        private readonly TextBox _search;
        private ListBox _grid;
        private TextBlock _hint;
        private TextBlock _status;
        private Border _bulbHost;
        private Border _clearSearch;
        private bool _smart = true;
        private readonly DispatcherTimer _debounce;

        // Filters
        private string _type, _tribe, _spellSchool;
        private int? _tier;
        private Dropdown _tribeDd, _spellDd;

        // Detail
        private BgCard _selected;
        private bool _golden;
        private Image _art;
        private Border _goldenBtn;
        private TextBlock _goldenLabel;
        private TextBlock _relatedHeader;
        private UniformGrid _relatedPanel;

        public OverlayLarge(CardStore store, PluginConfig config) : base(880, 800)
        {
            _store = store;
            _config = config;

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
            topRow.Children.Add(UiKit.SearchField("Search: name, tribe, keyword, t3, 5/5…", out _search, 18, trailing));
            Grid.SetRow(topRow, 0);
            grid.Children.Add(topRow);

            // ── Body: card grid | detail ──
            var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(312) });

            _grid = UiKit.CreateCardGrid(GridColumns, 256, OnCardClicked);   // decode 256 for crisp downscale
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

            SetRoot(UiKit.Panel(grid));

            _search.TextChanged += (s, e) =>
            {
                _debounce.Stop(); _debounce.Start();
                _clearSearch.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Collapsed : Visibility.Visible;
            };
            // Focus the search on every open (IsVisibleChanged fires on each Hide->Show; Activated
            // alone only reliably fired on the first open).
            Activated += (s, e) => FocusSearch();
            IsVisibleChanged += (s, e) => { if (IsVisible) FocusSearch(); };
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
        }

        private void ApplyTypeContext()
        {
            if (_tribeDd != null) _tribeDd.Visibility = _type == "minion" ? Visibility.Visible : Visibility.Collapsed;
            if (_spellDd != null) _spellDd.Visibility = _type == "spell" ? Visibility.Visible : Visibility.Collapsed;
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
                ToolTip = "Open this card on hsbg.cards"
            };
            // Detail portrait only (not grid, not related) deep-links to the website.
            _art.MouseLeftButtonUp += (s, e) => OpenOnWebsite(_selected);
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
            return sv;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            // Tab always toggles Smart/Normal while the window is open (it auto-closes on alt-tab,
            // so there's no other use for Tab here). Handled at window level, so it works
            // regardless of which control has focus.
            if (e.Key == Key.Tab) { ToggleSmart(); e.Handled = true; return; }

            // Re-focus search (default S) when it isn't focused — e.g. after clicking a card.
            // Gated on !search-focused so typing 's' in a query still works.
            if (e.Key == _config.FocusKeyParsed && !_search.IsKeyboardFocused)
            {
                FocusSearch();
                e.Handled = true;
                return;
            }

            if (e.Key == _config.GoldenKeyParsed && !_search.IsKeyboardFocused
                && _selected != null && _selected.CardType == "minion" && _selected.HasGoldenDiff)
            {
                ToggleGolden();
                e.Handled = true;
            }
        }

        // Focus the search box on open (deferred so it lands after the window activates over the
        // game), selecting any existing text so the user can immediately type a fresh query.
        private void FocusSearch()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _search.Focus();
                Keyboard.Focus(_search);
                _search.SelectAll();
            }), DispatcherPriority.Input);
        }

        /// <summary>Re-run the current query/browse (e.g. after the Show-Duos setting changes).</summary>
        public void RefreshPool() => Refresh();

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
                // Default/browse order = website order: by type (minion, spell, hero, ...), then tier, then name.
                cards = pool.OrderBy(c => CardStore.TypeRank(c.CardType))
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
            string path = CardStore.ResolveImagePath(c, goldenOn) ?? CardStore.ResolveImagePath(c, false);
            _art.Source = ImageCache.LoadTrimmed(path, 0);   // full native, transparent edges trimmed

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
            stack.Children.Add(new Image
            {
                Source = ImageCache.LoadTrimmed(CardStore.ResolveImagePath(card, false), 256),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,   // fill the column width
                MaxHeight = 200
            });
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

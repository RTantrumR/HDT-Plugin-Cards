using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HsbgCardLookup.Config;
using HsbgCardLookup.Hotkey;
using HsbgCardLookup.Update;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Settings dialog (opened from the plugin's "Settings" button in HDT options), organized as a
    /// MAIN page of feature categories — each row carries its master On/Off pill and (when the
    /// category has more settings) opens a SUB-PAGE — instead of the old single tall list. While this
    /// window is active the hotkey hook is in CAPTURE mode: every key is swallowed (so pressing F3
    /// here doesn't summon the overlay) and routed to us. Esc goes back / closes; rebinding to an
    /// already-used key STEALS it — the previous owner is set to unbound, with a notice.
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private static readonly string[] Kinds = { "browser", "golden", "focus" };
        private const string Unbound = "None";

        private readonly PluginConfig _config;
        private readonly Data.CardStore _store;   // the previews render real cards
        private readonly HotkeyManager _hotkey;
        private readonly Action _onChanged;
        private readonly Action<ArrangeTarget> _onArrange;   // enter/exit arrange for one feature
        private readonly Dictionary<string, TextBlock> _labels = new Dictionary<string, TextBlock>();
        private readonly TextBlock _status;             // single instance, re-parented onto every page
        private string _capturing;   // kind being rebound, or null
        private bool _onSubPage;     // Esc: sub-page → back, main page → close
        private bool _onMainPage;    // so a pushed update-state change can refresh the main page's hint
        private TextBlock _artFolderLabel;   // shows the current art-cache folder
        private Border _artChangeBtn;        // disabled while a move is in progress
        private ArrangeTarget _arranging = ArrangeTarget.None;      // which feature is being positioned
        private ArrangeTarget _arrangeTargetOnPage = ArrangeTarget.None;  // what THIS page's button arranges
        private Border _arrangeBtn;          // the Arrange/Done toggle button (on the current page, if any)
        // Every right-hand control on a settings row (key binding, On/Off pill, cycler, action button)
        // uses this, so their left AND right edges line up down the page.
        private const double ControlW = 106;

        private StackPanel _pageRoot;        // page-local: what ShowPage renders (header + status + body)
        private Func<bool> _pageMaster;      // page-local: the master switch gating this page, if any
        private Action _pageRefresh;         // page-local: re-render whatever live preview this page shows
        private MmrPanelPreview _mmrPreview; // page-local: the live MMR side-panel preview
        private HudPreview _hudPreview;      // page-local: the live trinket / anomaly HUD preview
        private DarkGiftPreview _giftPreview; // page-local: the live Dark Gift panel preview
        private TextBlock _arrangeBtnLabel;
        private readonly List<Action> _modeRefresh = new List<Action>();   // Dark-Gift mode row repaints

        private readonly string _currentVersion;
        private readonly Action _checkForUpdates;
        private readonly Action<UpdateNotice> _openDownloadPage;   // release page in the browser (notify-only updater)
        private readonly Action<string> _skipUpdate;
        private UpdateNotice _updateNotice;     // most recently pushed state (see RefreshUpdateStatus)
        private bool _onUpdatesPage;
        private StackPanel _updateActionsHost;  // repainted in place, no full page rebuild needed

        internal SettingsWindow(PluginConfig config, Data.CardStore store, HotkeyManager hotkey,
            Action onChanged, Action<ArrangeTarget> onArrange,
            string currentVersion, Action checkForUpdates, Action<UpdateNotice> openDownloadPage,
            Action<string> skipUpdate)
        {
            _config = config;
            _store = store;
            _hotkey = hotkey;
            _onChanged = onChanged;
            _onArrange = onArrange;
            _currentVersion = currentVersion;
            _checkForUpdates = checkForUpdates;
            _openDownloadPage = openDownloadPage;
            _skipUpdate = skipUpdate;

            Title = "HSBG Card Lookup - Settings";
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            Width = 470;
            SizeToContent = SizeToContent.Height;   // auto-fit the height to whatever rows exist
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E));

            // Notice line lives at the top of every page so feedback is immediately visible.
            _status = new TextBlock
            {
                Foreground = UiKit.AccentBrush, FontSize = 13, MinHeight = 18,
                Margin = new Thickness(0, 6, 0, 12), TextWrapping = TextWrapping.Wrap
            };

            BuildMain();

            // While this window is focused our hotkeys are SUPPRESSED (so F3 can't summon the overlay
            // from under the dialog) but nothing is swallowed — the window used to eat every keystroke
            // the whole time it was open, which broke ordinary typing and system shortcuts. Capture
            // mode, which really does swallow keys, now runs only while a rebind is listening.
            _hotkey.KeyCaptured += OnKeyCaptured;
            Activated += (s, e) => _hotkey.Suppress(true);
            Deactivated += (s, e) => { _hotkey.Suppress(false); EndKeyCapture(); };
            // Hidden while arranging: without this the plugin would think the window is still up.
            IsVisibleChanged += (s, e) => { if (!IsVisible) _hotkey.Suppress(false); };
            PreviewKeyDown += OnPreviewKeyDown;
            // Closing the window leaves arrange mode so placeholder boxes never strand. (We do NOT exit
            // on Deactivated — the boxes are topmost/no-activate, so you can alt-tab to the game and keep
            // arranging them over it.)
            Closed += (s, e) =>
            {
                _hotkey.EndCapture();
                _hotkey.Suppress(false);
                _hotkey.KeyCaptured -= OnKeyCaptured;
                if (_arranging != ArrangeTarget.None) { _arranging = ArrangeTarget.None; try { _onArrange?.Invoke(ArrangeTarget.None); } catch { } }
                _mmrPreview?.Close(); _mmrPreview = null;
                _hudPreview?.Close(); _hudPreview = null;
                _giftPreview?.Close(); _giftPreview = null;
            };
        }

        // ── Pages ─────────────────────────────────────────────────────────────────────────────

        private StackPanel NewPage(string title, bool sub) => NewPage(title, sub, null, null);

        /// <summary>
        /// Fresh page skeleton: title (with a Back button on sub-pages) + the shared status line.
        ///
        /// A feature page passes its master switch, which lands on the line that NAMES the feature
        /// and governs everything below: while it is off the body is dimmed, LOCKED (no input reaches
        /// it) and ringed by the dashed gold outline this window uses nowhere else. Header and status
        /// line stay outside the body, so Back and the feedback for the switch itself keep working.
        /// </summary>
        private StackPanel NewPage(string title, bool sub, Func<bool> master, Action<bool> setMaster)
        {
            EndKeyCapture();              // navigating away cancels a pending key capture
            _onSubPage = sub;
            _onMainPage = !sub;
            _onUpdatesPage = false; _updateActionsHost = null;   // page-local; rebuilt when its page shows
            // Arranging belongs to the page whose button started it, so leaving that page ends it —
            // otherwise the mode strands with no visible Done button anywhere.
            if (_arranging != ArrangeTarget.None) SetArrange(ArrangeTarget.None);
            _mmrPreview?.Close(); _mmrPreview = null;
            _hudPreview?.Close(); _hudPreview = null;
            _giftPreview?.Close(); _giftPreview = null;
            _arrangeBtn = null; _arrangeBtnLabel = null;
            _arrangeTargetOnPage = ArrangeTarget.None;
            _pageRefresh = null;   // page-local; rebuilt when its page shows
            _pageMaster = master;
            _modeRefresh.Clear();

            var stack = new StackPanel { Margin = new Thickness(22) };
            if (sub)
            {
                var head = new DockPanel { LastChildFill = true };
                var backLbl = new TextBlock { Text = "‹ Back", Foreground = UiKit.AccentBrush, FontSize = 14, FontWeight = FontWeights.SemiBold };
                var back = new Border
                {
                    Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 5, 11, 5), Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center, Child = backLbl
                };
                back.MouseLeftButtonUp += (s, e) => BuildMain();
                DockPanel.SetDock(back, Dock.Left);
                head.Children.Add(back);
                if (master != null)
                {
                    var pill = TogglePill(master(), setMaster, width: 74, get: master);
                    pill.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(pill, Dock.Right);
                    head.Children.Add(pill);
                }
                head.Children.Add(new TextBlock
                {
                    Text = title, Foreground = UiKit.TextPrimary, FontSize = 20, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0)
                });
                stack.Children.Add(head);
            }
            else stack.Children.Add(UiKit.Title(title, 22));

            (_status.Parent as Panel)?.Children.Remove(_status);
            stack.Children.Add(_status);

            _pageRoot = stack;
            if (master == null) return stack;

            // The dashed ring sits OUTSIDE the body (negative margin) so switching the feature on and
            // off never moves a single row.
            var body = new StackPanel();
            var dashes = DashedKey(10);
            dashes.Margin = new Thickness(-7, -4, -7, -4);
            var wrap = new Grid();
            wrap.Children.Add(body);
            wrap.Children.Add(dashes);
            stack.Children.Add(wrap);

            Action repaint = () =>
            {
                bool on = master();
                body.Opacity = on ? 1.0 : 0.42;
                // Locked, not just dim: settings for something switched off invite changes that do
                // nothing. IsHitTestVisible as well as IsEnabled, since these rows are Borders with
                // their own click handlers rather than Controls.
                body.IsEnabled = on;
                body.IsHitTestVisible = on;
                dashes.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            };
            repaint();
            _modeRefresh.Add(repaint);
            return body;
        }

        private void BuildMain()
        {
            var stack = NewPage("Settings", sub: false);

            stack.Children.Add(CategoryRow("Card search & floating cards",
                "Hotkeys, Duos filter, drag-out cards, art folder.",
                null, null, BuildCardSearch));

            stack.Children.Add(CategoryRow("Trinket HUD",
                "Your trinkets shown on the in-game overlay during a match.",
                () => _config.ShowTrinkets, v =>
                {
                    _config.ShowTrinkets = v;
                    _status.Text = v ? "Trinket HUD on." : "Trinket HUD off.";
                    Changed();
                    UpdateArrangeRow();
                }, BuildTrinkets));

            stack.Children.Add(CategoryRow("Anomaly HUD",
                "The lobby anomaly shown on the in-game overlay during a match.",
                () => _config.ShowAnomaly, v =>
                {
                    _config.ShowAnomaly = v;
                    _status.Text = v ? "Anomaly HUD on." : "Anomaly HUD off.";
                    Changed();
                    UpdateArrangeRow();
                }, BuildAnomaly));

            stack.Children.Add(CategoryRow("Opponents' MMR",
                "MMR, tiers, names — over the portraits and/or a movable panel.",
                () => _config.ShowOpponentMmr, v =>
                {
                    _config.ShowOpponentMmr = v;
                    _status.Text = v
                        ? "Opponents' MMR on — open the sub-page to pick where and what to show."
                        : "Opponent MMR off.";
                    Changed();
                    UpdateArrangeRow();
                }, BuildMmr));

            stack.Children.Add(CategoryRow("Dark Gifts",
                "Hover the Dark Discovery button for the gift list.",
                () => _config.ShowDarkGifts, v =>
                {
                    _config.ShowDarkGifts = v;
                    _status.Text = v
                        ? "Hover the Dark Discovery button in a match to see which Dark Gifts are still obtainable."
                        : "Dark Gift list off.";
                    Changed();
                }, BuildDarkGifts));

            stack.Children.Add(CategoryRow("Updates", UpdatesHint(), null, null, BuildUpdates));

            string exportDir = System.IO.Path.Combine(PluginConfig.DataDir, "match-exports");
            stack.Children.Add(CategoryRow("Match export (CSV)",
                "Record your board each round to a CSV in:\n" + exportDir,
                () => _config.ExportMatchBoards, v =>
                {
                    _config.ExportMatchBoards = v;
                    _status.Text = v
                        ? "Recording your board each round; CSVs land in " + exportDir
                        : "Match board export off.";
                    Changed();
                }, null));

            var closeLabel = new TextBlock { Text = "Close", Foreground = UiKit.TextPrimary, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            var close = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 8, 18, 8), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Child = closeLabel
            };
            close.MouseLeftButtonUp += (s, e) => Close();
            stack.Children.Add(close);

            ShowPage();
        }

        private void BuildCardSearch()
        {
            var stack = NewPage("Card search & floating cards", sub: true);

            stack.Children.Add(new TextBlock
            {
                Text = "Click a binding, then press the key to assign. Esc cancels; Alt can't be bound. " +
                       "Reusing a key moves it here and unbinds its previous owner.",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(KeyRow("browser", "Open overlay"));
            stack.Children.Add(KeyRow("golden", "Toggle golden"));
            stack.Children.Add(KeyRow("focus", "Focus search"));

            stack.Children.Add(Separator());

            stack.Children.Add(ToggleRow("In-game search button (by the card list)", _config.ShowSearchButton, v =>
            {
                _config.ShowSearchButton = v;
                _status.Text = v
                    ? "Magnifying glass next to the game's card-list book toggles the search."
                    : "In-game search button off.";
                Changed();
            }));

            stack.Children.Add(ToggleRow("Show Duos cards", _config.ShowDuos, v =>
            {
                _config.ShowDuos = v;
                _status.Text = v ? "Duos cards shown." : "Duos cards hidden.";
                Changed();
            }));

            stack.Children.Add(ToggleRow("Drag cards from detail view", _config.DragFromDetail, v =>
            {
                _config.DragFromDetail = v;
                _status.Text = v
                    ? "Drag the detail portrait to pull out a floating card."
                    : "Detail portrait drag-out off (click still opens the website).";
                Changed();
            }));

            stack.Children.Add(ToggleRow("Drag cards from results grid", _config.DragFromGrid, v =>
            {
                _config.DragFromGrid = v;
                _status.Text = v
                    ? "Drag a grid card to pull out a floating card."
                    : "Grid drag-out off (a grid click just selects).";
                Changed();
            }));

            stack.Children.Add(ToggleRow("Hide dragged cards with app", _config.HideDraggedWithApp, v =>
            {
                _config.HideDraggedWithApp = v;
                _status.Text = v
                    ? "Dragged cards hide with the overlay."
                    : "Dragged cards stay on screen when the overlay closes.";
                Changed();
            }));

            stack.Children.Add(Separator());
            stack.Children.Add(ArtFolderRow());

            ShowPage();
        }

        private void BuildTrinkets()
        {
            var stack = NewPage("Trinket HUD", sub: true, () => _config.ShowTrinkets, v =>
            {
                _config.ShowTrinkets = v;
                _status.Text = v ? "Trinket HUD on." : "Trinket HUD off.";
                Changed();
                UpdateArrangeRow();
            });

            stack.Children.Add(new TextBlock
            {
                Text = "A box appears for each trinket you are actually holding. That is usually two, but "
                     + "several lesser trinkets end up as a second greater one, and anomalies can hand out "
                     + "more — so up to four boxes are available to position.",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(HudPreviewBlock(isAnomaly: false));

            stack.Children.Add(ArrangeHudRow(ArrangeTarget.Trinkets, "Position the trinket boxes"));

            stack.Children.Add(new TextBlock
            {
                Text = "In match: right-click a trinket card to close it until the match ends, or turn the HUD off.",
                Foreground = UiKit.TextMuted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            ShowPage();
        }

        private void BuildAnomaly()
        {
            var stack = NewPage("Anomaly HUD", sub: true, () => _config.ShowAnomaly, v =>
            {
                _config.ShowAnomaly = v;
                _status.Text = v ? "Anomaly HUD on." : "Anomaly HUD off.";
                Changed();
                UpdateArrangeRow();
            });

            stack.Children.Add(HudPreviewBlock(isAnomaly: true));

            stack.Children.Add(ArrangeHudRow(ArrangeTarget.Anomaly, "Position the anomaly box"));

            stack.Children.Add(new TextBlock
            {
                Text = "In match: right-click the anomaly card to close it until the match ends, or turn the HUD off.",
                Foreground = UiKit.TextMuted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            ShowPage();
        }

        private void BuildMmr()
        {
            var stack = NewPage("Opponents' MMR", sub: true, () => _config.ShowOpponentMmr, v =>
            {
                _config.ShowOpponentMmr = v;
                _status.Text = v ? "Opponents' MMR on." : "Opponents' MMR off.";
                Changed();
                UpdateArrangeRow();
            });

            stack.Children.Add(new TextBlock
            {
                Text = "Pick where it shows, then what it shows. Any combination works.",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            });

            // ── Where ──────────────────────────────────────────────────────────────────────────
            stack.Children.Add(GroupHeader("Where it shows", first: true));

            stack.Children.Add(ToggleRow("On the leaderboard portraits", _config.ShowMmrLabels, v =>
            {
                _config.ShowMmrLabels = v;
                _status.Text = v
                    ? "Rating/name labels on the leaderboard portraits."
                    : "No labels on the portraits. Tavern tiers and the ⚔ marker are separate settings — "
                      + "they can stay by the portraits on their own.";
                Changed();
            }));

            stack.Children.Add(ToggleRow("Side panel", _config.ShowMmrPanel, v =>
            {
                _config.ShowMmrPanel = v;
                _status.Text = v
                    ? "Standings panel on — drag it anywhere over the game; drag its top-right corner to resize."
                    : "Standings panel off.";
                NormalizeTierMode();   // panel off → panel-involving tier modes snap to their fallback
                Changed();
                UpdateArrangeRow();
                foreach (var r in _modeRefresh) r();   // the tier cycler skips panel options while it is off
            }, get: () => _config.ShowMmrPanel));

            // ── What ───────────────────────────────────────────────────────────────────────────
            stack.Children.Add(GroupHeader("What it shows"));

            stack.Children.Add(CycleRow("Names",
                new[] { "Players", "Heroes", "Off" },
                new[] { "Players", "Heroes", "None" },
                new[] { "Player names (battletags) in the panel.",
                        "Hero names instead of battletags — nothing identifies the player.",
                        "No name column at all; just place, rating and tier." },
                () => _config.OpponentNameMode, v => _config.OpponentNameMode = v));

            stack.Children.Add(ToggleRow("MMR rating", _config.ShowMmrRating, v =>
            {
                _config.ShowMmrRating = v;
                _status.Text = v ? "Ratings shown (8000↓ below the leaderboard cutoff)." : "Ratings hidden.";
                Changed();
            }));

            stack.Children.Add(ToggleRow("Today's rating change (▲/▼)", _config.ShowMmrDeltas, v =>
            {
                _config.ShowMmrDeltas = v;
                _status.Text = v ? "Daily ▲/▼ deltas shown next to the rating." : "Daily deltas hidden.";
                Changed();
            }));

            NormalizeTierMode();   // e.g. a fresh config: panel off + mode "Both" → show as "Portraits"
            stack.Children.Add(CycleRow("Tavern tiers",
                new[] { "Both", "Portraits", "Panel", "Off" },
                new[] { "Both", "Portraits", "Panel", "Off" },
                new[] { "Tier icons by the portraits and in the panel.",
                        "Tier icons right of each leaderboard portrait only.",
                        "Tier icons inside the side panel only.",
                        "No tavern-tier icons anywhere." },
                () => _config.TavernTierMode, v => _config.TavernTierMode = v,
                // The two panel-involving locations simply aren't offered while the panel is off,
                // rather than being shown greyed out.
                v => _config.ShowMmrPanel
                     || (!string.Equals(v, "Both", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(v, "Panel", StringComparison.OrdinalIgnoreCase))));

            stack.Children.Add(ToggleRow("Mark last fought opponent (⚔)", _config.ShowLastOpponent, v =>
            {
                _config.ShowLastOpponent = v;
                _status.Text = v
                    ? "The previous combat's opponent gets a ⚔ marker by their portrait."
                    : "⚔ marker off.";
                Changed();
            }));

            stack.Children.Add(ToggleRow("Dim dead players", _config.DimDeadPlayers, v =>
            {
                _config.DimDeadPlayers = v;
                _status.Text = v ? "Knocked-out players gray out." : "Dead players keep full color.";
                Changed();
            }));

            stack.Children.Add(Separator());

            // A real panel, at the size it renders in game, over a live frame of the game when it is
            // running. Sized to the page width so the crop stays 16:9.
            _mmrPreview = new MmrPanelPreview(_config, 394, 222);
            stack.Children.Add(TabSwitch(new[] { "Solo", "Duos" }, 0, i => _mmrPreview?.SetDuos(i == 1)));
            stack.Children.Add(PreviewBlock(_mmrPreview.Root, () => _mmrPreview?.Refresh(),
                () => _config.ShowMmrPanel,
                () => { _config.ShowMmrPanel = true; AfterEnable("Standings panel on."); }));

            stack.Children.Add(ArrangeHudRow(ArrangeTarget.MmrPanel, "Position the side panel"));

            ShowPage();
        }

        // The live HUD preview block: one real card at its real size, sized to the page width. No
        // off-treatment of its own — the only switch that hides this HUD is the page's master, and the
        // whole body already dims and locks under it.
        private UIElement HudPreviewBlock(bool isAnomaly)
        {
            _hudPreview = new HudPreview(_config, _store, isAnomaly, 394);
            _pageRefresh = () => _hudPreview?.Refresh();
            return _hudPreview.Root;
        }

        // With the panel surface off, the panel-involving tier modes make no sense — snap them to the
        // equivalent panel-less choice ("Both"→"Portraits", "Panel"→"Off") so the selection always
        // sits on an option that's actually selectable.
        private void NormalizeTierMode()
        {
            if (_config.ShowMmrPanel) return;
            var m = _config.TavernTierMode;
            if (string.IsNullOrEmpty(m) || string.Equals(m, "Both", StringComparison.OrdinalIgnoreCase))
                _config.TavernTierMode = "Portraits";
            else if (string.Equals(m, "Panel", StringComparison.OrdinalIgnoreCase))
                _config.TavernTierMode = "Off";
        }

        private void BuildDarkGifts()
        {
            var stack = NewPage("Dark Gifts", sub: true, () => _config.ShowDarkGifts, v =>
            {
                _config.ShowDarkGifts = v;
                _status.Text = v ? "Dark Gift list on." : "Dark Gift list off.";
                Changed();
                UpdateArrangeRow();
            });

            stack.Children.Add(new TextBlock
            {
                Text = "What the hover panel shows. Right-clicking the panel in game cycles these too.",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(ModeRow("Both", "Gift list + minion pool",
                "The full panel — gifts, plus the guaranteed-type minions when they apply."));
            stack.Children.Add(ModeRow("Gifts", "Gift list only",
                "Never show the minion-art column."));
            stack.Children.Add(ModeRow("Minions", "Minion pool only",
                "Only the guaranteed-type minion arts; the panel stays hidden when no pool applies."));

            stack.Children.Add(Separator());

            // The real panel, in the selected mode. The turn is the one thing a preview cannot know and
            // the thing that changes the panel most, so it is a switch rather than a fixed guess.
            _giftPreview = new DarkGiftPreview(_config, _store, 394);
            var turns = DarkGiftPreview.SampleTurns;
            var turnLabels = new string[turns.Length];
            for (int i = 0; i < turns.Length; i++) turnLabels[i] = "Turn " + turns[i];
            _pageRefresh = () => _giftPreview?.Refresh();
            stack.Children.Add(TabSwitch(turnLabels, 1, i => _giftPreview?.SetTurn(turns[i])));
            stack.Children.Add(_giftPreview.Root);

            stack.Children.Add(ArrangeHudRow(ArrangeTarget.DarkGifts, "Position the Dark Gift panel"));

            stack.Children.Add(new TextBlock
            {
                Text = "In match: drag the panel to move it, drag its top-right corner to resize, and "
                     + "right-click it to cycle these modes. Until you move it, it appears beside the "
                     + "Dark Discovery button.",
                Foreground = UiKit.TextMuted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            ShowPage();
        }

        // ── Updates ───────────────────────────────────────────────────────────────────────────

        // Short line for the main-page category row — recomputed each time BuildMain runs (including
        // the live refresh RefreshUpdateStatus triggers while this page is the active one).
        private string UpdatesHint()
        {
            if (_updateNotice != null && _updateNotice.AvailableForDownload)
                return $"Update v{_updateNotice.AvailableVersion} available.";
            return $"You're on v{_currentVersion}.";
        }

        private void BuildUpdates()
        {
            var stack = NewPage("Updates", sub: true);
            _onUpdatesPage = true;

            stack.Children.Add(new TextBlock
            {
                Text = $"Installed version: v{_currentVersion}",
                Foreground = UiKit.TextPrimary, FontSize = 15, Margin = new Thickness(0, 0, 0, 12)
            });

            _updateActionsHost = new StackPanel();
            stack.Children.Add(_updateActionsHost);
            RepaintUpdateArea();

            ShowPage();
        }

        // Called by Plugin whenever the known update state changes (background or manual check) — the
        // same push that drives the F3 banner and the in-game badge, so all three never disagree.
        // Only actually repaints when the Updates sub-page is the one showing; the main page's hint
        // text catches up next time BuildMain runs.
        internal void RefreshUpdateStatus(UpdateNotice notice)
        {
            _updateNotice = notice;
            if (_onUpdatesPage) RepaintUpdateArea();
            else if (_onMainPage) BuildMain();
        }

        private void RepaintUpdateArea()
        {
            if (_updateActionsHost == null) return;
            _updateActionsHost.Children.Clear();

            if (_updateNotice != null && _updateNotice.AvailableForDownload)
            {
                _updateActionsHost.Children.Add(new TextBlock
                {
                    Text = $"Update v{_updateNotice.AvailableVersion} is available. The download and the "
                        + "release notes are on the release page; run install.bat from the zip to update.",
                    Foreground = UiKit.AccentBrush, FontSize = 14, Margin = new Thickness(0, 0, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                var notice = _updateNotice;
                row.Children.Add(SmallButton("Open download page", () => _openDownloadPage(notice)));
                row.Children.Add(SmallButton("Skip this version", () => _skipUpdate(notice.AvailableVersion)));
                _updateActionsHost.Children.Add(row);
                return;
            }

            string msg = _updateNotice?.Message ?? "Not checked yet this session.";
            _updateActionsHost.Children.Add(new TextBlock
            {
                Text = msg, Foreground = (_updateNotice?.IsError ?? false) ? Brushes.IndianRed : UiKit.TextMuted,
                FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            _updateActionsHost.Children.Add(SmallButton("Check for updates", () =>
            {
                _status.Text = "Checking for updates…";
                _checkForUpdates();
            }));
        }

        private static Border SmallButton(string text, Action onClick)
        {
            var lbl = new TextBlock { Text = text, Foreground = UiKit.TextPrimary, FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Center };
            var b = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 6, 12, 6), Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0), Child = lbl
            };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick?.Invoke(); };
            return b;
        }

        // ── Row builders ──────────────────────────────────────────────────────────────────────

        /// <summary>Apply a setting change: persist/re-apply it as before, then let the current page
        /// refresh anything live it is showing. Every row handler calls this instead of _onChanged so a
        /// new preview can never be left out of a code path by accident.</summary>
        private void Changed()
        {
            _onChanged();
            // Repaint everything on the page that renders a config value. Without this a control only
            // ever repainted when IT was the thing clicked, so a switch that governs other rows (the
            // page master) left them showing the state they had a moment ago.
            foreach (var r in _modeRefresh) { try { r(); } catch { } }
            try { _pageRefresh?.Invoke(); } catch { }
        }

        /// <summary>Show a built page. The ScrollViewer is a floor against pages outgrowing the screen:
        /// the window is SizeToContent.Height, so without a MaxHeight ON THE SCROLLVIEWER it would be
        /// measured at infinite height, never scroll, and simply run off the bottom.</summary>
        private void ShowPage()
        {
            Content = new ScrollViewer
            {
                Content = _pageRoot,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 90)
            };
        }

        // A heading that splits a page into groups. Pages used to be one flat list of toggles, which
        // made it impossible to tell a display SURFACE apart from the CONTENT shown on it.
        private static TextBlock GroupHeader(string text, bool first = false) => new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Foreground = UiKit.TextMuted, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, first ? 2 : 14, 0, 7)
        };

        /// <summary>
        /// A compact one-line selector: label on the left, "&#x25c4; value &#x25ba;" on the right, in a box the same
        /// width as the key-binding buttons so every right edge on the page lines up. Replaces stacking
        /// one bordered radio-card per option, which cost ~60px each for a single choice.
        /// <paramref name="selectable"/> filters which values can be stepped to (an unavailable value is
        /// skipped rather than shown greyed), and the caption always re-reads through
        /// <paramref name="get"/> so it can't go stale the way <see cref="TogglePill"/>'s cached state can.
        /// </summary>
        private UIElement CycleRow(string label, string[] values, string[] captions, string[] hints,
                                   Func<string> get, Action<string> set,
                                   Func<string, bool> selectable = null)
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };

            var valueLbl = new TextBlock
            {
                Foreground = UiKit.AccentBrush, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var left = new TextBlock
            {
                Text = "◄", Foreground = UiKit.TextSecondary, FontSize = 12.5, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 2, 0)
            };
            var right = new TextBlock
            {
                Text = "►", Foreground = UiKit.TextSecondary, FontSize = 12.5, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Margin = new Thickness(2, 0, 0, 0)
            };

            var inner = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(left, Dock.Left);
            DockPanel.SetDock(right, Dock.Right);
            inner.Children.Add(left);
            inner.Children.Add(right);
            inner.Children.Add(valueLbl);

            var box = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 5, 5, 5),
                Width = ControlW, Child = inner, VerticalAlignment = VerticalAlignment.Center
            };

            int IndexOf(string v)
            {
                for (int i = 0; i < values.Length; i++)
                    if (string.Equals(values[i], v, StringComparison.OrdinalIgnoreCase)) return i;
                return 0;
            }

            Action repaint = () => valueLbl.Text = captions[IndexOf(get())];
            repaint();
            _modeRefresh.Add(repaint);

            Action<int> step = dir =>
            {
                int i = IndexOf(get());
                // Walk in the requested direction until a selectable value turns up. The bound stops the
                // walk if every other option is unavailable, leaving the current value untouched.
                for (int n = 0; n < values.Length; n++)
                {
                    i = (i + dir + values.Length) % values.Length;
                    if (selectable == null || selectable(values[i])) break;
                }
                set(values[i]);
                foreach (var r in _modeRefresh) r();
                // The caption has to stay short to fit the shared control width, so the explanation
                // lives here rather than in the box.
                _status.Text = hints != null && i < hints.Length ? hints[i] : label;
                Changed();
            };
            left.MouseLeftButtonUp += (s, e) => { e.Handled = true; step(-1); };
            right.MouseLeftButtonUp += (s, e) => { e.Handled = true; step(+1); };
            box.MouseLeftButtonUp += (s, e) => { e.Handled = true; step(+1); };
            box.Cursor = Cursors.Hand;

            DockPanel.SetDock(box, Dock.Right);
            dock.Children.Add(box);
            dock.Children.Add(new TextBlock
            {
                Text = label, Foreground = UiKit.TextPrimary, FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });
            return dock;
        }

        /// <summary>A small segmented switch (e.g. Solo | Duos). Returns the container; the selected
        /// index is owned by the caller through <paramref name="onPick"/>.</summary>
        private static UIElement TabSwitch(string[] labels, int initial, Action<int> onPick)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            var cells = new Border[labels.Length];
            var texts = new TextBlock[labels.Length];
            int current = initial;

            Action paint = () =>
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    bool sel = i == current;
                    cells[i].Background = sel ? UiKit.Br(UiKit.PanelActive) : UiKit.Br(UiKit.RowBg);
                    cells[i].BorderBrush = sel ? UiKit.AccentBrush : UiKit.StrokeBrush;
                    texts[i].Foreground = sel ? UiKit.AccentBrush : UiKit.TextMuted;
                }
            };

            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                texts[i] = new TextBlock
                {
                    Text = labels[i], FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                cells[i] = new Border
                {
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand, Child = texts[i]
                };
                cells[i].MouseLeftButtonUp += (snd, e) =>
                {
                    e.Handled = true;
                    if (current == idx) return;
                    current = idx;
                    paint();
                    try { onPick(idx); } catch { }
                };
                strip.Children.Add(cells[i]);
            }
            paint();
            return strip;
        }

        private static Border Separator() =>
            new Border { Height = 1, Background = UiKit.StrokeBrush, Margin = new Thickness(0, 6, 0, 12) };

        // A main-page category: title + hint on the left; optional master On/Off pill; a chevron and
        // click-to-open when the category has a sub-page.
        private UIElement CategoryRow(string title, string hint, Func<bool> get, Action<bool> set, Action open)
        {
            var dock = new DockPanel { LastChildFill = true };

            if (open != null)
            {
                var chev = new TextBlock
                {
                    Text = "›", FontSize = 22, Foreground = UiKit.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 2)
                };
                DockPanel.SetDock(chev, Dock.Right);
                dock.Children.Add(chev);
            }
            if (get != null)
            {
                var pill = TogglePill(get(), set, width: 74);
                pill.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(pill, Dock.Right);
                dock.Children.Add(pill);
            }

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            left.Children.Add(new TextBlock { Text = title, Foreground = UiKit.TextPrimary, FontSize = 15 });
            if (!string.IsNullOrEmpty(hint))
                left.Children.Add(new TextBlock
                {
                    Text = hint, Foreground = UiKit.TextMuted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap
                });
            dock.Children.Add(left);

            var row = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8), Child = dock
            };
            if (open != null)
            {
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonUp += (s, e) => open();   // the pill marks its clicks Handled
            }
            return row;
        }

        // ---- The switched-off marker -------------------------------------------------------

        /// <summary>
        /// A live preview wrapped in its switched-off treatment, for a surface that has a switch of
        /// its OWN beneath the page master (today: the MMR side panel). Off doesn't blank it — it
        /// still answers "what would this look like" — but it dims, takes the dashed outline, and
        /// becomes clickable: the dimmed thing IS the switch.
        /// </summary>
        private UIElement PreviewBlock(FrameworkElement preview, Action refresh, Func<bool> live, Action enable)
        {
            // The viewport is a fixed width inside a wider page, so a stretched overlay would frame
            // the page instead of the preview. Match its box exactly.
            var dashes = DashedKey(8);
            dashes.Margin = preview.Margin;
            if (!double.IsNaN(preview.Width)) dashes.Width = preview.Width;

            var grid = new Grid();
            grid.Children.Add(preview);
            grid.Children.Add(dashes);

            Action repaint = () =>
            {
                // With the page master off the whole body is already dimmed, locked and ringed;
                // marking this preview a second time inside that would just be nested dashes.
                bool locked = _pageMaster != null && !_pageMaster();
                bool on = locked || live();
                preview.Opacity = on ? 1.0 : 0.45;
                dashes.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                grid.Cursor = on ? Cursors.Arrow : Cursors.Hand;
            };
            repaint();
            _modeRefresh.Add(repaint);
            // Set here rather than by the page, so a preview can never be added without its own
            // off-state repaint following every settings change.
            _pageRefresh = () => { try { refresh(); } catch { } repaint(); };

            grid.MouseLeftButtonUp += (s, e) =>
            {
                if (live()) return;   // a live preview is for looking at, not a hidden off switch
                e.Handled = true;
                enable();
            };
            return grid;
        }

        /// <summary>Tail of a "switch it on from the preview" click: persist and re-apply exactly as
        /// a pill click would. Changed() repaints the page, so the toggle the click flipped — which is
        /// not the one that was clicked — catches up on its own.</summary>
        private void AfterEnable(string status)
        {
            _status.Text = status;
            Changed();
            UpdateArrangeRow();
        }

        /// <summary>The dashed gold outline that means "switched off — and this is what switches it
        /// on". A WPF Border can't be dashed, so it is a Rectangle laid over whatever it marks.</summary>
        private static System.Windows.Shapes.Rectangle DashedKey(double radius) =>
            new System.Windows.Shapes.Rectangle
            {
                Stroke = UiKit.AccentBrush, StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = radius, RadiusY = radius,
                IsHitTestVisible = false
            };

        // The On/Off pill by itself (marks its click Handled so a category row doesn't also open).
        // `get` (optional) makes the pill re-read the config on repaint instead of trusting the state
        // it cached — needed wherever something OTHER than this pill can flip the same setting.
        private Border TogglePill(bool initial, Action<bool> onToggle, double width, Func<bool> get = null)
        {
            bool state = initial;
            var lbl = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var pill = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(0, 5, 0, 5), Width = width, Cursor = Cursors.Hand, Child = lbl
            };
            Action apply = () =>
            {
                if (get != null) state = get();
                pill.Background = state ? UiKit.Br(UiKit.PanelActive) : UiKit.Br(UiKit.RowBg);
                pill.BorderBrush = state ? UiKit.AccentBrush : UiKit.StrokeBrush;
                lbl.Foreground = state ? UiKit.AccentBrush : UiKit.TextPrimary;
                lbl.Text = state ? "On" : "Off";
            };
            apply();
            if (get != null) _modeRefresh.Add(apply);
            // onToggle BEFORE apply: a getter-backed pill has to repaint from the value the handler
            // just wrote, not from the one it is replacing.
            pill.MouseLeftButtonUp += (s, e) => { e.Handled = true; state = !state; onToggle(state); apply(); };
            return pill;
        }

        private UIElement KeyRow(string kind, string label)
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };

            var lbl = new TextBlock
            {
                Text = Display(GetKey(kind)), Foreground = UiKit.AccentBrush, FontSize = 15,
                FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center
            };
            _labels[kind] = lbl;
            var btn = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand,
                Width = ControlW, Child = lbl   // fixed width so a 1-2 char key doesn't size a tiny/huge box
            };
            btn.MouseLeftButtonUp += (s, e) => BeginCapture(kind);
            DockPanel.SetDock(btn, Dock.Right);
            dock.Children.Add(btn);   // right, fixed width

            dock.Children.Add(new TextBlock
            {
                Text = label, Foreground = UiKit.TextPrimary, FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });   // fills the rest
            return dock;
        }

        private UIElement ToggleRow(string label, bool initial, Action<bool> onToggle, Func<bool> get = null)
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };

            var pill = TogglePill(initial, onToggle, width: ControlW, get: get);
            DockPanel.SetDock(pill, Dock.Right);
            dock.Children.Add(pill);

            dock.Children.Add(new TextBlock
            {
                Text = label, Foreground = UiKit.TextPrimary, FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });
            return dock;
        }

        // Dark-Gift panel display mode — radio-style row; the selected one gets the accent treatment.
        private UIElement ModeRow(string value, string title, string desc) =>
            SelectRow(() => _config.DarkGiftMode, v => _config.DarkGiftMode = v,
                "Dark Gift panel: ", value, title, desc, null);

        // Radio-style single-select row (one per option; the group shares a config string value).
        // `enabled` (optional) grays the row out and ignores clicks while false — repainted with the
        // rest of the group, so e.g. a "panel only" option can follow the panel toggle live.
        private UIElement SelectRow(Func<string> get, Action<string> set, string statusPrefix,
            string value, string title, string desc, Func<bool> enabled)
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleTb = new TextBlock { Text = title, FontSize = 15 };
            left.Children.Add(titleTb);
            left.Children.Add(new TextBlock
            {
                Text = desc, Foreground = UiKit.TextMuted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap
            });

            var row = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand, Child = left
            };
            Action repaint = () =>
            {
                bool on = enabled?.Invoke() ?? true;
                bool sel = string.Equals(get(), value, StringComparison.OrdinalIgnoreCase)
                           || (value == "Both" && string.IsNullOrEmpty(get()));
                row.Background = sel ? UiKit.Br(UiKit.PanelActive) : UiKit.Br(UiKit.RowBg);
                row.BorderBrush = sel ? UiKit.AccentBrush : UiKit.StrokeBrush;
                titleTb.Foreground = sel ? UiKit.AccentBrush : UiKit.TextPrimary;
                row.Opacity = on ? 1.0 : 0.45;
                row.Cursor = on ? Cursors.Hand : Cursors.Arrow;
            };
            repaint();
            _modeRefresh.Add(repaint);
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (!(enabled?.Invoke() ?? true)) return;
                set(value);
                foreach (var r in _modeRefresh) r();
                _status.Text = statusPrefix + title.ToLowerInvariant() + ".";
                Changed();
            };
            return row;
        }

        // ── HUD arrange ("unlock overlay") ──────────────────────────────────────────────────────

        // Shows every enabled HUD box on screen as a draggable/resizable placeholder so the layout can
        // be set up without being in a match. Enabled only when at least one HUD toggle is on. Lives on
        // the Trinket + Anomaly sub-pages (it arranges both HUDs at once).
        private UIElement ArrangeHudRow(ArrangeTarget target, string title)
        {
            _arrangeTargetOnPage = target;
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 4) };

            _arrangeBtnLabel = new TextBlock { Text = "Arrange…", Foreground = UiKit.TextPrimary, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            _arrangeBtn = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand,
                Width = ControlW, Child = _arrangeBtnLabel, VerticalAlignment = VerticalAlignment.Center
            };
            _arrangeBtn.MouseLeftButtonUp += (s, e) => ToggleArrange();
            DockPanel.SetDock(_arrangeBtn, Dock.Right);
            dock.Children.Add(_arrangeBtn);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            left.Children.Add(new TextBlock { Text = title, Foreground = UiKit.TextPrimary, FontSize = 15 });
            left.Children.Add(new TextBlock
            {
                Text = "Brings Hearthstone to the front and shows just this, on its own. Drag it to move; "
                     + "drag its top-right corner to resize. Click Done here when it's where you want it.",
                Foreground = UiKit.TextMuted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap
            });
            dock.Children.Add(left);

            UpdateArrangeRow();
            return dock;
        }

        private void ToggleArrange()
        {
            if (_arrangeBtn == null || _arrangeTargetOnPage == ArrangeTarget.None) return;
            if (!_arrangeBtn.IsEnabled) return;
            SetArrange(_arranging == _arrangeTargetOnPage ? ArrangeTarget.None : _arrangeTargetOnPage);
        }

        private void SetArrange(ArrangeTarget target)
        {
            // Nothing draws outside the game, so entering arrange without Hearthstone up would show an
            // empty screen and no reason why. This refusal gets its own modal dialog rather than the
            // shared status line: that line carries every ordinary "setting applied" message, so a
            // refusal written there reads as one more passing tip and gets skipped. A MessageBox is
            // safe on THIS path specifically — it steals OS foreground, which normally makes HDT hide
            // its entire overlay, but the condition for showing it is that Hearthstone is not running,
            // so there is no overlay to lose.
            if (target != ArrangeTarget.None && !HearthstoneIsRunning())
            {
                _status.Text = "Hearthstone isn't running.";
                MessageBox.Show(this,
                    "Hearthstone isn't running.\n\n"
                    + "This draws on top of the game, so there is nothing to position yet. "
                    + "Start Hearthstone, then click Arrange again.",
                    "HSBG Card Lookup — Arrange",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _arranging = target;

            // Get out of the way: the thing being positioned is over the game, and this window is
            // topmost, so leaving it up would cover the very thing the user is dragging. The overlay's
            // own Done button brings it back (EndArrangeFromOverlay), and Closed/NewPage both end the
            // session, so there is no path that leaves it hidden with no way back.
            if (target != ArrangeTarget.None) Hide();
            else if (!IsVisible) { Show(); Activate(); }

            try { _onArrange?.Invoke(target); } catch { }
            _status.Text = target != ArrangeTarget.None
                ? "Arranging — drag it to move, drag its top-right corner to resize. Click Done when finished."
                : "Position saved.";
            UpdateArrangeRow();
        }

        /// <summary>The overlay's own Done button was clicked. Ends the session and repaints this
        /// window's button, so the two can't disagree about whether arranging is still running.</summary>
        internal void EndArrangeFromOverlay()
        {
            try { Dispatcher.BeginInvoke(new Action(() => SetArrange(ArrangeTarget.None))); } catch { }
        }

        private static bool HearthstoneIsRunning()
        {
            try { return Hearthstone_Deck_Tracker.User32.GetHearthstoneWindow() != System.IntPtr.Zero; }
            catch { return false; }
        }

        // Positioning something that is switched off would be positioning something the user can't
        // see, so the button follows its own feature's toggle. (Null-safe: the button only exists on
        // pages that have something to position.)
        private bool ArrangeTargetEnabled(ArrangeTarget t)
        {
            switch (t)
            {
                case ArrangeTarget.Trinkets: return _config.ShowTrinkets;
                case ArrangeTarget.Anomaly: return _config.ShowAnomaly;
                case ArrangeTarget.MmrPanel: return _config.ShowOpponentMmr && _config.ShowMmrPanel;
                case ArrangeTarget.DarkGifts: return _config.ShowDarkGifts;
                default: return false;
            }
        }

        private void UpdateArrangeRow()
        {
            bool enabled = ArrangeTargetEnabled(_arrangeTargetOnPage);
            // Switching the feature off mid-arrange ends the session rather than stranding it.
            if (!enabled && _arranging != ArrangeTarget.None && _arranging == _arrangeTargetOnPage)
            {
                SetArrange(ArrangeTarget.None);
                return;   // SetArrange re-enters here
            }
            if (_arrangeBtn == null) return;
            bool on = _arranging != ArrangeTarget.None && _arranging == _arrangeTargetOnPage;
            _arrangeBtn.IsEnabled = enabled;
            _arrangeBtn.Opacity = enabled ? 1.0 : 0.5;
            _arrangeBtn.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
            _arrangeBtnLabel.Text = on ? "Done" : "Arrange…";
            _arrangeBtnLabel.Foreground = on ? UiKit.AccentBrush : UiKit.TextPrimary;
            _arrangeBtn.BorderBrush = on ? UiKit.AccentBrush : UiKit.StrokeBrush;
        }

        // ── Art-cache folder (relocate the ~200MB off the system drive) ────────────────────────

        private UIElement ArtFolderRow()
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };

            var btnLabel = new TextBlock { Text = "Change…", Foreground = UiKit.TextPrimary, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            _artChangeBtn = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand,
                Width = ControlW, Child = btnLabel, VerticalAlignment = VerticalAlignment.Center
            };
            _artChangeBtn.MouseLeftButtonUp += (s, e) => ChangeArtFolder();
            DockPanel.SetDock(_artChangeBtn, Dock.Right);
            dock.Children.Add(_artChangeBtn);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            left.Children.Add(new TextBlock { Text = "Card art folder", Foreground = UiKit.TextPrimary, FontSize = 15 });
            _artFolderLabel = new TextBlock
            {
                Foreground = UiKit.TextMuted, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300
            };
            left.Children.Add(_artFolderLabel);
            dock.Children.Add(left);

            UpdateArtFolderLabel();
            return dock;
        }

        private void UpdateArtFolderLabel()
        {
            if (_artFolderLabel == null) return;
            _artFolderLabel.Text = CardArt.CacheDir;
            _artFolderLabel.ToolTip = CardArt.CacheDir;
        }

        private void ChangeArtFolder()
        {
            string picked;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Choose a folder for the card-art cache (~200 MB). A 'HsbgCardLookup-art' subfolder is created there.";
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                picked = dlg.SelectedPath;
            }

            string oldDir = CardArt.CacheDir;
            string newDir = Path.Combine(picked, "HsbgCardLookup-art");
            if (PathsEqual(oldDir, newDir)) { _status.Text = "Card art is already there."; return; }

            _artChangeBtn.IsEnabled = false;
            _artChangeBtn.Opacity = 0.5;
            _status.Text = "Moving card art… (may take a moment across drives)";

            Task.Run(() =>
            {
                bool ok = MoveArt(oldDir, newDir, out string err);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_artChangeBtn != null) { _artChangeBtn.IsEnabled = true; _artChangeBtn.Opacity = 1.0; }
                    if (ok)
                    {
                        CardArt.CacheDir = newDir;
                        _config.ArtCacheDir = newDir;
                        _config.Save();
                        UpdateArtFolderLabel();
                        _status.Text = "Card art folder changed.";
                    }
                    else
                    {
                        _status.Text = "Couldn't move card art: " + err + " — kept current folder.";
                    }
                }));
            });
        }

        // Move the flat art-cache files (webp + any stray zip) into the new folder. Same-drive moves are
        // instant; cross-drive File.Move copies then deletes. Best-effort: a failure leaves the old
        // folder intact so nothing is lost (the caller then keeps the current folder).
        private static bool MoveArt(string oldDir, string newDir, out string err)
        {
            err = null;
            try
            {
                Directory.CreateDirectory(newDir);
                if (Directory.Exists(oldDir) && !PathsEqual(oldDir, newDir))
                {
                    foreach (var f in Directory.GetFiles(oldDir))
                    {
                        var dest = Path.Combine(newDir, Path.GetFileName(f));
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(f, dest);
                    }
                    try { if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir); } catch { }
                }
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        // ── Capture ───────────────────────────────────────────────────────────────────────────

        private void BeginCapture(string kind)
        {
            _capturing = kind;
            _hotkey.BeginCapture();   // swallow everything until a key lands or the user cancels
            _labels[kind].Text = "Press a key...";
            _status.Text = "Listening for a key... (Esc to cancel)";
        }

        /// <summary>Leave rebind listening, whether it completed, was cancelled, or focus moved away.</summary>
        private void EndKeyCapture()
        {
            if (_capturing == null) return;
            var kind = _capturing;
            _capturing = null;
            _hotkey.EndCapture();
            try { _labels[kind].Text = Display(GetKey(kind)); } catch { }
        }

        // Esc used to arrive through the swallow-everything hook. Now that the window gets real
        // keyboard input, it is ordinary WPF input — and it must not fight an active rebind.
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_capturing != null) return;
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_onSubPage) BuildMain();   // Esc steps back before it closes
            else Close();
        }

        // Fires on the hook thread (HDT's UI thread); marshal to be safe against reentrancy.
        private void OnKeyCaptured(Key key)
        {
            Dispatcher.BeginInvoke(new Action(() => HandleCaptured(key)));
        }

        private void HandleCaptured(Key key)
        {
            if (_capturing == null) return;   // not listening: the hook isn't swallowing anything

            if (key == Key.Escape)
            {
                EndKeyCapture();
                _status.Text = "Cancelled.";
                return;
            }
            if (IsModifier(key))
            {
                _status.Text = (key == Key.LeftAlt || key == Key.RightAlt || key == Key.System)
                    ? "Alt can't be bound - press another key."
                    : "Press a non-modifier key.";
                return;
            }

            string ks = key.ToString();

            // Steal: if another binding uses this key, unbind it and take the key here.
            var stolen = new List<string>();
            foreach (var other in Kinds)
            {
                if (other == _capturing) continue;
                if (string.Equals(GetKey(other), ks, StringComparison.OrdinalIgnoreCase))
                {
                    SetKey(other, Unbound);
                    stolen.Add(Label(other));
                }
            }

            SetKey(_capturing, ks);
            string bound = _capturing;
            _capturing = null;
            _hotkey.EndCapture();   // the key landed: stop swallowing

            // Refresh every row's display (a stolen one just became unbound).
            foreach (var k in Kinds)
                if (_labels.TryGetValue(k, out var l)) l.Text = Display(GetKey(k));

            _status.Text = stolen.Count == 0
                ? $"Saved. \"{Label(bound)}\" bound to {ks}."
                : $"Saved. \"{Label(bound)}\" bound to {ks}; unbound: {string.Join(", ", stolen)}.";

            Changed();
        }

        private static bool IsModifier(Key k) =>
            k == Key.LeftShift || k == Key.RightShift || k == Key.LeftCtrl || k == Key.RightCtrl ||
            k == Key.LeftAlt || k == Key.RightAlt || k == Key.LWin || k == Key.RWin || k == Key.System;

        private static string Display(string ks) =>
            string.IsNullOrEmpty(ks) || ks == Unbound ? "—" : ks;

        // ── Config accessors ─────────────────────────────────────────────────────────────────

        private string GetKey(string kind)
        {
            switch (kind)
            {
                case "browser": return _config.BrowserKey;
                case "golden": return _config.GoldenKey;
                case "focus": return _config.FocusKey;
                default: return "";
            }
        }

        private void SetKey(string kind, string v)
        {
            switch (kind)
            {
                case "browser": _config.BrowserKey = v; break;
                case "golden": _config.GoldenKey = v; break;
                case "focus": _config.FocusKey = v; break;
            }
        }

        private static string Label(string kind)
        {
            switch (kind)
            {
                case "browser": return "Open overlay";
                case "golden": return "Toggle golden";
                case "focus": return "Focus search";
                default: return kind;
            }
        }
    }
}

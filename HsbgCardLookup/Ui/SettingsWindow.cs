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
using System.Windows.Threading;
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
        private static readonly string[] Kinds = { "browser", "golden", "focus", "history" };
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
        private DispatcherTimer _applyDebounce;   // see ChangedLive
        private bool _applyPending;
        private MmrPanelPreview _mmrPreview; // page-local: the live MMR side-panel preview
        private HudPreview _hudPreview;      // page-local: the live trinket / anomaly HUD preview
        private DarkGiftPreview _giftPreview; // page-local: the live Dark Gift panel preview
        private TextBlock _arrangeBtnLabel;
        private readonly List<Action> _modeRefresh = new List<Action>();   // Dark-Gift mode row repaints

        private readonly string _currentVersion;
        private readonly Action _checkForUpdates;
        private readonly Action<UpdateNotice> _openDownloadPage;   // release page in the browser (notify-only updater)
        private readonly Action<string> _skipUpdate;
        private readonly Game.FinalBoard.FinalBoardStore _matchHistory;
        private readonly Action<Game.FinalBoard.FinalBoardRecord> _showMatch;   // opens one match on HDT's overlay canvas
        private TextBlock _shotFolderLabel;
        private System.Windows.Controls.Primitives.Popup _previewPopup;   // hovered match, drawn beside the list
        private FinalBoard.FinalBoardPanel _previewPanel;
        private UpdateNotice _updateNotice;     // most recently pushed state (see RefreshUpdateStatus)
        private bool _onUpdatesPage;
        private StackPanel _updateActionsHost;  // repainted in place, no full page rebuild needed

        internal SettingsWindow(PluginConfig config, Data.CardStore store, HotkeyManager hotkey,
            Action onChanged, Action<ArrangeTarget> onArrange,
            string currentVersion, Action checkForUpdates, Action<UpdateNotice> openDownloadPage,
            Action<string> skipUpdate, Game.FinalBoard.FinalBoardStore matchHistory,
            Action<Game.FinalBoard.FinalBoardRecord> showMatch)
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
            _matchHistory = matchHistory;
            _showMatch = showMatch;

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
                FlushPending();
                _hotkey.EndCapture();
                _hotkey.Suppress(false);
                _hotkey.KeyCaptured -= OnKeyCaptured;
                if (_arranging != ArrangeTarget.None) { _arranging = ArrangeTarget.None; try { _onArrange?.Invoke(ArrangeTarget.None); } catch { } }
                _mmrPreview?.Close(); _mmrPreview = null;
                _hudPreview?.Close(); _hudPreview = null;
                _giftPreview?.Close(); _giftPreview = null;
                ClosePreview();
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
        private StackPanel NewPage(string title, bool sub, Func<bool> master, Action<bool> setMaster,
                                  Action onBack = null)
        {
            EndKeyCapture();              // navigating away cancels a pending key capture
            FlushPending();               // ...but a pending setting still has to land
            _onSubPage = sub;
            _onMainPage = !sub;
            _onUpdatesPage = false; _updateActionsHost = null;   // page-local; rebuilt when its page shows
            // Arranging belongs to the page whose button started it, so leaving that page ends it —
            // otherwise the mode strands with no visible Done button anywhere.
            if (_arranging != ArrangeTarget.None) SetArrange(ArrangeTarget.None);
            _mmrPreview?.Close(); _mmrPreview = null;
            _hudPreview?.Close(); _hudPreview = null;
            _giftPreview?.Close(); _giftPreview = null;
            ClosePreview();
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
                // Back returns to the page you came FROM, which for a page reached from another
                // sub-page is not the main list.
                var goBack = onBack ?? (Action)BuildMain;
                back.MouseLeftButtonUp += (s, e) => goBack();
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
                "A “?” by the Dark Discovery button opens the gift list.",
                () => _config.ShowDarkGifts, v =>
                {
                    _config.ShowDarkGifts = v;
                    _status.Text = v
                        ? "In a match, click the “?” above the Dark Discovery button to see which Dark Gifts are still obtainable."
                        : "Dark Gift list off.";
                    Changed();
                }, BuildDarkGifts));

            stack.Children.Add(CategoryRow("Final Board",
                "Keep every finished match, and pick what the end-of-match panel shows.",
                () => _config.RecordMatchHistory, v =>
                {
                    _config.RecordMatchHistory = v;
                    _status.Text = v
                        ? "Recording matches. Open the sub-page to choose what the panel shows."
                        : "Not recording new matches; the ones already stored are still there.";
                    Changed();
                }, BuildFinalBoard));

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
                    : "Detail portrait drag-out off.";
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
                NormalizeLocationModes();   // panel off → panel-involving tier/type modes snap to their fallback
                Changed();
                UpdateArrangeRow();
                foreach (var r in _modeRefresh) r();   // the tier/type cyclers skip panel options while it is off
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

            NormalizeLocationModes();   // e.g. a fresh config: panel off + mode "Both" → show as "Portraits"
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
                PanelLocationSelectable));

            stack.Children.Add(CycleRow("Minion types",
                new[] { "Both", "Portraits", "Panel", "Off" },
                new[] { "Both", "Portraits", "Panel", "Off" },
                new[] { "Each player's most common minion type — under the tier icon by the portrait, and in the panel.",
                        "Type icon under each leaderboard portrait's tier icon only.",
                        "Type icon inside the side panel only.",
                        "No minion-type icons anywhere." },
                () => _config.OpponentTribeMode, v => _config.OpponentTribeMode = v,
                PanelLocationSelectable));

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

        // With the panel surface off, the panel-involving locations make no sense — snap them to the
        // equivalent panel-less choice ("Both"→"Portraits", "Panel"→"Off") so the selection always
        // sits on an option that's actually selectable. Applies to every Off/Portraits/Panel/Both axis
        // (tavern tiers, minion types).
        private void NormalizeLocationModes()
        {
            if (_config.ShowMmrPanel) return;
            _config.TavernTierMode = PanelLessMode(_config.TavernTierMode);
            _config.OpponentTribeMode = PanelLessMode(_config.OpponentTribeMode);
        }

        private static string PanelLessMode(string m)
        {
            if (string.IsNullOrEmpty(m) || string.Equals(m, "Both", StringComparison.OrdinalIgnoreCase)) return "Portraits";
            if (string.Equals(m, "Panel", StringComparison.OrdinalIgnoreCase)) return "Off";
            return m;
        }

        // CycleRow gate for a location axis: the panel-involving choices are offered only while the
        // panel surface is on.
        private bool PanelLocationSelectable(string v) =>
            _config.ShowMmrPanel
            || (!string.Equals(v, "Both", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, "Panel", StringComparison.OrdinalIgnoreCase));

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
                Text = "What the panel shows. Right-clicking the panel in game cycles these too.",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(ModeRow("Both", "Gift list + minion pool",
                "The full panel — gifts, plus the guaranteed-type minions when they apply."));
            stack.Children.Add(ModeRow("Gifts", "Gift list only",
                "Never show the minion-art column."));
            stack.Children.Add(ModeRow("Minions", "Minion pool only",
                "Only the guaranteed-type minion arts; the panel stays hidden when no pool applies."));

            stack.Children.Add(SliderRow("Max amount of minions to show", PluginConfig.MaxDarkGiftPool,
                () => _config.DarkGiftPoolCount,
                v => _config.DarkGiftPoolCount = v,
                v => v == 0 ? "Tribe only" : v.ToString(),
                v => v == 0
                    ? "The guaranteed tribe is named; none of its minions are drawn."
                    : $"At most {v} of the guaranteed tribe's minions, sole gift-enablers first. "
                      + "Fewer when its pool is smaller.",
                // The gift list on its own never draws the pool, so there is nothing for this to size.
                () => !string.Equals(_config.DarkGiftMode, "Gifts", StringComparison.OrdinalIgnoreCase)));

            stack.Children.Add(Separator());

            // The real panel, in the selected mode. The turn is the one thing a preview cannot know and
            // the thing that changes the panel most, so it is a switch rather than a fixed guess.
            _giftPreview = new DarkGiftPreview(_config, _store, 394);
            _pageRefresh = () => _giftPreview?.Refresh();
            stack.Children.Add(_giftPreview.Root);

            stack.Children.Add(new TextBlock
            {
                Text = "In match: a “?” sits above the Dark Discovery button — click it and the panel opens "
                     + "in the top-right corner. Close it with the ✕ or a click anywhere else. "
                     + "Scroll it with the wheel; right-click it to cycle these modes.",
                Foreground = UiKit.TextMuted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            ShowPage();
        }

        // -- Final Board -----------------------------------------------------------------------
        //
        // This page is a TRIAL of a different row style: every control sits in its own container,
        // the way the main page's category rows already do, and the sentence that used to hang under
        // each label has moved into a "?" beside it. The old shape stacked a 15px label, a wrapped
        // 11.5px sentence and the next label with nothing between them, so the eye had to work out
        // where one setting ended -- and a page of nine switches ran twice as tall as it needed to.
        // The page-level description at the top stays a sentence: it explains the FEATURE, and there
        // is no single control for it to hang off.
        //
        // Deliberately confined to this page. If it reads better than the others, the helpers below
        // are what the rest of them adopt.

        private void BuildFinalBoard()
        {
            var stack = NewPage("Final Board", sub: true, () => _config.RecordMatchHistory, v =>
            {
                _config.RecordMatchHistory = v;
                _status.Text = v ? "Recording matches." : "Not recording new matches.";
                Changed();
            });

            stack.Children.Add(new TextBlock
            {
                Text = "Every match is recorded whole either way \u2014 these choose what the panel draws, "
                     + "so switching one back on shows history that was already kept.",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            string group = null;
            bool firstHeading = true;
            foreach (var b in FinalBoardOptions.Blocks)
            {
                if (b.Group != group)
                {
                    group = b.Group;
                    stack.Children.Add(SectionHeading(group, firstHeading));
                    firstHeading = false;
                }
                stack.Children.Add(BlockRow(b));
            }

            stack.Children.Add(SectionHeading("Screenshots"));
            stack.Children.Add(ShotFolderRow());

            stack.Children.Add(SectionHeading("Match history"));
            stack.Children.Add(HistoryRow());
            stack.Children.Add(SessionClickRow());
            stack.Children.Add(HistoryKeyRow());

            ShowPage();
        }

        // -- the trial row style -----------------------------------------------------------------

        /// <summary>A section heading that outranks the labels under it. The old one was 11.5px
        /// against 15px rows, so it read as a note attached to the first switch rather than as the
        /// title of the group.</summary>
        private static TextBlock SectionHeading(string text, bool first = false) => new TextBlock
        {
            Text = text,
            Foreground = UiKit.TextPrimary, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, first ? 2 : 16, 0, 8)
        };

        /// <summary>The container every row on this page sits in -- same treatment as a main-page
        /// category, which is what stops a column of settings reading as one undifferentiated block.</summary>
        private static Border PageCard(UIElement content) => new Border
        {
            Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 7), Child = content
        };

        /// <summary>
        /// The explanation, folded into a mark beside the label. It costs one line of height instead
        /// of two or three, and it only speaks when asked -- which is the right trade for a sentence
        /// most readers need once and then never again.
        /// </summary>
        private static UIElement HelpIcon(string text)
        {
            if (string.IsNullOrEmpty(text)) return new Border { Width = 0 };

            var glyph = new TextBlock
            {
                Text = "?", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = UiKit.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var mark = new Border
            {
                Width = 17, Height = 17, CornerRadius = new CornerRadius(9),
                Background = UiKit.Br(UiKit.PanelBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Help, Child = glyph
            };
            UiKit.Tip(mark, text);
            mark.MouseEnter += (s, e) => { mark.BorderBrush = UiKit.AccentBrush; glyph.Foreground = UiKit.AccentBrush; };
            mark.MouseLeave += (s, e) => { mark.BorderBrush = UiKit.StrokeBrush; glyph.Foreground = UiKit.TextMuted; };
            return mark;
        }

        /// <summary>Label + "?" on the left, the On/Off pill on the right, all inside one container.</summary>
        private UIElement BlockRow(FinalBoardOptions.Block b)
        {
            var dock = new DockPanel { LastChildFill = true };

            var pill = TogglePill(b.Get(_config.FinalBoardDisplay), v =>
            {
                b.Set(_config.FinalBoardDisplay, v);
                _status.Text = b.Label + (v ? " shown." : " hidden.");
                Changed();
            }, width: 74, get: () => b.Get(_config.FinalBoardDisplay));
            pill.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(pill, Dock.Right);
            dock.Children.Add(pill);

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = b.Label, Foreground = UiKit.TextPrimary, FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });
            left.Children.Add(HelpIcon(b.Desc));
            dock.Children.Add(left);

            return PageCard(dock);
        }

        // -- where the camera writes --------------------------------------------------------------

        private UIElement ShotFolderRow()
        {
            var dock = new DockPanel { LastChildFill = true };

            var btnLabel = new TextBlock { Text = "Change\u2026", Foreground = UiKit.TextPrimary, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            var btn = new Border
            {
                Background = UiKit.Br(UiKit.PanelBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(0, 5, 0, 5), Cursor = Cursors.Hand,
                Width = 74, Child = btnLabel, VerticalAlignment = VerticalAlignment.Center
            };
            btn.MouseLeftButtonUp += (s, e) => { e.Handled = true; ChangeShotFolder(); };
            DockPanel.SetDock(btn, Dock.Right);
            dock.Children.Add(btn);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = "Save folder", Foreground = UiKit.TextPrimary, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(HelpIcon("The camera in the panel's top-right copies the picture to the clipboard "
                                     + "and keeps a copy here. Pictures already saved are not moved."));
            left.Children.Add(head);
            _shotFolderLabel = new TextBlock
            {
                Foreground = UiKit.TextMuted, FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280
            };
            left.Children.Add(_shotFolderLabel);
            dock.Children.Add(left);

            UpdateShotFolderLabel();
            return PageCard(dock);
        }

        private void UpdateShotFolderLabel()
        {
            if (_shotFolderLabel == null) return;
            var dir = FinalBoard.FinalBoardExport.ShareDirFor(_config);
            _shotFolderLabel.Text = dir;
            _shotFolderLabel.ToolTip = dir;
        }

        /// <summary>Points new pictures somewhere else. Nothing is moved -- unlike the art cache, these
        /// are the player's own files in their own folder, and relocating them behind their back is
        /// not ours to do.</summary>
        private void ChangeShotFolder()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Choose where the Final Board camera saves its pictures.";
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                _config.FinalBoardShotDir = dlg.SelectedPath;
            }
            Changed();
            UpdateShotFolderLabel();
            _status.Text = "New pictures will be saved in " + _config.FinalBoardShotDir;
        }

        // -- match history -------------------------------------------------------------------------

        /// <summary>How many rows the history page builds at once. The store is a permanent archive;
        /// past a hundred rows the page costs more to build than anyone scrolls.</summary>
        private const int HistoryRows = 100;

        /// <summary>
        /// One row, not the list. The archive only grows, so inlining it would make this page taller
        /// every time a game is played -- and taller for everyone, including the reader who came here
        /// to flip a switch. It gets a page of its own instead.
        /// </summary>
        private UIElement HistoryRow()
        {
            int count = _matchHistory != null ? _matchHistory.All.Count : 0;

            var dock = new DockPanel { LastChildFill = true };

            var btnLabel = new TextBlock { Text = "Browse\u2026", Foreground = UiKit.TextPrimary, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            var btn = new Border
            {
                Background = UiKit.Br(UiKit.PanelBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(0, 5, 0, 5),
                Width = 74, Child = btnLabel, VerticalAlignment = VerticalAlignment.Center,
                Cursor = count > 0 ? Cursors.Hand : Cursors.Arrow, Opacity = count > 0 ? 1.0 : 0.45
            };
            if (count > 0) btn.MouseLeftButtonUp += (s, e) => { e.Handled = true; BuildMatchHistory(); };
            DockPanel.SetDock(btn, Dock.Right);
            dock.Children.Add(btn);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = "Your matches", Foreground = UiKit.TextPrimary, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(HelpIcon("Pick a match to open its panel on the game overlay. Records go back "
                                     + "further than Hearthstone Deck Tracker's own seven days, which is why "
                                     + "they are copied out."));
            left.Children.Add(head);
            left.Children.Add(new TextBlock
            {
                Text = count == 0 ? "Nothing recorded yet."
                                  : count + (count == 1 ? " match kept." : " matches kept."),
                Foreground = UiKit.TextMuted, FontSize = 11.5
            });
            dock.Children.Add(left);

            return PageCard(dock);
        }

        /// <summary>The in-game route: HDT's session list, one click per game.</summary>
        private UIElement SessionClickRow()
        {
            var dock = new DockPanel { LastChildFill = true };

            var pill = TogglePill(_config.FinalBoardSessionClick, v =>
            {
                _config.FinalBoardSessionClick = v;
                _status.Text = v ? "HDT's session list opens matches." : "HDT's session list left alone.";
                Changed();
            }, width: 74, get: () => _config.FinalBoardSessionClick);
            pill.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(pill, Dock.Right);
            dock.Children.Add(pill);

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = "Click a game in HDT's session list", Foreground = UiKit.TextPrimary, FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });
            left.Children.Add(HelpIcon("The list of this session's games on Hearthstone Deck Tracker's own "
                                     + "Battlegrounds overlay. Its hover preview stays; a click opens the "
                                     + "full panel. While this is on, a click on one of those rows goes "
                                     + "to the row rather than to the game under it."));
            dock.Children.Add(left);

            return PageCard(dock);
        }

        /// <summary>Jump straight to the match history — what the Match history hotkey opens.</summary>
        internal void OpenMatchHistory() => BuildMatchHistory();

        /// <summary>
        /// The binding that summons this page. It lives here rather than with the overlay hotkeys
        /// because it belongs to this feature, and because it is the only one of ours that carries a
        /// modifier -- the "?" says why that changes when it fires.
        /// </summary>
        private UIElement HistoryKeyRow()
        {
            var row = KeyRow("history", "Hotkey");
            var dock = row as DockPanel;
            if (dock != null)
            {
                dock.Margin = new Thickness(0);
                // Slip the "?" in beside the label, which KeyRow adds last.
                var label = dock.Children[dock.Children.Count - 1] as TextBlock;
                if (label != null)
                {
                    dock.Children.Remove(label);
                    var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    left.Children.Add(label);
                    left.Children.Add(HelpIcon("Only fires while Hearthstone or Hearthstone Deck Tracker is "
                                             + "in front. The default, Ctrl+H, already means History in every "
                                             + "browser and Find and Replace in most editors, and this should "
                                             + "not take it away from them."));
                    dock.Children.Add(left);
                }
            }
            return PageCard(row);
        }

        private void BuildMatchHistory()
        {
            var stack = NewPage("Match history", sub: true, master: null, setMaster: null, onBack: BuildFinalBoard);

            stack.Children.Add(new TextBlock
            {
                Text = "Pick one to open its panel on the game overlay.",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            var all = _matchHistory != null ? _matchHistory.All : null;
            if (all == null || all.Count == 0)
            {
                stack.Children.Add(PageCard(new TextBlock
                {
                    Text = "No matches recorded yet.", Foreground = UiKit.TextMuted, FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                }));
                ShowPage();
                return;
            }

            for (int i = 0; i < all.Count && i < HistoryRows; i++)
                stack.Children.Add(MatchRow(all[i]));

            if (all.Count > HistoryRows)
                stack.Children.Add(new TextBlock
                {
                    Text = "\u2026and " + (all.Count - HistoryRows) + " older, kept on disk.",
                    Foreground = UiKit.TextMuted, FontSize = 11.5, Margin = new Thickness(2, 2, 0, 0)
                });

            ShowPage();
        }

        private UIElement MatchRow(Game.FinalBoard.FinalBoardRecord rec)
        {
            var dock = new DockPanel { LastChildFill = true };

            var chev = new TextBlock
            {
                Text = "\u203a", FontSize = 20, Foreground = UiKit.TextMuted,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 2)
            };
            DockPanel.SetDock(chev, Dock.Right);
            dock.Children.Add(chev);

            // The HERO is what a player remembers a run by, so it leads and it is the bright text.
            // The date is how you find a run you already have in mind -- it has to be there, it does
            // not have to be loud. Fixed widths so the columns line up down the list; a ragged edge
            // turns a list you scan into a list you have to read.
            // Tier-A records imported from HDT's store carry only a card id, so the name is resolved
            // the way the panel's header resolves it and the two can never disagree about a hero.
            var hero = rec.HeroName;
            if (string.IsNullOrEmpty(hero)) hero = Game.FinalBoard.FinalBoardCapture.HeroNameOf(rec.HeroCardId);

            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(Col(string.IsNullOrEmpty(hero) ? "Unknown hero" : hero, 158, UiKit.TextPrimary, 13.5, FontWeights.Normal));
            line.Children.Add(Col(FinalBoard.FinalBoardPanel.Ordinal(rec.Placement), 36, PlacementInk(rec), 13.5, FontWeights.SemiBold));
            line.Children.Add(Col(FinalBoard.FinalBoardPanel.Duration(rec) ?? "\u2014", 46, UiKit.TextMuted, 12.5, FontWeights.Normal));
            line.Children.Add(Col(FinalBoard.FinalBoardPanel.Timestamp(rec) ?? "", 0, UiKit.TextMuted, 12, FontWeights.Normal));
            dock.Children.Add(line);

            var card = PageCard(dock);
            card.Cursor = Cursors.Hand;
            card.MouseEnter += (s, e) => { card.BorderBrush = UiKit.AccentBrush; ShowPreview(card, rec); };
            card.MouseLeave += (s, e) => { card.BorderBrush = UiKit.StrokeBrush; ClosePreview(); };
            card.MouseLeftButtonUp += (s, e) => OpenMatch(rec);
            return card;
        }

        /// <summary>
        /// What the hovered row is: the hero, and the warband they finished with. It is the real
        /// panel, not a second drawing of one -- the block switches are exactly the vocabulary needed
        /// to say "header and board, nothing else", so the preview is that panel with those switches,
        /// and anything that changes how a board is drawn changes here for free.
        /// </summary>
        private static readonly FinalBoardOptions PreviewOptions = new FinalBoardOptions
        {
            HeroPortrait = true, MmrDelta = true, MatchMeta = false, PlayerName = false,
            HeadlineTiles = false, Counters = false, TurnTable = false,
            DetailRow = false, Board = true,
        };

        private void ShowPreview(UIElement anchor, Game.FinalBoard.FinalBoardRecord rec)
        {
            try
            {
                if (_previewPanel == null)
                {
                    _previewPanel = new FinalBoard.FinalBoardPanel(PreviewOptions);
                    _previewPanel.ChromeVisible = false;

                    // A Viewbox rather than the panel's own Scale: that one is a RenderTransform, so
                    // the element still MEASURES at its full reference width and the popup around it
                    // would be sized for a panel twice as wide as the one being drawn.
                    var box = new Viewbox { Width = 480, Stretch = Stretch.Uniform, Child = _previewPanel.Root };
                    _previewPopup = new System.Windows.Controls.Primitives.Popup
                    {
                        AllowsTransparency = true,
                        StaysOpen = true,
                        Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
                        HorizontalOffset = -8,
                        Child = new Border { Padding = new Thickness(0), Child = box }
                    };
                }
                _previewPanel.Show(rec);
                _previewPopup.PlacementTarget = anchor;
                _previewPopup.IsOpen = true;
            }
            catch { ClosePreview(); }
        }

        private void ClosePreview()
        {
            try { if (_previewPopup != null) _previewPopup.IsOpen = false; }
            catch { }
        }

        /// <summary>
        /// Show the match over the game. This window gets out of the way and Hearthstone comes
        /// forward, because the panel is drawn on Hearthstone Deck Tracker's overlay and that overlay
        /// is hidden whenever the game is not in front: clicking a match while browsing settings used
        /// to produce nothing at all until the player thought to click the game themselves. The panel
        /// gives the window back when it is closed, on this page, scrolled where it was -- nothing is
        /// rebuilt, so there is nothing to restore.
        /// </summary>
        private void OpenMatch(Game.FinalBoard.FinalBoardRecord rec)
        {
            ClosePreview();

            if (!HearthstoneIsRunning())
            {
                _status.Text = "Hearthstone isn't running.";
                MessageBox.Show(this,
                    "Hearthstone isn't running.\n\n"
                    + "The match panel is drawn on top of the game, so there is nowhere to show it yet. "
                    + "Start Hearthstone, then pick the match again.",
                    "HSBG Card Lookup \u2014 Match history",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _status.Text = "Showing that match over the game. Close it with the \u2715 to come back here.";
            _hiddenForOverlay = true;
            Hide();
            try { _showMatch?.Invoke(rec); } catch { }
        }

        // Set by OpenMatch, cleared by the restore. The panel can also be opened from the game
        // itself (a click on HDT's session list) with this window hidden for its own reasons —
        // that dismissal must not drag the window back up.
        private bool _hiddenForOverlay;

        /// <summary>The overlay panel was dismissed; come back exactly as we were left.</summary>
        internal void RestoreAfterOverlay()
        {
            try
            {
                if (!_hiddenForOverlay) return;
                _hiddenForOverlay = false;
                if (IsVisible) return;
                // The line that sent the reader to the game has been obeyed; leaving it up would have
                // the window still asking for something that already happened.
                _status.Text = "";
                Show();
                Activate();
            }
            catch { }
        }

        private static TextBlock Col(string text, double width, Brush ink, double size, FontWeight weight)
        {
            var tb = new TextBlock
            {
                Text = text ?? "", Foreground = ink, FontSize = size, FontWeight = weight,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (width > 0) tb.Width = width;
            return tb;
        }

        // Same reading as the panel's own placement colour: the top half of the lobby is a good result,
        // and duos has four teams rather than eight players.
        private static Brush PlacementInk(Game.FinalBoard.FinalBoardRecord rec)
        {
            if (rec.Placement <= 0) return UiKit.TextMuted;
            bool good = rec.Duos ? rec.Placement <= 2 : rec.Placement <= 4;
            if (!good) return UiKit.Br(Color.FromRgb(0xEC, 0x69, 0x69));
            return rec.Placement == 1 ? UiKit.AccentBrush : UiKit.Br(Color.FromRgb(0x6D, 0xEB, 0x6C));
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

        /// <summary>
        /// Apply a change that arrives in BURSTS — a slider drag, or a held click on a slider track,
        /// which repeats its step several times a second until the thumb reaches the cursor.
        ///
        /// The caller sets the value and its caption on every tick, so the control itself is exact and
        /// immediate. Everything DOWNSTREAM of the value waits for the burst to end, for two reasons,
        /// both found live:
        ///
        ///   • re-rendering per tick FLICKERS hard — the Dark Gift panel's minion grid re-flows between
        ///     one and four columns as the count crosses a row boundary, and every rebuild reloads its
        ///     card art, so a sweep across the track is a strobe of relayouts;
        ///   • one _onChanged is a config write to disk plus a whole ApplySettings, which re-runs the F3
        ///     overlay's pool and re-applies every feature — a dozen of those for one gesture stuttered
        ///     for seconds while Hearthstone was starting and the disk was already busy.
        ///
        /// Trailing edge, restarted by every tick, so a fast sweep redraws exactly ONCE. The interval is
        /// short enough that a single deliberate step still reads as instant.
        /// </summary>
        private void ChangedLive()
        {
            if (_applyDebounce == null)
            {
                _applyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _applyDebounce.Tick += (s, e) => FlushPending();
            }
            _applyPending = true;
            _applyDebounce.Stop();
            _applyDebounce.Start();   // restart on every tick: the timer fires once the burst settles
        }

        /// <summary>Apply a pending live change now. Everything that ends a burst or walks away from one
        /// calls this, so a value can never be left unrendered or unsaved: the drag-release, leaving the
        /// page, and closing the window.</summary>
        private void FlushPending()
        {
            _applyDebounce?.Stop();
            if (!_applyPending) return;
            _applyPending = false;
            Changed();
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
            // ONE handler, on the box, and the direction comes from WHERE the release landed — not from
            // which glyph caught the event. The arrow glyphs are a few px wide and nothing captures
            // the mouse, so a quick click that let go a pixel off "◄" used to fall through to the
            // box's forward handler: two lefts + two rights no longer round-tripped (live-reported
            // 2026-09-05). The whole left strip (padding + glyph + its margin, with slack) steps back.
            const double backZoneW = 26;
            box.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                step(e.GetPosition(box).X < backZoneW ? -1 : +1);
            };
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


        /// <summary>
        /// An integer slider: label and live value on one line, the track under it. The value caption
        /// carries the meaning of the ends (0 reads "Tribe only", not "0"), so the row needs no
        /// sentence explaining itself — the hint goes to the status line on change, like every other
        /// row here.
        ///
        /// The value applies AS IT MOVES — a drag and a held track click behave the same, which they
        /// did not when only the drag deferred. What keeps that affordable is <see cref="ChangedLive"/>:
        /// the label and the preview follow every tick, while the config write behind them waits for
        /// the burst to end.
        /// </summary>
        private UIElement SliderRow(string label, int max, Func<int> get, Action<int> set,
                                    Func<int, string> caption, Func<int, string> hint, Func<bool> enabled)
        {
            var valueLbl = new TextBlock
            {
                Foreground = UiKit.AccentBrush, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
                MinWidth = 70
            };
            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(valueLbl, Dock.Right);
            head.Children.Add(valueLbl);
            head.Children.Add(new TextBlock
            {
                Text = label, Foreground = UiKit.TextPrimary, FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });

            var slider = new Slider
            {
                Minimum = 0, Maximum = max,
                SmallChange = 1, LargeChange = 1,
                TickFrequency = 1, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 6, 0, 0),
                Focusable = false          // the window routes keys elsewhere; this is a mouse control
            };
            try { slider.Style = SliderStyle(); } catch { }
            slider.Value = Clamp(get(), 0, max);

            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
            row.Children.Add(head);
            row.Children.Add(slider);

            bool suppress = false;
            Action repaint = () =>
            {
                suppress = true;
                int v = Clamp(get(), 0, max);
                if (Math.Abs(slider.Value - v) > 0.01) slider.Value = v;
                suppress = false;
                valueLbl.Text = caption(v);
                bool on = enabled == null || enabled();
                row.Opacity = on ? 1.0 : 0.45;
                row.IsEnabled = on;
            };
            slider.ValueChanged += (s, e) =>
            {
                // A repaint assigning Value must not apply: that calls Changed(), which repaints,
                // which would assign Value again.
                if (suppress) return;
                int v = (int)Math.Round(slider.Value);
                valueLbl.Text = caption(v);
                if (v == get()) return;
                set(v);
                _status.Text = hint(v);
                ChangedLive();
            };
            // Releasing the thumb inside a cooldown would otherwise leave the final value pending.
            slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                new System.Windows.Controls.Primitives.DragCompletedEventHandler((s, e) => FlushPending()));

            repaint();
            _modeRefresh.Add(repaint);
            return row;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Slider chrome in this window's palette — the stock Windows track and thumb are the
        /// wrong colours entirely against a dark panel. Same technique as UiKit.ThinScrollBarStyle:
        /// the template is parsed from XAML, which is far shorter than assembling it in code. Kept
        /// here rather than in UiKit because settings is the only place with a slider.</summary>
        private static Style SliderStyle()
        {
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>" +
                "<Setter Property='Height' Value='22'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='Slider'>" +
                "<Grid Background='Transparent'>" +
                "<Border Height='4' CornerRadius='2' Background='#0A0D14' BorderBrush='#39475E' " +
                "BorderThickness='1' VerticalAlignment='Center'/>" +
                "<Track Name='PART_Track'>" +
                "<Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge'>" +
                "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
                "<Border Background='Transparent'>" +
                "<Border Height='4' CornerRadius='2' Background='#E8B54B' VerticalAlignment='Center'/>" +
                "</Border></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>" +
                "<Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge'>" +
                "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
                "<Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>" +
                "</RepeatButton></Track.IncreaseRepeatButton>" +
                "<Track.Thumb><Thumb>" +
                "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Grid Width='16' Height='16'><Ellipse Fill='#E8B54B' Stroke='#12161E' StrokeThickness='2'/></Grid>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "</Track></Grid></ControlTemplate>" +
                "</Setter.Value></Setter></Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
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
        private void OnKeyCaptured(Key key, ModifierKeys mods)
        {
            Dispatcher.BeginInvoke(new Action(() => HandleCaptured(key, mods)));
        }

        private void HandleCaptured(Key key, ModifierKeys mods)
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
                // A modifier on its own is not a binding, but holding one is how a combo is typed --
                // so this says "keep going", not "wrong key".
                _status.Text = (key == Key.LeftAlt || key == Key.RightAlt || key == Key.System)
                    ? "Alt can't be part of a binding - hold Ctrl or Shift, or press a key on its own."
                    : "Now press the key to combine it with.";
                return;
            }
            if ((mods & ModifierKeys.Alt) != 0)
            {
                // Windows hands Alt+key to the focused window's menu, and a global hook that fires on
                // it while the game has one open is a fight we would lose.
                _status.Text = "Alt can't be part of a binding - try Ctrl or Shift.";
                return;
            }

            mods &= ModifierKeys.Control | ModifierKeys.Shift;
            string ks = HotkeyText.Format(key, mods);

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
                case "history": return _config.MatchHistoryKey;
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
                case "history": _config.MatchHistoryKey = v; break;
            }
        }

        private static string Label(string kind)
        {
            switch (kind)
            {
                case "browser": return "Open overlay";
                case "golden": return "Toggle golden";
                case "focus": return "Focus search";
                case "history": return "Match history";
                default: return kind;
            }
        }
    }
}

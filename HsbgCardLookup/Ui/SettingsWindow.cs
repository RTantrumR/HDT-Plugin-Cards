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
        private readonly HotkeyManager _hotkey;
        private readonly Action _onChanged;
        private readonly Action<bool> _onHudEditMode;   // enter/exit the HUD arrange ("unlock") mode
        private readonly Dictionary<string, TextBlock> _labels = new Dictionary<string, TextBlock>();
        private readonly TextBlock _status;             // single instance, re-parented onto every page
        private string _capturing;   // kind being rebound, or null
        private bool _onSubPage;     // Esc: sub-page → back, main page → close
        private bool _onMainPage;    // so a pushed update-state change can refresh the main page's hint
        private TextBlock _artFolderLabel;   // shows the current art-cache folder
        private Border _artChangeBtn;        // disabled while a move is in progress
        private bool _arranging;             // HUD arrange mode is active
        private Border _arrangeBtn;          // the Arrange/Done toggle button (on the current page, if any)
        private TextBlock _arrangeBtnLabel;
        private readonly List<Action> _modeRefresh = new List<Action>();   // Dark-Gift mode row repaints

        private readonly string _currentVersion;
        private readonly Action _checkForUpdates;
        private readonly Action<UpdateNotice> _openDownloadPage;   // release page in the browser (notify-only updater)
        private readonly Action<string> _skipUpdate;
        private UpdateNotice _updateNotice;     // most recently pushed state (see RefreshUpdateStatus)
        private bool _onUpdatesPage;
        private StackPanel _updateActionsHost;  // repainted in place, no full page rebuild needed

        internal SettingsWindow(PluginConfig config, HotkeyManager hotkey, Action onChanged, Action<bool> onHudEditMode,
            string currentVersion, Action checkForUpdates, Action<UpdateNotice> openDownloadPage,
            Action<string> skipUpdate)
        {
            _config = config;
            _hotkey = hotkey;
            _onChanged = onChanged;
            _onHudEditMode = onHudEditMode;
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

            // Capture mode is on whenever this window is the active one (keys swallowed + routed
            // to us); off the moment focus leaves, so global hotkeys work normally again.
            _hotkey.KeyCaptured += OnKeyCaptured;
            Activated += (s, e) => _hotkey.BeginCapture();
            Deactivated += (s, e) => _hotkey.EndCapture();
            // Closing the window leaves arrange mode so placeholder boxes never strand. (We do NOT exit
            // on Deactivated — the boxes are topmost/no-activate, so you can alt-tab to the game and keep
            // arranging them over it.)
            Closed += (s, e) =>
            {
                _hotkey.EndCapture();
                _hotkey.KeyCaptured -= OnKeyCaptured;
                if (_arranging) { _arranging = false; try { _onHudEditMode?.Invoke(false); } catch { } }
            };
        }

        // ── Pages ─────────────────────────────────────────────────────────────────────────────

        // Fresh page skeleton: title (with a Back button on sub-pages) + the shared status line.
        private StackPanel NewPage(string title, bool sub)
        {
            _capturing = null;            // navigating away cancels a pending key capture
            _onSubPage = sub;
            _onMainPage = !sub;
            _onUpdatesPage = false; _updateActionsHost = null;   // page-local; rebuilt when its page shows
            _arrangeBtn = null; _arrangeBtnLabel = null;   // page-local; rebuilt when its page shows
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
                head.Children.Add(new TextBlock
                {
                    Text = title, Foreground = UiKit.TextPrimary, FontSize = 20, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
                });
                stack.Children.Add(head);
            }
            else stack.Children.Add(UiKit.Title(title, 22));

            (_status.Parent as Panel)?.Children.Remove(_status);
            stack.Children.Add(_status);
            return stack;
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
                    _onChanged();
                    UpdateArrangeRow();
                }, BuildTrinkets));

            stack.Children.Add(CategoryRow("Anomaly HUD",
                "The lobby anomaly shown on the in-game overlay during a match.",
                () => _config.ShowAnomaly, v =>
                {
                    _config.ShowAnomaly = v;
                    _status.Text = v ? "Anomaly HUD on." : "Anomaly HUD off.";
                    _onChanged();
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
                    _onChanged();
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
                    _onChanged();
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
                    _onChanged();
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

            Content = stack;
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
                _onChanged();
            }));

            stack.Children.Add(ToggleRow("Show Duos cards", _config.ShowDuos, v =>
            {
                _config.ShowDuos = v;
                _status.Text = v ? "Duos cards shown." : "Duos cards hidden.";
                _onChanged();
            }));

            stack.Children.Add(ToggleRow("Drag cards from detail view", _config.DragFromDetail, v =>
            {
                _config.DragFromDetail = v;
                _status.Text = v
                    ? "Drag the detail portrait to pull out a floating card."
                    : "Detail portrait drag-out off (click still opens the website).";
                _onChanged();
            }));

            stack.Children.Add(ToggleRow("Drag cards from results grid", _config.DragFromGrid, v =>
            {
                _config.DragFromGrid = v;
                _status.Text = v
                    ? "Drag a grid card to pull out a floating card."
                    : "Grid drag-out off (a grid click just selects).";
                _onChanged();
            }));

            stack.Children.Add(ToggleRow("Hide dragged cards with app", _config.HideDraggedWithApp, v =>
            {
                _config.HideDraggedWithApp = v;
                _status.Text = v
                    ? "Dragged cards hide with the overlay."
                    : "Dragged cards stay on screen when the overlay closes.";
                _onChanged();
            }));

            stack.Children.Add(Separator());
            stack.Children.Add(ArtFolderRow());

            Content = stack;
        }

        private void BuildTrinkets()
        {
            var stack = NewPage("Trinket HUD", sub: true);

            stack.Children.Add(ToggleRow("Show extra trinket boxes (3rd/4th)", _config.ShowExtraTrinkets, v =>
            {
                _config.ShowExtraTrinkets = v;
                _status.Text = v
                    ? "Extra trinket boxes show when an anomaly grants more than two."
                    : "Extra trinket boxes off (just lesser + greater).";
                _onChanged();
            }));

            stack.Children.Add(ArrangeHudRow());

            stack.Children.Add(new TextBlock
            {
                Text = "In match: right-click a trinket card to close it until the match ends, or turn the HUD off.",
                Foreground = UiKit.TextMuted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            Content = stack;
        }

        private void BuildAnomaly()
        {
            var stack = NewPage("Anomaly HUD", sub: true);

            stack.Children.Add(ArrangeHudRow());

            stack.Children.Add(new TextBlock
            {
                Text = "In match: right-click the anomaly card to close it until the match ends, or turn the HUD off.",
                Foreground = UiKit.TextMuted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            Content = stack;
        }

        private void BuildMmr()
        {
            var stack = NewPage("Opponents' MMR", sub: true);

            stack.Children.Add(new TextBlock
            {
                Text = "Mix and match: pick where it shows, then which parts. Any combination works — " +
                       "e.g. tavern tiers alone, a names-only side panel, rating without names…",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(ToggleRow("MMR labels on the portraits", _config.ShowMmrLabels, v =>
            {
                _config.ShowMmrLabels = v;
                _status.Text = v
                    ? "Rating/name labels on the leaderboard portraits."
                    : "No rating/name labels on the portraits. Tavern tiers and the ⚔ marker are separate " +
                      "toggles below — they can stay by the portraits on their own.";
                _onChanged();
            }));

            stack.Children.Add(ToggleRow("Separate movable panel", _config.ShowMmrPanel, v =>
            {
                _config.ShowMmrPanel = v;
                _status.Text = v
                    ? "Standings panel on — drag it anywhere over the game; drag its top-right corner to resize."
                    : "Standings panel off.";
                NormalizeTierMode();   // panel off → panel-involving tier modes snap to their fallback
                _onChanged();
                UpdateArrangeRow();
                foreach (var r in _modeRefresh) r();   // the panel-involving tier options follow this
            }));

            stack.Children.Add(Separator());

            stack.Children.Add(ToggleRow("Player names", _config.ShowOpponentNames, v =>
            {
                _config.ShowOpponentNames = v;
                _status.Text = v ? "Names shown." : "No names on screen (handy when streaming).";
                _onChanged();
            }));

            stack.Children.Add(ToggleRow("MMR rating", _config.ShowMmrRating, v =>
            {
                _config.ShowMmrRating = v;
                _status.Text = v ? "Ratings shown (8000↓ below the leaderboard cutoff)." : "Ratings hidden.";
                _onChanged();
            }));

            stack.Children.Add(ToggleRow("Today's rating change (▲/▼)", _config.ShowMmrDeltas, v =>
            {
                _config.ShowMmrDeltas = v;
                _status.Text = v ? "Daily ▲/▼ deltas shown next to the rating." : "Daily deltas hidden.";
                _onChanged();
            }));

            stack.Children.Add(new TextBlock
            {
                Text = "Tavern tiers",
                Foreground = UiKit.TextPrimary, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 6)
            });
            NormalizeTierMode();   // e.g. a fresh config: panel off + mode "Both" → show as "Portraits"
            Func<string> tierGet = () => _config.TavernTierMode;
            Action<string> tierSet = v => _config.TavernTierMode = v;
            stack.Children.Add(SelectRow(tierGet, tierSet, "Tavern tiers: ",
                "Both", "By the portraits + in the panel", "Icons in both places.",
                () => _config.ShowMmrPanel));
            stack.Children.Add(SelectRow(tierGet, tierSet, "Tavern tiers: ",
                "Portraits", "By the portraits only", "Icons right of each leaderboard portrait — works on its own, no labels needed.", null));
            stack.Children.Add(SelectRow(tierGet, tierSet, "Tavern tiers: ",
                "Panel", "In the panel only", "Icons inside the separate panel; nothing by the portraits.",
                () => _config.ShowMmrPanel));
            stack.Children.Add(SelectRow(tierGet, tierSet, "Tavern tiers: ",
                "Off", "Off", "No tavern-tier icons anywhere.", null));

            stack.Children.Add(ToggleRow("Mark last fought opponent (⚔)", _config.ShowLastOpponent, v =>
            {
                _config.ShowLastOpponent = v;
                _status.Text = v
                    ? "The previous combat's opponent gets a ⚔ marker by their portrait (and in the panel)."
                    : "⚔ marker off.";
                _onChanged();
            }));

            stack.Children.Add(ToggleRow("Dim dead players", _config.DimDeadPlayers, v =>
            {
                _config.DimDeadPlayers = v;
                _status.Text = v ? "Knocked-out players gray out." : "Dead players keep full color.";
                _onChanged();
            }));

            stack.Children.Add(Separator());
            stack.Children.Add(ArrangeHudRow());

            Content = stack;
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
            var stack = NewPage("Dark Gifts", sub: true);

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

            Content = stack;
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

            Content = stack;
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

        // The On/Off pill by itself (marks its click Handled so a category row doesn't also open).
        private Border TogglePill(bool initial, Action<bool> onToggle, double width)
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
                pill.Background = state ? UiKit.Br(UiKit.PanelActive) : UiKit.Br(UiKit.RowBg);
                pill.BorderBrush = state ? UiKit.AccentBrush : UiKit.StrokeBrush;
                lbl.Foreground = state ? UiKit.AccentBrush : UiKit.TextPrimary;
                lbl.Text = state ? "On" : "Off";
            };
            apply();
            pill.MouseLeftButtonUp += (s, e) => { e.Handled = true; state = !state; apply(); onToggle(state); };
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
                Width = 118, Child = lbl   // fixed width so a 1-2 char key doesn't size a tiny/huge box
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

        private UIElement ToggleRow(string label, bool initial, Action<bool> onToggle)
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };

            var pill = TogglePill(initial, onToggle, width: 118);
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
                _onChanged();
            };
            return row;
        }

        // ── HUD arrange ("unlock overlay") ──────────────────────────────────────────────────────

        // Shows every enabled HUD box on screen as a draggable/resizable placeholder so the layout can
        // be set up without being in a match. Enabled only when at least one HUD toggle is on. Lives on
        // the Trinket + Anomaly sub-pages (it arranges both HUDs at once).
        private UIElement ArrangeHudRow()
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 4) };

            _arrangeBtnLabel = new TextBlock { Text = "Arrange…", Foreground = UiKit.TextPrimary, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            _arrangeBtn = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand,
                Width = 118, Child = _arrangeBtnLabel, VerticalAlignment = VerticalAlignment.Center
            };
            _arrangeBtn.MouseLeftButtonUp += (s, e) => ToggleArrange();
            DockPanel.SetDock(_arrangeBtn, Dock.Right);
            dock.Children.Add(_arrangeBtn);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            left.Children.Add(new TextBlock { Text = "Position HUD on screen", Foreground = UiKit.TextPrimary, FontSize = 15 });
            left.Children.Add(new TextBlock
            {
                Text = "Drag a box to move it; drag its top-right corner to resize. Covers trinkets, anomaly + the MMR panel. Needs Hearthstone running.",
                Foreground = UiKit.TextMuted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap
            });
            dock.Children.Add(left);

            UpdateArrangeRow();
            return dock;
        }

        private void ToggleArrange()
        {
            if (_arrangeBtn == null || !_arrangeBtn.IsEnabled) return;
            SetArrange(!_arranging);
        }

        private void SetArrange(bool on)
        {
            _arranging = on;
            try { _onHudEditMode?.Invoke(on); } catch { }
            _status.Text = on
                ? "Arranging HUD — drag a box to move it, drag its top-right corner to resize. Click Done when finished."
                : "HUD positions saved.";
            UpdateArrangeRow();
        }

        // Enable only when a HUD is on; if every HUD is turned off mid-arrange, leave arrange mode.
        // (Null-safe: the arrange button only exists on the Trinket/Anomaly sub-pages.)
        private void UpdateArrangeRow()
        {
            bool any = _config.ShowTrinkets || _config.ShowAnomaly
                || (_config.ShowOpponentMmr && _config.ShowMmrPanel);
            if (!any && _arranging) { SetArrange(false); return; }   // SetArrange re-enters here once locked
            if (_arrangeBtn == null) return;
            _arrangeBtn.IsEnabled = any;
            _arrangeBtn.Opacity = any ? 1.0 : 0.5;
            _arrangeBtnLabel.Text = _arranging ? "Done" : "Arrange…";
            _arrangeBtnLabel.Foreground = _arranging ? UiKit.AccentBrush : UiKit.TextPrimary;
            _arrangeBtn.BorderBrush = _arranging ? UiKit.AccentBrush : UiKit.StrokeBrush;
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
                Width = 118, Child = btnLabel, VerticalAlignment = VerticalAlignment.Center
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
            _labels[kind].Text = "Press a key...";
            _status.Text = "Listening for a key... (Esc to cancel)";
        }

        // Fires on the hook thread (HDT's UI thread); marshal to be safe against reentrancy.
        private void OnKeyCaptured(Key key)
        {
            Dispatcher.BeginInvoke(new Action(() => HandleCaptured(key)));
        }

        private void HandleCaptured(Key key)
        {
            if (_capturing == null)
            {
                if (key == Key.Escape)
                {
                    if (_onSubPage) BuildMain();   // Esc steps back before it closes
                    else Close();
                }
                return;
            }

            if (key == Key.Escape)
            {
                _labels[_capturing].Text = Display(GetKey(_capturing));
                _capturing = null;
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

            // Refresh every row's display (a stolen one just became unbound).
            foreach (var k in Kinds)
                if (_labels.TryGetValue(k, out var l)) l.Text = Display(GetKey(k));

            _status.Text = stolen.Count == 0
                ? $"Saved. \"{Label(bound)}\" bound to {ks}."
                : $"Saved. \"{Label(bound)}\" bound to {ks}; unbound: {string.Join(", ", stolen)}.";

            _onChanged();
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

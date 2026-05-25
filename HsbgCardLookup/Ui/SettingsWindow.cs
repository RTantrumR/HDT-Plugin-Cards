using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HsbgCardLookup.Config;
using HsbgCardLookup.Hotkey;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Settings dialog (opened from the plugin's "Settings" button in HDT options). Rebinds the
    /// four keys (quick panel, full browser, toggle golden, focus search) and toggles "Show Duos
    /// cards". While this window is active the hotkey hook is in CAPTURE mode: every key is
    /// swallowed (so pressing F2 here doesn't summon the overlay) and routed to us. Rebinding to
    /// an already-used key STEALS it — the previous owner is set to unbound, with a notice.
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private static readonly string[] Kinds = { "browser", "golden", "focus" };
        private const string Unbound = "None";

        private readonly PluginConfig _config;
        private readonly HotkeyManager _hotkey;
        private readonly Action _onChanged;
        private readonly Dictionary<string, TextBlock> _labels = new Dictionary<string, TextBlock>();
        private readonly TextBlock _status;
        private string _capturing;   // kind being rebound, or null

        public SettingsWindow(PluginConfig config, HotkeyManager hotkey, Action onChanged)
        {
            _config = config;
            _hotkey = hotkey;
            _onChanged = onChanged;

            Title = "HSBG Card Lookup - Settings";
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            Width = 470;
            Height = 430;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E));

            var stack = new StackPanel { Margin = new Thickness(22) };
            stack.Children.Add(UiKit.Title("Settings", 22));
            stack.Children.Add(new TextBlock
            {
                Text = "Click a binding, then press the key to assign. Esc cancels; Alt can't be bound. " +
                       "Reusing a key moves it here and unbinds its previous owner.",
                Foreground = UiKit.TextMuted, FontSize = 13, Margin = new Thickness(0, 6, 0, 12),
                TextWrapping = TextWrapping.Wrap
            });

            // Notice line lives at the top so "saved / unbound" feedback is immediately visible.
            _status = new TextBlock
            {
                Foreground = UiKit.AccentBrush, FontSize = 13, MinHeight = 18,
                Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(_status);

            stack.Children.Add(KeyRow("browser", "Open overlay"));
            stack.Children.Add(KeyRow("golden", "Toggle golden"));
            stack.Children.Add(KeyRow("focus", "Focus search"));

            stack.Children.Add(new Border { Height = 1, Background = UiKit.StrokeBrush, Margin = new Thickness(0, 6, 0, 12) });

            stack.Children.Add(ToggleRow("Show Duos cards", _config.ShowDuos, v =>
            {
                _config.ShowDuos = v;
                _status.Text = v ? "Duos cards shown." : "Duos cards hidden.";
                _onChanged();
            }));

            var closeLabel = new TextBlock { Text = "Close", Foreground = UiKit.TextPrimary, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            var close = new Border
            {
                Background = UiKit.Br(UiKit.RowBg), BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 8, 18, 8), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Child = closeLabel
            };
            close.MouseLeftButtonUp += (s, e) => Close();
            stack.Children.Add(close);

            Content = stack;

            // Capture mode is on whenever this window is the active one (keys swallowed + routed
            // to us); off the moment focus leaves, so global hotkeys work normally again.
            _hotkey.KeyCaptured += OnKeyCaptured;
            Activated += (s, e) => _hotkey.BeginCapture();
            Deactivated += (s, e) => _hotkey.EndCapture();
            Closed += (s, e) => { _hotkey.EndCapture(); _hotkey.KeyCaptured -= OnKeyCaptured; };
        }

        // ── Rows ──────────────────────────────────────────────────────────────────────────────

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

            bool state = initial;
            var lbl = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var pill = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(0, 6, 0, 6), Width = 118, Cursor = Cursors.Hand, Child = lbl
            };
            Action apply = () =>
            {
                pill.Background = state ? UiKit.Br(UiKit.PanelActive) : UiKit.Br(UiKit.RowBg);
                pill.BorderBrush = state ? UiKit.AccentBrush : UiKit.StrokeBrush;
                lbl.Foreground = state ? UiKit.AccentBrush : UiKit.TextPrimary;
                lbl.Text = state ? "On" : "Off";
            };
            apply();
            pill.MouseLeftButtonUp += (s, e) => { state = !state; apply(); onToggle(state); };
            DockPanel.SetDock(pill, Dock.Right);
            dock.Children.Add(pill);

            dock.Children.Add(new TextBlock
            {
                Text = label, Foreground = UiKit.TextPrimary, FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });
            return dock;
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
                if (key == Key.Escape) Close();   // window active, nothing being rebound
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
            foreach (var k in Kinds) _labels[k].Text = Display(GetKey(k));

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

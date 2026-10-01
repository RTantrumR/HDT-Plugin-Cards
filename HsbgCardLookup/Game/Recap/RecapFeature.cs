using System;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker;        // Core.Game
using Hearthstone_Deck_Tracker.Hearthstone;   // Database
using HsbgCardLookup.Config;
using HsbgCardLookup.Ui;

namespace HsbgCardLookup.Game.Recap
{
    /// <summary>
    /// The match-recap feature end to end: tracks the solo match in progress, saves it at the end,
    /// shows the panel with the comparison against this season's history, and dismisses it either
    /// by its ✕ or 10 s into the next match. Tracking runs whenever the toggle is on so the history
    /// accumulates even while the panel is closed early.
    /// </summary>
    internal sealed class RecapFeature
    {
        private const int DismissAfterStartMs = 10_000;
        private const int PlacementRetries = 80;   // × ~100 ms OnUpdate = ~8 s for HDT's own store entry

        private readonly PluginConfig _config;
        private readonly Dispatcher _ui;
        private readonly Action<string> _log;
        private readonly RecapTracker _tracker;
        private readonly RecapStore _store;
        private RecapPanel _panel;                 // created on the canvas thread, on first use
        private readonly RecapSessionRowHook _rows; // a click on HDT's "Latest Games" row re-opens that match

        private volatile bool _startFlag, _endFlag;
        private bool _finishing, _scored;
        private int _placementWait;
        private RecapRecord _finished;
        private DispatcherTimer _dismiss;

        public RecapFeature(PluginConfig config, Dispatcher ui, Action<string> log)
        {
            _config = config;
            _ui = ui;
            _log = log;
            _tracker = new RecapTracker(log);
            _store = new RecapStore(log);
            _store.Load();
            Log("store: " + _store.All.Count + " record(s)");
            _rows = new RecapSessionRowHook(_store, () => _config.ShowMatchRecap, ShowRecord, log);

            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Add(new Action(() => _startFlag = true)); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameEnd.Add(new Action(() => _endFlag = true)); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnEntityWillTakeDamage.Add(new Action<Hearthstone_Deck_Tracker.API.PredamageInfo>(info => _tracker.HandlePredamage(info))); } catch { }
        }

        public RecapStore Store => _store;

        /// <summary>Records this season, after the last reset — what the settings page reports.</summary>
        public int ComparableCount => _store.Comparable(_config.RecapSeason, _config.RecapResetAt, null).Count;

        /// <summary>~100 ms from Plugin.OnUpdate. Everything runs on HDT's UI thread.</summary>
        public void Poll()
        {
            try
            {
                if (_startFlag)
                {
                    _startFlag = false;
                    OnMatchStart();
                }
                _rows.Poll();   // checks the toggle itself, and un-promotes the rows when it is off
                if (!_config.ShowMatchRecap) { _endFlag = false; return; }

                _tracker.Poll();

                if (_endFlag)
                {
                    _endFlag = false;
                    _finishing = !_scored && _tracker.Current != null;   // one recap per match, whatever HDT re-fires
                    _placementWait = 0;
                }
                if (_finishing) TryFinish();
            }
            catch (Exception ex) { Log("Poll error: " + ex.Message); }
        }

        private void OnMatchStart()
        {
            _finishing = false;
            _scored = false;
            _tracker.Reset();
            var canvas = Canvas();
            if (canvas != null && _config.ShowMatchRecap && _config.RecapAutoDismiss && _panel != null && _panel.IsVisible)
            {
                if (_dismiss == null)
                {
                    _dismiss = new DispatcherTimer(DispatcherPriority.Normal, canvas.Dispatcher) { Interval = TimeSpan.FromMilliseconds(DismissAfterStartMs) };
                    _dismiss.Tick += (s, e) => { _dismiss.Stop(); HidePanel(); };
                }
                _dismiss.Stop();
                _dismiss.Start();
            }
        }

        /// <summary>
        /// Placement comes from HDT's own last-games store, which fills ~3 s after OnGameEnd under
        /// the same StartTime key we use — the first live match showed PLAYER_LEADERBOARD_PLACE on
        /// the player entity still 0 a full 2.5 s after the end (1st place). The tag stays as the
        /// fallback once the wait runs out.
        /// </summary>
        private void TryFinish()
        {
            var g = Core.Game;
            int place = PlacementFromHdt(_tracker.Current);
            if (place <= 0 && ++_placementWait < PlacementRetries) return;
            _finishing = false;
            _scored = true;

            var rec = _tracker.Finish();
            if (rec == null || rec.Turns.Count == 0) { Log("nothing to score"); return; }
            if (place > 0) rec.Placement = place;
            else Log("placement: HDT store had no entry after " + PlacementRetries + " polls, tag=" + rec.Placement);
            if (string.IsNullOrEmpty(rec.GameId)) rec.GameId = rec.StartedAt.ToString("o");
            rec.Season = _config.RecapSeason ?? "";
            rec.HeroName = HeroNameOf(rec.HeroCardId);

            var history = _store.Comparable(rec.Season, _config.RecapResetAt, rec);
            _store.Save(rec);
            _finished = rec;
            Log(string.Format("match saved | place={0} turns={1} actions={2} window={3:0}s apm={4:0.0} dealt={5} combats={6} | history={7}",
                rec.Placement, rec.Turns.Count, rec.ActionCount, rec.WindowSeconds, rec.MatchApm, rec.TotalDamage, rec.CombatsFought, history.Count));

            var text = RecapText.Build(rec, history);
            ShowPanel(text);
        }

        /// <summary>Re-open a stored match (a session-row click): scored against the same history
        /// it would have had, i.e. everything comparable except itself.</summary>
        public void ShowRecord(RecapRecord rec)
        {
            if (rec == null) return;
            var history = _store.Comparable(rec.Season, _config.RecapResetAt, rec);
            ShowPanel(RecapText.Build(rec, history));
        }

        private void ShowPanel(RecapText text)
        {
            var canvas = Canvas();
            if (canvas == null) return;
            canvas.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_panel == null)
                    {
                        _panel = new RecapPanel();
                        _panel.CloseRequested = () => HidePanel();
                        _panel.GeometryChanged = (xf, yf) =>
                        {
                            _config.RecapHud.Set = true;
                            _config.RecapHud.XF = xf;
                            _config.RecapHud.YF = yf;
                            try { _config.Save(); } catch { }
                        };
                    }
                    if (_config.RecapHud.Set) _panel.Place(_config.RecapHud.XF, _config.RecapHud.YF);
                    _panel.Show(text);
                }
                catch (Exception ex) { Log("show failed: " + ex.Message); }
            }));
        }

        private void HidePanel()
        {
            var canvas = Canvas();
            if (canvas == null || _panel == null) return;
            canvas.Dispatcher.BeginInvoke(new Action(() => { try { _panel.Hide(); } catch { } }));
        }

        /// <summary>Settings changed: a switched-off feature takes its panel with it.</summary>
        public void Apply()
        {
            if (!_config.ShowMatchRecap) HidePanel();
        }

        public void Unload()
        {
            try { _dismiss?.Stop(); } catch { }
            var canvas = Canvas();
            if (canvas == null || _panel == null) return;
            try { canvas.Dispatcher.Invoke(new Action(() => _panel.Close())); } catch { }
        }

        /// <summary>HDT's BgsLastGames entry for this match (keyed by CurrentGameStats.StartTime "o"), or 0.</summary>
        private static int PlacementFromHdt(RecapRecord rec)
        {
            try
            {
                if (rec == null || string.IsNullOrEmpty(rec.GameId)) return 0;
                var games = Hearthstone_Deck_Tracker.Utility.Battlegrounds.BattlegroundsLastGames.Instance?.Games;
                if (games == null) return 0;
                foreach (var g in games)
                    if (g != null && g.StartTime == rec.GameId && g.Placement > 0) return g.Placement;
            }
            catch { }
            return 0;
        }

        /// <summary>Hero name via HDT's database, resolved through a skin's parent so every skin names the hero.</summary>
        private static string HeroNameOf(string heroCardId)
        {
            try
            {
                if (string.IsNullOrEmpty(heroCardId)) return null;
                var card = Database.GetCardFromId(heroCardId);
                if (card != null && card.BattlegroundsSkinParentId > 0)
                    card = Database.GetCardFromDbfId(card.BattlegroundsSkinParentId, false) ?? card;
                return card != null ? card.LocalizedName : null;
            }
            catch { return null; }
        }

        private static System.Windows.Controls.Canvas Canvas()
        {
            try { return Hearthstone_Deck_Tracker.API.Core.OverlayCanvas; } catch { return null; }
        }

        private void Log(string msg)
        {
            try { _log?.Invoke("[Recap] " + msg); } catch { }
        }
    }
}

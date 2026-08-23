namespace HsbgCardLookup.Ui
{
    /// <summary>Which feature the user is currently positioning, if any.</summary>
    internal enum ArrangeTarget
    {
        None,
        Trinkets,
        Anomaly,
        MmrPanel
    }

    /// <summary>
    /// The one owner of "we are arranging X right now".
    ///
    /// Arranging used to light up every HUD feature at once, which made positioning one of them a
    /// hunt through the others. While a session is active exactly one feature is on screen: the one
    /// being arranged is forced VISIBLE even if its own toggle is off (you can lay a feature out
    /// before switching it on), and everything else is forced away.
    ///
    /// Both halves are temporary and live only here — nothing in this class touches
    /// <see cref="Config.PluginConfig"/>, which is what guarantees an arrange session can't leave a
    /// feature switched on or off behind it. Geometry the user drags still persists, because that
    /// goes through each card's own GeometryChanged callback.
    ///
    /// The per-surface predicates are deliberately separate even though they currently agree: when a
    /// rule changes (say the search button should stay up while arranging), it changes here and no
    /// feature grows a flag of its own.
    ///
    /// Deliberately NOT suppressed: dragged-out floating cards (the user placed those on purpose, and
    /// yanking them is more surprising than helpful) and the F3 search overlay (already hidden unless
    /// summoned).
    ///
    /// A static is the right shape here — unlike a canvas host, this genuinely is one process-wide
    /// mode: one settings window (enforced by Plugin.OpenSettings), one screen, one user. The field is
    /// written on the UI thread and read on the OnUpdate thread, hence volatile; it must stay a single
    /// value, since a compound state would need real synchronisation.
    /// </summary>
    internal static class ArrangeSession
    {
        // volatile cannot be applied to an enum field, so the backing store is its int value.
        private static volatile int _active = (int)ArrangeTarget.None;

        public static ArrangeTarget Active => (ArrangeTarget)_active;

        public static bool IsActive => Active != ArrangeTarget.None;

        public static void Set(ArrangeTarget target) => _active = (int)target;

        // ── What each surface is allowed to render while a session is running ──────────────────
        public static bool AllowsTrinkets => !IsActive || Active == ArrangeTarget.Trinkets;
        public static bool AllowsAnomaly => !IsActive || Active == ArrangeTarget.Anomaly;
        public static bool AllowsMmrPanel => !IsActive || Active == ArrangeTarget.MmrPanel;
        public static bool AllowsMmrLabels => !IsActive;   // fixed to the portraits; never arrangeable
        public static bool AllowsDarkGifts => !IsActive;
        public static bool AllowsSearchButton => !IsActive;
    }
}

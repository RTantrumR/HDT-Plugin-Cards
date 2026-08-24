using Hearthstone_Deck_Tracker.API;                 // Core.OverlayCanvas

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Shared sizing for the settings previews: they render the REAL canvas features (the MMR side
    /// panel, the HUD cards, the Dark Gift panel) into a canvas of our own, and every one of those
    /// scales itself against the size of the canvas it sits in. Get that size wrong and the preview
    /// lies about the one thing it exists to show.
    /// </summary>
    internal static class PreviewStage
    {
        public const double FallbackW = 1920, FallbackH = 1080;

        /// <summary>
        /// The LOGICAL size of the live overlay canvas. HDT's canvas is the exact coordinate space
        /// while it is up; otherwise the game's client rect in DIPs.
        ///
        /// Never raw pixels: <c>HsGeometry.TryClientSize</c> returns physical px, and at 125% Windows
        /// scaling a pixel-sized stage renders every font and card a quarter too small relative to
        /// the game — which is precisely the comparison a preview is for.
        /// </summary>
        public static void ResolveSize(out double cw, out double ch)
        {
            try
            {
                var canvas = Core.OverlayCanvas;
                if (canvas != null && canvas.ActualWidth > 0 && canvas.ActualHeight > 0)
                {
                    cw = canvas.ActualWidth; ch = canvas.ActualHeight;
                    return;
                }
            }
            catch { }
            try
            {
                var r = Hearthstone_Deck_Tracker.User32.GetHearthstoneRect(true);   // true = DIPs
                if (r.Width > 0 && r.Height > 0) { cw = r.Width; ch = r.Height; return; }
            }
            catch { }
            cw = FallbackW; ch = FallbackH;
        }
    }
}

v0.7.0: Match Recap and a new name

### The plugin is now called Tantrum's Battlegrounds Toolkit
"HSBG Card Lookup" described the first feature, not what the plugin has grown into.
- **Nothing to do on your side**: update the usual way and the plugin stays enabled. Hearthstone Deck Tracker lists it under the new name from the first launch.
- **Only the name changed**: the download is still `HsbgCardLookup-vX.Y.Z.zip`, the plugin folder is still `HsbgCardLookup`, and your settings and card art stay where they are.

### Match Recap
When a solo Battlegrounds match ends, a small panel in the top-left corner sums up how you played it.
- **APM**: actions per minute, counted over the time you were actually acting rather than the whole shop timer, plus your best turn and your peak burst (the most actions inside any 10 seconds).
- **Damage dealt**: the total, and the average per combat.
- **Against your own season**: once 5 matches are recorded, the panel shows your averages next to this match's numbers and tells you whether you were above or below them. The history starts over with each new season, and "Reset history" in the settings starts it over by hand.
- **The panel**: drag it to move it, ✕ closes it, and it disappears on its own 10 seconds into your next match (this can be switched off).
- **Looking back**: click a match in HDT's "Latest Games" list to open its recap again. Only matches played with the recap switched on have one.
- **Settings**: a new "Match recap" page. Off by default, solo only for now.

### Opponent info is on from the start
A new install used to show nothing in a match until you found the right switches in the settings.
- **A first run now starts with the setup most players and streamers use**: every opponent's tavern tier and most common minion type next to their leaderboard portrait, and a movable panel with their names and MMR.
- **Existing setups are left as they are.** If you have used the plugin before, nothing changes on update.
- **Everything else** (trinkets, anomaly, Dark Gifts, match recap, match export) is still switched on in the settings.

### Installer
Two reports of "I installed it and nothing happened" traced back to the install script.
- **A first install now switches the plugin on.** Hearthstone Deck Tracker keeps a newly installed plugin switched off and never asks about it, so the plugin could sit there installed and invisible. If the plugin has never run on your PC, the installer enables it in HDT for you. If you have used it before, your own choice is left alone.
- **HDT is closed and reopened properly.** The installer asks HDT to close, waits for it, and stops with a clear message if it cannot close it (for example when HDT runs as administrator), instead of starting a second copy next to the first.
- **The result is checked.** The installer verifies the files really arrived and ends with either INSTALLED OK or INSTALL FAILED and the reason.
- **If the plugin is still missing in HDT**: open Options → Tracker → Plugins and enable "Tantrum's Battlegrounds Toolkit".

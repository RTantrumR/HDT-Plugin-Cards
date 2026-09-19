v0.6.0: Opponent Minion Types, the Dark Gift "?" and Aberrations

Work in progress — this file is filled in as features are merged into master, not written after the fact.

### Opponent Minion Types (Opponent MMR)
Each opponent's most common minion type — the "4 Beasts" the game shows when you focus a leaderboard portrait — now shows all the time, for every player from turn 1, on both Opponent MMR surfaces.
- **By the portraits**: a round type icon directly under the tavern-tier icon, with the count beside it. The ⚔ last-opponent marker moves right of it.
- **In the standings panel**: an icon + count column after the tier column.
- **Three states, matching the game's own rule**: the leading type with its count; the "All" art when types are tied (the game shows no count either); the Neutral (Brann) art when a board has no typed minions. Players with no data yet show nothing.
- **Settings**: a new "Minion types" location switch (Off / Portraits / Panel / Both) next to "Tavern tiers", with the same behaviour while the panel is off.

Under the hood: Hearthstone Deck Tracker never receives these counts (the game sends them as a separate real-time message), so the plugin reads them from the client the same way HDT reads its own data. A Hearthstone patch that moves the relevant fields degrades this to "no icons" rather than breaking anything else.

### Dark Gifts: opened by a "?", parked in the top-right corner
The gift panel no longer appears while you hover the Dark Discovery button. Hovering that button to read its own tooltip used to throw a large panel across the middle of the board, every time.
- **A small round "?" sits just above the Dark Discovery button.** Click it and the panel opens — nothing shows up until you ask for it.
- **The panel opens pinned to the top-right corner** instead of beside the cursor, so the shop and your own board stay readable.
- **Closing it**: the ✕ in its corner, a left click anywhere outside it, or pressing Dark Discovery, which closes the panel on its own. While the panel is open it covers the Dark Discovery button, so it has to be closed before you can press it.
- **The "?" only appears when there is something behind it** — the lobby has to actually be running Dark Discovery and a gift has to still be offerable this turn. It dims, and stops responding to clicks, whenever the current display mode would open an empty panel (Minions-only before the turn-6 guaranteed type, for instance).

Also in this release:
- The panel — and the "?" with it — stays hidden for the rest of the match once all three Dark Gifts have been used.
- Pool cells now show the card's name behind the art, so a card whose render is unavailable no longer leaves an empty hole in the grid.

### Aberrations
Hearthstone's new minion type arrives with the September 22 patch, and the plugin already knows it.
- **Aberration is in the minion type filter**, with its own icon, at the top of the list.
- **"aberration" works as a search word**, alone or alongside anything else — `t4 aberration` for the tier-4 ones. Partial and misspelled forms land too.
- **The opponents' minion type icons read it**, so a board leaning on Aberrations shows the Aberration icon and its count, the same as any other type.
- **The Dark Gift panel counts it** among the lobby's types when it works out which gifts can still turn up.

The cards themselves — the new Aberration minions, and the Naga leaving the pool — arrive from hsbg.cards on their own once the patch is live. Nothing to reinstall.

### Settings
- The Off / Portraits / Panel / Both switches step in the right direction on quick clicks (the back arrow's tiny hit target could hand a fast click to the forward handler).

v0.6.0: Opponent Minion Types

Work in progress — this file is filled in as features are merged into master, not written after the fact.

### Opponent Minion Types (Opponent MMR)
Each opponent's most common minion type — the "4 Beasts" the game shows when you focus a leaderboard portrait — now shows all the time, for every player from turn 1, on both Opponent MMR surfaces.
- **By the portraits**: a round type icon directly under the tavern-tier icon, with the count beside it. The ⚔ last-opponent marker moves right of it.
- **In the standings panel**: an icon + count column after the tier column.
- **Three states, matching the game's own rule**: the leading type with its count; the "All" art when types are tied (the game shows no count either); the Neutral (Brann) art when a board has no typed minions. Players with no data yet show nothing.
- **Settings**: a new "Minion types" location switch (Off / Portraits / Panel / Both) next to "Tavern tiers", with the same behaviour while the panel is off.

Under the hood: Hearthstone Deck Tracker never receives these counts (the game sends them as a separate real-time message), so the plugin reads them from the client the same way HDT reads its own data. A Hearthstone patch that moves the relevant fields degrades this to "no icons" rather than breaking anything else.

### Dark Gifts
- The panel stays hidden for the rest of the match once all three Dark Gifts have been used.
- Pool cells now show the card's name behind the art, so a card whose render is unavailable no longer leaves an empty hole in the grid.

### Settings
- The Off / Portraits / Panel / Both switches step in the right direction on quick clicks (the back arrow's tiny hit target could hand a fast click to the forward handler).

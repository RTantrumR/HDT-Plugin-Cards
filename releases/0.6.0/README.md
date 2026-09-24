v0.6.0: Opponent Minion Types, Aberrations and the Dark Gift "?"

### Opponent Minion Types (Opponent MMR)
Each opponent's most common minion type (the "4 Beasts" the game shows when you focus a leaderboard portrait) now shows all the time, for every player from turn 1, on both Opponent MMR surfaces.
- **By the portraits**: a round type icon directly under the tavern-tier icon, with the count beside it. The ⚔ last-opponent marker moves to the right of it.
- **In the separate panel**: an icon + count column after the tier column.
- **Three states, matching the game's own rule**: the leading type with its count, the "All" art when types are tied (the game shows no count either), and the Neutral (Brann) art when a board has no typed minions. Players with no data yet show nothing.
- **Settings**: a new "Minion types" location switch (Off / Portraits / Panel / Both) next to "Tavern tiers", with the same behaviour while the panel is off.
- **Underneath** - HDT never receives these counts, and hence does not provide them (the game sends them as a separate real-time message), so the plugin reads them from the client the same way HDT reads its own data.

### Aberrations
Hearthstone's new minion type arrives with the September 22 patch, and the plugin has been prepared for it in advance.
- **The plugin is mostly ready for Aberrations**, although before they actually arrive in the game and we can test their full implementation, there may be a need for a separate 0.6.1 update for the fixes of our guesses on the game logic from previous patterns.
- **Delay** - after the update on 22.09.26 there will be a small delay, can be from a few hours to half a day, where the website's database will need updating, and as the plugin takes the information from [website's open API](https://hsbg.cards/api-docs) it will need to wait until the API updates with a new card extraction. Please be patient and check for the plugin updates a day after the game update, as we do not have much control of it.

### Dark Gifts: opened by a "?", parked in the top-right corner
The gift panel no longer appears while you hover the Dark Discovery button. Hovering that button to read its own tooltip used to throw a large panel across the middle of the board, every time.
- **A small round "?" sits just above the Dark Discovery button.** Click it and the panel opens.
- **The panel opens pinned to the top-right corner** instead of beside the cursor, this is made purely to make the shop and board at least partially readable on "Both" setting, and fully readable on either just minions or dark gifts.
- **Closing it**: the ✕ in its corner, a left click anywhere outside it, or pressing Dark Discovery through the panel, which closes the panel on its own, and some other conditions may trigger the close automatically.
- **The "?" only appears when there is something behind it** - the lobby has to actually be running Dark Discovery and a gift has to still be offerable this turn. It dims, and stops responding to clicks, whenever the current display mode would open an empty panel (Minions-only before the turn-6 guaranteed type, for instance, or all uses are done).

### Settings
- Settings got some small bug fixes and specifications on a few options.

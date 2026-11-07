v0.8.1: Clearer opponent settings

### Portrait/Sidebar overlay
- **New name**: the "Opponents' MMR" settings page is now **Portrait/Sidebar overlay**, because it holds more than MMR: it decides everything shown by the leaderboard portraits and in the movable sidebar.
- **Four matching selectors**: MMR rating, tavern tiers, minion types and player names each go on the **Portraits**, in the **Sidebar**, on **Both**, or **Off**.
- **No more surface switches**: the sidebar appears when something is assigned to it and disappears when nothing is, so there is nothing to switch on first.
- **Name type** is its own setting under Details: **Battletags** or **Hero names**, wherever names show. It is greyed out while names are Off.
- **The hint under the title no longer moves the page**: it has a fixed two lines, so the rows stay put while you click through the options.

### Trinket and Anomaly HUD
- Their descriptions now say what they are for: an always-on panel for stream viewers on phones or without the Twitch extension. The Anomaly HUD notes that it does nothing until anomalies are back in the game.

### Settings menu
- Reordered: Portrait/Sidebar overlay and Dark Gifts first, then Card search, the two HUDs, Updates and Language.

### Match recap removed
- The end-of-match recap (APM, damage dealt against your averages) is gone for now. It may return later with other features. Your recorded matches stay in the plugin's data folder under `recaps`.

### Install and update with a wizard
- Releases now ship **TantrumsBattlegroundsToolkit-Setup-0.8.1.exe**: run it, and it closes HDT, installs the plugin, switches it on when it is new on this PC and offers to start HDT again. Updating is the same file over the old install.
- The in-plugin update notice now links straight to that Setup.exe.
- The zip with `install.bat` is still attached to this release for anyone who prefers it.

### Good to know
- Your layout is kept when you update. The exception is a setup that had the MMR rating switched off: today's rating change now needs the rating to be shown, and battletags on both the portraits and the sidebar stay in the sidebar only.

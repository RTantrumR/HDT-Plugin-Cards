v1.0.0: Runs with or without Hearthstone Deck Tracker

> The standalone program is new and still in beta. The HDT plugin and Setup are the tested part of this release. If something in the standalone program misbehaves, please open an issue, and you can switch back to the plugin at any time by running Setup again.

### Choose how the toolkit runs
- Setup now asks **how the toolkit should run**: on its own as a **standalone program**, or inside Hearthstone Deck Tracker as before. HDT is no longer required.
- Only one can run at a time. Installing one switches the other off, so you never see everything twice. If both do end up running, each warns you and tells you how to switch one off.
- To change your mind later, run Setup again and pick the other option.

### The standalone program (beta)
- It draws over Hearthstone by itself: opponents' names, MMR, tavern tiers and minion types on the portraits and in the sidebar, the Dark Gift list, the trinket and anomaly HUDs, and the F3 card search with its in-game button.
- It reads the game directly instead of going through HDT, so tiers and minion types often update sooner.
- It lives in the tray. Right-click its icon to open the card search or the settings, or to exit.
- **Start with Windows** is your choice in Setup; there is no default. When it's on, the program waits in the tray using about 2 MB of memory, starts the toolkit when Hearthstone starts and closes it again with the game. When it's off, Setup can put a shortcut on your desktop. You can change this later in the toolkit's settings or in Task Manager under Startup apps.
- It keeps its own settings in `%APPDATA%\TantrumsBattlegroundsToolkit`. On its first start it copies your plugin's settings and downloaded card art, so nothing has to be set up or downloaded again.
- It uninstalls from Apps & features like any other program.

### Setup in your language
- Setup runs in 12 languages. The selector at the top right changes Setup's language on any page.
- A page near the start picks the **toolkit's** language, shown with the same flags as in the toolkit. It starts on Setup's language, and an update keeps whatever language you already use.

### Good to know
- For HDT plugin users nothing changes in how the features look or work. A lot changed underneath to let the same features run without HDT.
- The standalone program can't follow HDT's language setting. If yours was set to follow HDT, the standalone program shows English until you pick a language in its settings.
- The zip with `install.bat` is still attached for anyone who installs the HDT plugin by hand.

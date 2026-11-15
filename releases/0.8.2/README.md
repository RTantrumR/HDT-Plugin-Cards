v0.8.2: Setup waits for Hearthstone Deck Tracker

### Setup
- **Setup now makes sure Hearthstone Deck Tracker is installed before it installs anything.** The toolkit is a plugin that runs inside HDT, with no window or desktop icon of its own. Until now Setup installed it on a PC without HDT and said it was done, so nothing ever started.
- **On a PC without HDT, Setup stops on a "Hearthstone Deck Tracker" page**, and **Next** stays greyed out until HDT is found. The page explains what to do and has three buttons:
  - **Download HDT** opens hsdecktracker.net.
  - **Check again** looks for HDT once you have installed it, so you don't have to restart Setup.
  - **Locate HDT** lets you pick `HearthstoneDeckTracker.exe` yourself, for an HDT in a folder Setup does not look in, such as the portable version.
- **Setup finds HDT in more places**: a running HDT, its usual install folder, its entry in Apps & features, and the HDT used by your last toolkit install.
- **Start Hearthstone Deck Tracker at the end now starts the HDT Setup found**, a portable one included. The page before **Install** shows which HDT that is.
- **Portable HDT that keeps its settings next to the program**: the plugin is now switched on in that HDT's own settings, so it is on at the first start there too.
- **The last page of Setup now says what to do next**: the toolkit starts with HDT, F3 opens the card search, and whether the plugin was switched on. Until now that page showed only the generic "Setup has finished" text.

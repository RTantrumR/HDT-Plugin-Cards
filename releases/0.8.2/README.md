v0.8.2: Setup waits for Hearthstone Deck Tracker

### Setup
- **Setup now checks for Hearthstone Deck Tracker first.** The toolkit is a plugin that runs inside HDT, so Setup no longer reports a finished install on a PC where nothing would ever load it.
- **No HDT yet?** Setup stops and links to hsdecktracker.net. Once HDT is installed, click **Check again**. If Setup still can't find it, for example a portable HDT, click **Locate HDT** and pick `HearthstoneDeckTracker.exe`. Setup goes on only once HDT is found.
- At the end, Setup starts the HDT it found and switches the plugin on, and its last page tells you what to do next.

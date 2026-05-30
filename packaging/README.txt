HSBG Card Lookup - Hearthstone Deck Tracker plugin
==================================================

INSTALL (easy - recommended)
1. Right-click this zip > Extract All (extract the whole thing to a folder).
2. Double-click install.bat. It closes HDT if open and copies the plugin into place.
3. Start HDT. If prompted, enable the plugin under Options > Plugins.
4. Press F3 (in-game or anywhere) to open the card search.

INSTALL (manual - if you'd rather not run the .bat)
1. Fully close Hearthstone Deck Tracker (also quit it from the system tray).
2. Copy the "HsbgCardLookup" folder from this zip into your HDT plugins folder:
      %APPDATA%\HearthstoneDeckTracker\Plugins\
   (Tip: paste that path into the File Explorer address bar and press Enter.)
   You should end up with:
      ...\HearthstoneDeckTracker\Plugins\HsbgCardLookup\HsbgCardLookup.dll
3. Start HDT, enable under Options > Plugins if prompted, press F3.

FIRST RUN
- On first launch the plugin downloads card art in the background (~200 MB, one time).
  Until it finishes, images fill in gradually; after that they load instantly.
- Card data and art update automatically from hsbg.cards when they change.

CONTROLS
- F3            open / close the overlay
- (type)        search; Tab toggles smart search; Esc closes
- F2            toggle the golden version of the selected minion
- click art     open that card on hsbg.cards

Requires .NET Framework 4.7.2 (already installed if HDT runs).

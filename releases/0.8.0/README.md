v0.8.0: The plugin in your language

### Cards in the game's language
- **What's new**: card names, text and art in the F3 search, the Dark Gift list, the trinket and anomaly HUD and the standings panel's hero names follow your Hearthstone client's language — German, Spanish (EU and Latin America), French, Italian, Japanese, Korean, Polish, Portuguese, Thai, Simplified and Traditional Chinese. English clients see no change.
- **Search both ways**: the smart search understands your language's words for tribes, keywords, card types, spell schools and trinket tiers, and still understands English — `Spott t2` and `taunt t2` are the same search. Accents do not matter: `damon` finds Dämon.
- **Your choice**: Settings → Card search & floating cards → **Card language**. "Automatic" follows the client; any language can be forced, English included.

### The plugin in your language
- Menus, tooltips, hints and messages in the same languages, following HDT's language setting by default; **Plugin language** on the same settings page overrides it.
- These texts are machine-translated and not reviewed by native speakers yet. Tell us what reads wrong, or fix it directly in the plugin's `data\ui\<language>.json`.

### Good to know
- Switching a language downloads nothing by itself. A card language's data (about 1 MB) is fetched the first time it is actually used — the search opens, a match starts — and its card art card by card, as you look at it. The English art pack is downloaded once, as before, and never again because of a language switch.
- Cards Blizzard did not translate stay English, as on hsbg.cards. A Russian client gets English: hsbg.cards does not serve Russian.
- Dark Gift requirement checks, match export CSVs and the plugin's log stay English: they describe game rules and data, not the interface.

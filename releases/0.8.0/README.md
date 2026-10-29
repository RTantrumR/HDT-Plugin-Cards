v0.8.0: The plugin in your language

### Your Hearthstone language, everywhere
- **What's new**: card names, text and art in the F3 search, the Dark Gift list, the trinket and anomaly HUD and the standings panel's hero names follow HDT's language setting — German, Spanish (EU and Latin America), French, Italian, Japanese, Korean, Polish, Portuguese, Thai, Simplified and Traditional Chinese. The plugin's own menus, tooltips, hints and messages follow along. English setups see no change.
- **Search both ways**: the smart search understands your language's words for tribes, keywords, card types, spell schools and trinket tiers, and still understands English — `Spott t2` and `taunt t2` are the same search. Accents do not matter: `damon` finds Dämon.
- **Your choice**: Settings → **Language**, its own row under the globe, with every language's flag and name. "Automatic" follows HDT's language; any language can be pinned, English included.
- The plugin's own texts are machine-translated and not reviewed by native speakers yet. Tell us what reads wrong, or fix it directly in the plugin's `data\ui\<language>.json`.

### Good to know
- Switching the language downloads nothing by itself. A language's card data (about 1 MB) is fetched the first time it is actually used — the search opens, a match starts — and its card art card by card, as you look at it. The English art pack is downloaded once, as before, and never again because of a language switch.
- Cards Blizzard did not translate stay English, as on hsbg.cards.
- Dark Gift requirement checks, match export CSVs and the plugin's log stay English: they describe game rules and data, not the interface.

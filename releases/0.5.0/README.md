v0.5.0: UI Redesign and MMR UX Overhaul

This release delivers the long-planned UI overhaul, providing feature-specific settings pages with live previews, alongside a major rework of the Opponent MMR features for Battlegrounds Duos support.

### UI Redesign & Previews
The settings window has been completely reworked to manage the plugin's expanding feature set. 
- **Categorized Pages**: Features are now grouped into dedicated pages (Search, HUD, MMR, Dark Gifts) with master toggles for each.
- **Live Previews**: Each settings page includes a high-fidelity preview of its respective UI element over a live Hearthstone frame, reflecting changes to scaling, opacity, and positioning instantly.
- **Visual Keys**: Added visual cues and legend keys for the various display surfaces to improve clarity on what each setting affects.
- **Settings Refinements**: The window no longer swallows all keystrokes while focused; Esc works as expected, and hotkeys only suppress while rebinding.

### MMR & Duos Support
The opponent MMR system has been significantly updated to support Battlegrounds Duos and improve layout consistency.
- **Duos Support**: The standings and portrait labels now feature teamed layouts with duo leaderboard support, including pending rating lookups for all players in the match.
- **Portrait Anchor Calibration**: Leaderboard labels now anchor precisely to each portrait's top, ensuring consistent alignment in Duos.
- **Standings Panel Rework**: The separate standings panel was rebuilt with a shared grid for perfect column alignment, including new Place and Delta columns and automatic width collapsing for hidden elements.

### HUD & Dark Gifts
- **Trinket Slot Rework**: The HUD now dynamically fills positionally (up to 4 slots) to support complex transforms and anomalies.
- **Dark Gifts Slider**: Added a "Max minions" slider (0-12) to control the size of the guaranteed-tribe pool display. The preview turn follows the slider count for better visual feedback.
- **Smart Search**: "Dark Gift" is now a valid category facet in the search overlay.

### Maintenance & Performance
- **Bandwidth Optimization**: Enabled GZip/Deflate compression for card data updates, reducing launch-time bandwidth by ~87%.
- **Stability**: Fixed various positioning bugs, including NaN guards for the preview panels and fast-requeue lifecycle resets.
- **Developer UX**: The repository is now buildable from a clean clone using the new `setup.ps1` script to pull local HDT assemblies.

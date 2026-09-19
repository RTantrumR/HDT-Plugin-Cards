# HSBG Card Lookup — Hearthstone Deck Tracker plugin

**English** | [Українська](README.ua.md)

An in-game search overlay for **Hearthstone Battlegrounds** cards. Press a hotkey, a search panel
pops over the game, type to find any card (smart filters for tier, tribe, keywords, stats), browse
art, dismiss. The desktop sibling of [hsbg.cards](https://hsbg.cards).

![Dark Gift panel — available gifts + the guaranteed-tribe minion pool over a Battlegrounds match](assets/Dark%20Gifts%20Showcase_both.png)

### Key Features
- **Fast Search**: Fuzzy + structured search (`t3`, `5/5`, tribes, keywords, spell schools).
- **Rich Card Data**: Full card art, golden variants, related cards (buddies, tokens, hero powers).
- **Floating Cards**: Drag any card out of the overlay as a free-floating, resizable card — perfect for streamers or quick reference.
- **Live HUDs**:
  - **Trinkets & Anomaly**: Real-time display of your current lesser/greater trinkets and lobby anomaly.
  - **Dark Gifts**: A "?" above the Dark Discovery button opens the list of available gifts and the guaranteed-tribe minion pool (customizable).
  - **Opponent MMR**: Standings panel and portrait labels showing opponent MMR, tavern tiers and each player's most common minion type.
- **Update Notifications**: Card data and art refresh from hsbg.cards; the plugin notifies you of new releases on GitHub for manual installation.
- **Match Recorder**: Opt-in per-match board state export to CSV.

### What's New in v0.6.0
- **Opponent Minion Types**: Each opponent's most common minion type, with its count, on both Opponent MMR surfaces from turn 1.
- **Dark Gifts on demand**: The panel opens from a "?" above the Dark Discovery button and parks in the top-right corner, instead of appearing whenever you hover the button.
- **Aberrations**: Hearthstone's new minion type, prepared ahead of the September 22 patch.

[Full release notes](https://github.com/RTantrumR/HDT-Plugin-Cards/releases/tag/v0.6.0) | [All releases](https://github.com/RTantrumR/HDT-Plugin-Cards/releases)

<p align="center">
  <img src="assets/overlay.png" width="32%" alt="Search overlay over a Battlegrounds game" />
  <img src="assets/search.png" width="32%" alt="Tier-7 browse" />
  <img src="assets/screen-trinkets_anomalies.png" width="32%" alt="Live trinkets & anomaly HUD" />
  <img src="assets/screen-cards-drag.png" width="32%" alt="Floating cards" />
  <img src="assets/filters.png" width="32%" alt="Search filters" />
  <img src="assets/MMR%20Chart%20Showcase.png" width="32%" alt="Opponent MMR history" />
</p>

> **Note**: Windows only. Requires Hearthstone Deck Tracker. Hearthstone must be in **Borderless** or **Windowed** mode.

## Installation

1. Download the latest release zip from [Releases](https://github.com/RTantrumR/HDT-Plugin-Cards/releases).
2. Extract the zip and double-click **`install.bat`** (it closes HDT and copies the plugin into place).
   *Manual install*: Drop the `HsbgCardLookup` folder into `%APPDATA%\HearthstoneDeckTracker\Plugins\`.
3. Start HDT and enable the plugin under **Options → Plugins** if prompted.
4. Press **F3** (default) to open the search overlay.

On first launch, the plugin downloads card art (~200 MB); subsequent loads are instant.

## Usage & Controls

| Key | Action |
|---|---|
| `F3` | Open / close the overlay |
| `Type` | Search (Tab toggles smart search, Esc closes, Enter opens first result) |
| `G` | Toggle golden version of the selected card |
| `S` | Re-focus the search box |

*Keys and HUD placements are customizable via the plugin's **Settings** button in HDT.*

## Requirements
- **Hearthstone Deck Tracker** (HDT)
- **.NET Framework 4.7.2** (included with HDT)
- **Hearthstone** in Borderless / Windowed mode.

## Development

### Stack
- **Language**: C# 7.3
- **Framework**: .NET Framework 4.7.2, WPF
- **Toolchain**: Visual Studio 2022 (MSBuild)
- **Host**: Hearthstone Deck Tracker

### Project Structure
- `HsbgCardLookup/`: Main project source code.
  - `Config/`: Persisted XML settings.
  - `Game/`: Game state reading and HUD logic.
  - `Ui/`: WPF overlays, floating cards, and settings UI.
  - `Net/`: API and update notification clients.
- `libs/`: The bundled WebP decoder (ImageSharp + its `System.*` closure, committed) and HDT's own
  assemblies, which are referenced at build time only and never shipped — `setup.ps1` copies those
  from your local HDT install and they are not in this repo.
- `packaging/`: Templates for distribution releases.
- `dist/`: Build artifacts (after running scripts).

### Scripts
- `.\setup.ps1`: One-time setup. Pulls required HDT assemblies (`HearthstoneDeckTracker.exe`, `HearthDb.dll`, etc.) from your local HDT install into `libs/`.
- `.\deploy.ps1`: Builds the project in Release mode, copies it to the HDT Plugins folder, and restarts HDT.
- `.\package.ps1`: Builds and creates a distribution ZIP in `dist/`.
- `.\restartHDT.ps1`: Helper to safely restart Hearthstone Deck Tracker.

### Environment Variables
- `BLIZZARD_CLIENT_ID`: (Optional) Used for internal data tools.
- `BLIZZARD_CLIENT_SECRET`: (Optional) Used for internal data tools.

### Building from Source
1. Clone the repository.
2. Run `.\setup.ps1` to link HDT assemblies.
3. Open `HsbgCardLookup.sln` in Visual Studio 2022.
4. Build using VS or run `.\deploy.ps1`.

*Note: The `dotnet` CLI cannot build this project as it uses the legacy WPF markup compiler for .NET Framework 4.7.2.*

## License

[Apache License 2.0](LICENSE). See [NOTICE](NOTICE) for third-party component details.

Inspired by [HDT-BGMMRPlugin](https://github.com/Reign-in-blood/HDT-BGMMRPlugin).

*Hearthstone is a trademark of Blizzard Entertainment, Inc. This is an unofficial fan project.*

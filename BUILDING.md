# Building Tantrum's Battlegrounds Toolkit

The project, DLL and plugin folder are named `HsbgCardLookup`, the plugin's original name.

## Stack
- **Language**: C# 7.3
- **Framework**: .NET Framework 4.7.2, WPF
- **Toolchain**: Visual Studio 2022 (MSBuild)
- **Host**: Hearthstone Deck Tracker

## Building from Source
1. Clone the repository.
2. Run `.\setup.ps1` to copy HDT's assemblies from your local HDT install.
3. Open `HsbgCardLookup.sln` in Visual Studio 2022.
4. Build using VS or run `.\deploy.ps1`.

*Note: The `dotnet` CLI cannot build this project as it uses the legacy WPF markup compiler for .NET Framework 4.7.2.*

## Scripts
- `.\setup.ps1`: One-time setup. Pulls required HDT assemblies (`HearthstoneDeckTracker.exe`, `HearthDb.dll`, etc.) from your local HDT install into `libs/`.
- `.\deploy.ps1`: Builds the project in Release mode, copies it to the HDT Plugins folder, and restarts HDT.
- `.\package.ps1`: Builds and creates a distribution ZIP in `dist/`.
- `.\restartHDT.ps1`: Helper to safely restart Hearthstone Deck Tracker.

## Project Structure
- `HsbgCardLookup/`: Main project source code.
  - `Config/`: Persisted XML settings.
  - `Game/`: Game state reading and HUD logic.
  - `Ui/`: WPF overlays, floating cards, and settings UI.
  - `Net/`: API and update notification clients.
  - `Localization/`: the two language services (`CardLanguage` for game content, `UiLanguage` for the
    plugin's own texts), the locale registry, the vocabulary table and the snapshot sync client.
  - `data/ui/<language>.json`: the plugin's UI strings; `en.json` is the key source, every other file
    is a partial overlay (a missing key renders English).
  - `data/locale-terms.json`: the game's words per language (tribes, keywords, card types, spell
    schools, trinket tiers, categories) for the search and the filter labels.

## Localization tooling
- `node tools/gen-locale-terms.js <path-to-hsbgcards-checkout>` regenerates `data/locale-terms.json`
  from the website's `data/production/terms.json` and UI catalogs. Run it after a site import that
  changes the terms table.
- `node tools/check-ui-strings.js [--strict]` holds every `data/ui/<language>.json` to `en.json`: valid
  JSON, no unknown keys, identical `{n}` placeholders; `--strict` also fails on missing keys.
- `libs/`: The bundled WebP decoder (ImageSharp + its `System.*` closure, committed) and HDT's own
  assemblies, which are referenced at build time only and never shipped — `setup.ps1` copies those
  from your local HDT install and they are not in this repo.
- `packaging/`: Templates for distribution releases.
- `dist/`: Build artifacts (after running scripts).

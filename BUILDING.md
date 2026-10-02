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
- `libs/`: The bundled WebP decoder (ImageSharp + its `System.*` closure, committed) and HDT's own
  assemblies, which are referenced at build time only and never shipped — `setup.ps1` copies those
  from your local HDT install and they are not in this repo.
- `packaging/`: Templates for distribution releases.
- `dist/`: Build artifacts (after running scripts).

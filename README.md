# HSBG Card Lookup — Hearthstone Deck Tracker plugin

In-game quick search for Hearthstone Battlegrounds cards. Press a hotkey, a search
overlay pops over the game, type, find the card, dismiss. The desktop sibling of the
[hsbg.cards](https://hsbg.cards) Twitch extension and bots.

> **Status: spike in progress.** See [`hdt-plugin-plan.md`](hdt-plugin-plan.md) for the full plan.

## Requirements

- **Hearthstone Deck Tracker** installed.
- Hearthstone running in **Borderless / Windowed (Fullscreen)** mode — exclusive
  fullscreen blocks any overlay (this is already HDT's own requirement).

## Install (users)

1. Download the release zip.
2. Drop the `HsbgCardLookup\` folder into
   `%appdata%\HearthstoneDeckTracker\Plugins` (no spaces in the folder name).
3. Restart HDT. Enable the plugin under **Options → Tracker → Plugins**.

## Development

**Toolchain:** .NET Framework 4.7.2, WPF, C#. Build with **MSBuild from Visual Studio
2022** (the `dotnet` CLI cannot run the WPF markup compiler for net472). Classic-style
`.csproj` on purpose — SDK-style `<UseWPF>` is a .NET Core feature and unreliable on net472.

### One-time setup: reference assemblies

The build references HDT's own assemblies, which are **not committed** (third-party,
large). Copy them from your local HDT install into `libs\`:

```powershell
$src = "$env:LOCALAPPDATA\HearthstoneDeckTracker\app-<version>"  # newest app-* folder
Copy-Item "$src\HearthstoneDeckTracker.exe" .\libs\
Copy-Item "$src\HearthDb.dll" .\libs\
Copy-Item "$src\Newtonsoft.Json.dll" .\libs\
```

### One-time setup: card data

The bundled card snapshot is also not committed. Copy it from the web-app repo:

```powershell
New-Item -ItemType Directory -Force .\HsbgCardLookup\data | Out-Null
Copy-Item "<web-app repo>\data\production\cards.json" .\HsbgCardLookup\data\
```

### Build & deploy

```powershell
.\deploy.ps1            # builds Release + copies the DLL into HDT's Plugins folder
```

Then restart HDT. The `deploy.ps1` MSBuild path assumes VS 2022 **Community** — edit it
for your edition.

### Verifying the spike

The skeleton logs each lifecycle call to
`%appdata%\HearthstoneDeckTracker\HsbgCardLookup\spike.log`. If you see `OnLoad`
entries after launching HDT, the plugin loaded.

## Layout

```
HsbgCardLookup.sln
HsbgCardLookup/            class library (the plugin)
  Plugin.cs               IPlugin implementation
  Properties/             AssemblyInfo
libs/                     HDT reference assemblies (gitignored — copy locally)
deploy.ps1                build + deploy to HDT Plugins folder
hdt-plugin-plan.md        the founding spec
```

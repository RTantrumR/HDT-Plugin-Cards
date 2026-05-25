# CLAUDE.md — HSBG Card Lookup (HDT plugin)

Project state for future sessions. Keep this current as the project evolves.

## What this is

A Hearthstone Deck Tracker (HDT) plugin: a hotkey-summoned overlay to search HSBG cards
in-game. Founding spec: [`hdt-plugin-plan.md`](hdt-plugin-plan.md) (read it — locked scope
decisions, architecture, search-port notes, distribution).

Sibling of the web app / Twitch extension / bots, whose repo is the **source of truth** for
card data, search logic, and images: `E:\RiderProjects\Testing\ConsoleApp1`.

## Tech / build

- **.NET Framework 4.7.2, WPF, C# 7.3.** Class library compiled to a DLL implementing
  `Hearthstone_Deck_Tracker.Plugins.IPlugin`.
- **Classic-style `.csproj`** (NOT SDK-style) — SDK `<UseWPF>` is a .NET Core feature, a
  minefield on net472.
- Build with **MSBuild from VS 2022 Community**
  (`C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`).
  `dotnet build` can't run the WPF markup compiler for net472.
- `.\deploy.ps1` builds Release + copies the DLL to
  `%appdata%\HearthstoneDeckTracker\Plugins\HsbgCardLookup\`. **Restart HDT to load**
  (plugins are scanned at startup via `PluginManager.LoadPluginsFromPath`).
- **AnyCPU** project loaded into HDT's **x86** process → MSB3270 arch-mismatch warning is
  expected and harmless.

## Verified facts (don't re-derive)

- HDT **program** install: `%LOCALAPPDATA%\HearthstoneDeckTracker\app-<ver>` (newest
  `app-1.51.15`). Reference assemblies copied to `libs\` (gitignored):
  `HearthstoneDeckTracker.exe`, `HearthDb.dll`.
- HDT **data** dir (config, Logs, Plugins): `%APPDATA%\HearthstoneDeckTracker` — **no
  spaces**. (The plan originally said "Hearthstone Deck Tracker" with spaces; that was
  wrong and cost us a debug cycle. Confirmed against this machine.)
- **`IPlugin` interface** (`Hearthstone_Deck_Tracker.Plugins`, assembly
  `HearthstoneDeckTracker`), confirmed by metadata inspection of the actual exe:
  - Props: `string Name`, `string Description`, `string ButtonText`, `string Author`,
    `System.Version Version`, `System.Windows.Controls.MenuItem MenuItem` (return null to skip).
  - Methods: `void OnLoad()`, `OnUnload()`, `OnButtonPress()`, `OnUpdate()` (~100 ms).
- `Hearthstone_Deck_Tracker.API.GameEvents` and `...Plugins.PluginManager` exist. The current
  game is reached via `Hearthstone_Deck_Tracker.Core.Game` → `GameV2` (in
  `Hearthstone_Deck_Tracker.Hearthstone`). **Live game-state / BG API options + live-verified
  findings are documented in [`hdt-game-state.md`](hdt-game-state.md).** What exists today is only a
  **read-only diagnostic probe** (`Game/GameStateProbe.cs`, driven by `OnUpdate`, logs to
  `gamestate.log`; changes no UX, never filters the pool) plus the join foundation
  (`BgCard.ExternalId` ↔ `entity.CardId`, `CardStore.ByExternalId`/`Lookup`, `HearthDb.dll`
  referenced). No product feature is built on it yet; we deliberately do NOT filter the browsable
  pool by lobby. Verified live: hero/minion/trinket joins work; golden `_G` ids need base-card
  fallback; lobby tribes are NOT in entity tags.

## Status / progress

Spike (plan §7): **all four criteria ✅** — loads, global hotkey with HS focused, Topmost
overlay over game, and real search over real data. Verified live in HDT.

**Current state — single full-browser overlay with live search.**

- **F3 → `Ui/OverlayLarge.cs`** — the overlay, **modeled on the website / twitch-extension**
  (studied under `twitch-extension/src/components/` + `components/FilterBar.tsx`,
  `components/SearchBar.tsx`). The old F2 quick panel (`Ui/OverlayMedium.cs`) was **removed** per
  user request — there's now one overlay only.

### Settings / key rebinding

**`Ui/SettingsWindow.cs`** opens from the plugin's **Settings** button (`OnButtonPress`). Rebinds
three keys — **Open overlay**, **Toggle golden**, **Focus search** — by click-then-press, plus a
**"Show Duos cards"** toggle. `PluginConfig` holds `BrowserKey` (F3 — name kept for config
back-compat; it's the lone summon key now), `GoldenKey` (G), `FocusKey` (S), `ShowDuos` (bool,
default true), persisted as `config.xml`. Changes apply live: `Plugin.ApplySettings` saves +
`RewireHotkeys()` (re-registers the hook's key; an **unbound** key = `Key.None` is simply not
registered) + `_overlayLarge.RefreshPool()` (picks up a Duos change). Golden/focus are read live
from `_config` by `OverlayLarge`.

- **Capture-mode swallow** (`HotkeyManager.BeginCapture/EndCapture` + `KeyCaptured` event): while
  the settings window is **active**, the low-level hook swallows EVERY key (returns 1) and routes it
  to the dialog — so pressing F2/F3 there doesn't summon the overlay. `SettingsWindow` toggles
  capture on `Activated`/`Deactivated`. (Normal mode never swallows — hotkeys still reach the game.)
- **Steal-on-collision**: binding a key already used by another action **takes** it and sets the
  previous owner to **unbound** (`"None"`, shown as `—`), with a top-of-dialog notice. (Earlier
  behavior rejected collisions.)
- Fixed-width (118px) binding boxes; Esc cancels a capture / closes the dialog; Alt + bare
  modifiers can't be bound.

Shared infra (keep): `Ui/OverlayBase.cs` (chrome, Esc/focus-loss dismiss with a **popup guard** so
dropdowns don't self-dismiss, Toggle, **`ForceForeground`** — see below), `Ui/UiKit.cs` (palette +
widgets + `SetCardText` HTML parser + tier-icon + lightbulb + thin-scrollbar style + **`ClearButton`
"✕"**), `Ui/ImageCache.cs` (decoded-bitmap cache), `Ui/Dropdown.cs` (compact single-select dropdown
w/ Popup). `UiKit.CreateResultsList`/`ResultRow` are now **dead code** (only F2 used them) — left in
place, harmless.

### Reliable search focus across processes (`OverlayBase.ForceForeground`)

Summoning over another app (you alt-tabbed to a browser/IDE, then pressed F3) used to leave the
overlay visible but **unfocused** — typing went nowhere — because Windows blocks a background
process from stealing the foreground (`SetForegroundWindow` lock). Fix: on Toggle→Show we call
`ForceForeground()`, which briefly **`AttachThreadInput`**s our thread to the current foreground
thread, lifting the lock so `SetForegroundWindow` + activation succeed; the existing
`Activated → FocusSearch` then lands. (Consecutive opens always worked — HDT was already foreground.)

### Website deep-link (detail portrait only)

Clicking the **detail pane's main card art** opens the card on the site:
`https://hsbg.cards/card/{slug}?utm_source=hdt&utm_medium=plugin&utm_campaign=clickDetail&utm_content={slug}`
(`OverlayLarge.OpenOnWebsite`, `Process.Start`, slug URL-escaped). `utm_content={slug}` gives
per-card click attribution. **Grid cells and related-card thumbs do NOT link** (grid = select,
related = navigate). UTM scheme + the `/card/{slug}` route confirmed from the web-app repo's
`utm-links.md`.

### Clear buttons

`UiKit.ClearButton(onClear,…)` builds a small "✕" that swallows its own click (won't bubble to a
parent). Used in two places: in the **search box** just left of the lamp (visible only when there's
text; clears + refocuses), and on each **filter dropdown** when a value is set (left of the chevron;
clears that filter — clearing Type also cascades to Tribe/School via the same change handler).

UI behaviors:
- **Conditional stats** (`UiKit.StatsText`): A/H only for minions, "HP" for heroes, nothing else.
- **Card text is HTML-rendered** (`UiKit.SetCardText`): `<b>`→bold, `<i>`→italic, `\n`→break,
  other tags stripped.
- **Real card-art thumbnails** (cached, `UniformToFill`) with **tier glow**; **tier shown as the
  icon** (`public/tiers/Tier{n}.png`), not a number, in results + detail + tier dropdown.
- **Lightbulb smart toggle** inside the search box (filled+glow when on); **Tab** also toggles.
- **F3 filters = compact horizontal dropdowns** (replaced the tall rail): Type; Tier (icons);
  **Tribe only when Type=Minion**; **Spell-school only when Type=Spell**. Single-select, derived
  from data (`CardStore.Types/Tribes/Tiers/SpellSchools`). Filters narrow the pool, then search
  runs over it (blank query + filters = browse). "Neutral" = minions with no tribe.
- **Card art** rests directly on the panel (no container box), sized to aspect (MaxWidth 300).
- **Golden**: toggle button (minions with a real golden diff only) swapping
  `attackGold/healthGold/textGold/imageGold`; **`G` hotkey** (`PluginConfig.GoldenKey`, default G)
  toggles it **when the search box isn't focused** (so typing 'g' in queries still works).
- Bigger fonts throughout; thin 9px scrollbar (`UiKit.ThinScrollBarStyle`, applied as an implicit
  `ScrollBar` style in the window's resources); wider gap between list and detail.

### Performance (critical — results list is virtualized)

The lag spike on search/filter was synchronous decoding of ~150 full card PNGs per change.
Fixed by **`UiKit.CreateResultsList`**: a virtualized `ListBox` (CanContentScroll + recycling)
whose item visuals are an FEF `DataTemplate`; thumbnails/tier-icons load lazily via
`Ui/ResultConverters.cs` (`ThumbConverter`/`TierIconConverter`/`SubtitleConverter`) so only the
~10 visible rows decode at once. Thumbnails decode at display size (~76px), not 160. The
per-row drop-shadow glow was removed. Search input is **debounced ~140ms**. (The F3 grid uses the
same lazy-decode approach via `CreateCardGrid`; `CreateResultsList` itself is now unused.)

### F3 results = virtualized 3-column card grid

`UiKit.CreateCardGrid(columns, decodeWidth, onSelect)`: results are chunked into rows of 3 and a
vertical `VirtualizingStackPanel` virtualizes the rows (only visible rows decode art). Cells are
flat `Button`s (DataContext bound to `[i]` of the row); art **fills the column** (Stretch) with the
name underneath (15px); tight margins. Search+filter dropdowns share the top row (title removed).

- **Default/browse order matches the website** (`CardStore.TypeRank`): by card type — minion, spell,
  hero, hero_power, quest, reward, trinket, anomaly — then tier, then name. (Not alphabetical: that
  interleaved mismatched-aspect frames like heroes next to trinkets.) Search results keep their
  engine sort (smart = tier→name; simple = relevance).

- **Grid cells are `Focusable=false`** (and the ListBoxItem too) — otherwise clicking a card in a
  lower row grabs focus, the ScrollViewer scrolls it into view, and the card moves out from under
  the cursor before mouse-up so the click never lands. Non-focusable = static grid + reliable open.
- **Pixel scrolling** (`VirtualizingPanel.ScrollUnit=Pixel` with virtualization on) → a normal,
  proportional scrollbar (not the tiny item-based thumb). Scrollbar template is 7px wide, thumb
  `MinHeight=28`.
- **Search auto-focuses on open** (`Activated` → dispatcher-deferred `Focus()`+`SelectAll()`), so the
  user can hit the hotkey and immediately type a query — no extra click.

### Card art / images (PNG for now; WebP deferred)

Native PNG masters are **512×673**. Grid decodes at **256px** (crisp downscale); the detail preview
decodes at **full native** (`ImageCache.Load(path, 0)`). **WebP is deliberately deferred** to the
distribution phase (plan §5): WPF can't decode WebP without a bundled decoder, and it would NOT help
runtime perf — a decoded bitmap is `w*h*4` bytes regardless of source format, and virtualization
already means only ~12 images decode at once. WebP's real win is **bundle/download size** (ship a few
MB of small WebP instead of ~480MB of PNG), so we'll add a decoder (libwebp or SkiaSharp) when we
build the shippable bundle, not before.

### Detail pane (minimal, per user)

F3 detail = **card art only** (rests on the panel, no box, sized to aspect) + **golden toggle**
(minions with a golden diff; `attackGold/healthGold/textGold/imageGold`; `G` hotkey when search
unfocused) + **related cards** (`CardStore.RelatedCards` → buddy/token/hero-power/source,
clickable to navigate). No tier/meta/text — the art already shows them.

- **Transparent-edge trimming** (`ImageCache.LoadTrimmed`): hero/spell PNGs (e.g. heroes are
  404×558 with ~72px transparent padding top & bottom) otherwise show big empty gaps around the
  card. The detail art, related thumbs, AND grid thumbnails (`ThumbConverter`) are trimmed to their
  opaque bounds (computed once via `CopyPixels`, cached). Minions are full-frame → unchanged. This
  keeps the detail compact (avoids an unnecessary scrollbar) and makes hero/spell frames fill their
  grid cells instead of floating in transparent padding.
- **Empty grid cells collapse**: a partial last row (e.g. 2 results in a 3-wide row) used to render
  empty-but-clickable cells. Each cell's `Visibility` binds to its row slot (`[i]`) with
  `NullToCollapsedConverter` + `FallbackValue=Collapsed`, so empty slots vanish.

### Window dragging

`OverlayBase.SetRoot` overlays a transparent 18px strip along the very top that calls
`DragMove()` — repositions the borderless overlay. Position persists across toggles.

### Search engine (ported)

Faithful C# port of the **website's** `lib/search` (richer than the bot/shared modules — has
relevance ranking + `categories`/`Neutral`), in `Search/`:
- `Levenshtein.cs`, `Aliases.cs` (UA/EN table), `QueryParser.cs` (`ParsedQuery`),
  `SearchEngine.cs` (`Smart` = `applySmartSearch`, structured; `Simple` = `applySimpleSearch`,
  name/text + fuzzy, relevance-ranked). `BgCard.cs` mirrors the web-app model.
- Smart results sorted tier-asc then name (plan §6); Simple sorted by relevance.
- Excludes `versionOf`-set + `developer` cards. Parity point: re-sync if shared logic changes.
- **Intentional divergence from the website**: a card can't be two types, so the parser keeps the
  **first card-type by query position** as the filter and demotes any additional card-type tokens
  to the name/text query. So `"trinket hero power"` → `cardType=trinket` + text `"hero power"`
  (→ trinkets that mention hero powers), instead of `trinket AND hero_power` → 0 (the website's
  behavior). See `cardTypeHits` resolution in `QueryParser.Parse`. Verified: 2 results.

### Data

`Data/CardStore.cs` loads the bundled `HsbgCardLookup/data/cards.json` (next to the DLL;
gitignored — copied from the web-app `data/production/cards.json`). Pools: `Current`
(pool=true), `Legacy`. `deploy.ps1` ships the `data\` folder alongside the DLL.
**Duos filter:** when `PluginConfig.ShowDuos` is false, `OverlayLarge.PassesFilters` drops
`IsDuosOnly` cards (60 of the 1148-card current pool). Default = shown.
**Portraits:** `CardStore.ResolveImagePath` reads PNGs straight from the web-app
`public/cards/production/pngs/full/` (a dev-time absolute path — distribution must bundle/fetch
art instead, plan §5). The bundled WebP thumbs are NOT used (WPF can't decode WebP).

### Deps / build notes

- **Newtonsoft.Json** referenced from `libs\` (HDT's own copy, `Private=False`, loaded
  in-process — no redistribution/version clash).
- **`<CodePage>65001</CodePage>`** in the csproj so `csc` reads the Cyrillic alias literals as
  UTF-8 (verified: `звір` → Beast tribe matched 71 cards).
- HDT **auto-updated to `app-1.52.9`** mid-dev and **shadow-copies** the whole plugin folder
  (DLL + `data\`) into its app dir to load — the bundle travels correctly.

Lifecycle + hotkey presses log to
`%appdata%\HearthstoneDeckTracker\HsbgCardLookup\spike.log`.

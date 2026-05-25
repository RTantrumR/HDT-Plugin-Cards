# HSBG Card Lookup — Hearthstone Deck Tracker Plugin (Plan)

> **Status:** Planning / pre-spike. This document is meant to be **copied into a new, separate project folder** and used as the founding spec. It is self-contained, but points back to the web-app repo (see [Source of truth](#source-of-truth)) for API contracts, card data shapes, and search logic to port.

---

## 1. What this is

A **Hearthstone Deck Tracker (HDT) plugin** that lets a player look up any Hearthstone Battlegrounds card *while in the game*, fast. Press a configurable hotkey (e.g. F1) → a search overlay pops over the game → type → find the card → dismiss.

It is the desktop, in-game sibling of the existing **Twitch extension** and **Telegram/Discord bots** — same card data, same search grammar, different delivery surface. Unlike the Twitch extension (which streamers add to a channel), an HDT plugin is **downloaded and installed individually by each player**, which is explicitly allowed by HDT.

### Locked scope decisions

| Decision | Choice | Why it matters |
|---|---|---|
| **Integration depth** | **Standalone first, game-state later.** v1 is a pure card browser with no game-state coupling; v2 *may* read `Hearthstone.Game` via `OnUpdate` to surface contextually relevant cards. | v1 ships fast and de-risked; the `OnUpdate` seam means v2 is an addition, not a rewrite. |
| **Data source** | **Bundled snapshot + API refresh.** Ship a local card snapshot (JSON + small thumbnails) so search is instant and works offline mid-game; refresh from the public API when online. | In-game lookups can't depend on a network round-trip or the server being up. |
| **Feature breadth** | **Deliberately open.** Build the hotkey→overlay→search spine first, *then* look at it and decide lean vs. big. | The headline UX (hotkey-summoned quick search) defines the plugin; feature count is secondary and best judged from a running prototype. |
| **First step** | **Thin vertical spike** (see [§7](#7-the-spike-build-this-first)). | Proves the genuinely uncertain parts (hotkey capture in-game, overlay-over-game, plugin loading) before investing in features. |

---

## 2. Source of truth

This plugin **does not reinvent card data, search, or images** — it reuses what the web app already exposes. All of the following live in the web-app repo:

```
E:\RiderProjects\Testing\ConsoleApp1
```

When building the plugin in its new folder, treat that path as the authoritative reference for:

| What you need | Where it is in the web-app repo |
|---|---|
| **Public API full reference** (endpoints, params, examples) | `docs/public-api.md` |
| **Public API base URL** | `https://hsbg.cards/api/v1/` |
| **OpenAPI 3.0 spec** (feed to a C# client generator) | `https://hsbg.cards/api/v1/openapi.json` — or source at `lib/api/openapi-spec.ts` |
| **Card data (for the bundled snapshot)** | `data/production/*.json` — `cards.json` is the full set; also split per-type (`minions.json`, `heroes.json`, `spells.json`, …) |
| **Card images (for the bundled thumbnails)** | `public/cards/production/thumbs/{small,medium,full}/` (WebP) and `pngs/full/` (PNG masters) |
| **Search + filter logic to port to C#** | `extensions/shared/src/search/search.ts`, `query-parser.ts`, `aliases.ts`, `levenshtein.ts` |
| **Card model shape** | `extensions/shared/src/data/types.ts` (the `BgCard` interface) |
| **Feature/UX reference (closest sibling)** | `twitch-extension/` — search, type/tier filters, current/legacy toggle, golden toggle, card detail |
| **Project conventions / API design notes** | `CLAUDE.md`, `docs/architecture.md`, `docs/data-model.md` |

### The `BgCard` model (current shape — verify against `types.ts` when porting)

```ts
interface BgCard {
  id: number;
  slug: string;
  name: string;
  text: string;
  image: string;
  attack?: number;
  health?: number;
  manaCost?: number;
  armor?: number;
  tier?: number;
  cardType: string;          // minion | hero | spell | quest | reward | anomaly | trinket | hero_power | location | weapon | other | unknown
  minionTypes: string[];     // tribes
  keywords: string[];
  spellSchool?: string;
  categories?: string[];
  isHero: boolean;
  isDuosOnly: boolean;
  isSolosOnly: boolean;
  companionId?: number;
  childIds: number[];
  parentId?: number;
  pool: boolean;             // true = current pool; false = legacy
  versionOf?: number;        // set on alternate versions; excluded from search
  trinketTier?: string;
}
```

---

## 3. Technical constraints (HDT plugin facts — verified)

- **Runtime:** .NET **Framework 4.7.2**, **WPF**. Windows-only. (Not .NET 8 — match HDT's target.)
- **Artifact:** a C# **class library compiled to a `.dll`**, implementing `Plugins.IPlugin` (public class).
- **`IPlugin` surface:**
  - Properties: `Name`, `Description`, `ButtonText`, `Author`, `Version`, `MenuItem` (a `System.Windows.Controls.MenuItem` added to HDT's Plugins menu — return `null` to skip).
  - Methods: `OnLoad()`, `OnUnload()`, `OnButtonPress()` (fires when the user clicks the plugin's button in Options → Tracker → Plugins), `OnUpdate()` (called roughly every **~100 ms** — the future hook for game-state reactivity).
- **Install location:** `%appdata%\HearthstoneDeckTracker\Plugins` (**no spaces** — verified on a real install; an earlier draft of this doc said "Hearthstone Deck Tracker" with spaces, which is wrong). Subfolders are supported, so we ship `Plugins\HsbgCardLookup\` containing the DLL **plus** a data folder.
- **Available APIs inside HDT:** `API.GameEvents` (draw/play/game events), `API.DeckManagerEvents`, and the `Hearthstone.Game` class (full game state — card locations, health, etc.). This is what v2 would consume.
- **Hotkey precedent:** [`kosorin/hearthstone-hotkeys`](https://github.com/kosorin/hearthstone-hotkeys) is an existing HDT plugin that registers global hotkeys — proof the headline interaction is achievable.
- **Overlay precedent:** the official example plugin draws "the names of the cards in the player's hand in the center of the overlay" — proof a plugin can render UI over the game.

### Known technical risks / gotchas to resolve

1. **Global hotkey while the game is focused.** A normal WPF window won't receive a keypress when Hearthstone has focus. Need a **system-wide hotkey** — either Win32 `RegisterHotKey`, a low-level `WH_KEYBOARD_LL` hook, or whatever mechanism `hearthstone-hotkeys` uses (read its source first). The spike must prove this works *with HS in the foreground*.
2. **Overlay rendering over the game.** Requires Hearthstone in **Borderless / Windowed (Fullscreen)** mode — exclusive fullscreen blocks overlays. This is already HDT's standing requirement for its own overlay, so most users are set up correctly, but it must be stated in the README. The popup window should be `Topmost`.
3. **⚠️ WPF can't decode WebP natively.** The bundled thumbnails are WebP (that's what `public/cards/production/thumbs/` ships). WPF's `BitmapImage` won't read them. Options, in order of preference:
   - **Bundle PNG instead of WebP** for the offline set (simplest; larger on disk — `pngs/full/` masters are ~420 KB each, so prefer downscaled PNGs generated by the export script).
   - **Use a decoder library** — `SkiaSharp` (decodes WebP → `SKBitmap` → WPF `WriteableBitmap`/`BitmapSource`) or `Magick.NET`. Adds a dependency but keeps the small WebP file sizes.
   - Decide this during the spike. Recommendation: bundle a **small downscaled PNG set** for the offline list, fetch larger art on demand from the API and cache to disk.
4. **API key for autocomplete.** The fast `GET /api/v1/cards/suggest` endpoint is **the only key-gated endpoint**. v1 should do search **client-side over the bundled snapshot** (no key needed, works offline). Only consider `suggest` if we ever want server-ranked results — and then the key would have to ship with the plugin, which is leaky. **Plan: client-side search, no API key.**

---

## 4. Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ Hearthstone Deck Tracker (host process, .NET 4.7.2 WPF)      │
│                                                              │
│  ┌────────────────────────────────────────────────────┐    │
│  │ HsbgCardLookup plugin (IPlugin)                       │   │
│  │                                                       │   │
│  │  OnLoad:  load bundled snapshot → in-memory index     │   │
│  │           register global hotkey                      │   │
│  │           kick off background API refresh             │   │
│  │  Hotkey:  show/hide the search overlay window         │   │
│  │  OnUpdate (~100ms): [v2] read Hearthstone.Game        │   │
│  │  OnUnload: unregister hotkey, dispose window          │   │
│  │                                                       │   │
│  │  ┌─────────────┐  ┌──────────────┐  ┌─────────────┐  │   │
│  │  │ CardStore   │  │ SearchEngine │  │ Overlay UI  │  │   │
│  │  │ (snapshot + │  │ (port of TS  │  │ (WPF window,│  │   │
│  │  │  API cache) │  │  search)     │  │  Topmost)   │  │   │
│  │  └─────────────┘  └──────────────┘  └─────────────┘  │   │
│  └────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
            │ (online, background only)
            ▼
   hsbg.cards/api/v1  ── card data + images (no key needed)
```

- **CardStore** — loads the bundled `cards.json` on startup (instant, offline). In the background, when online, refreshes from `GET /api/v1/cards` (paginated) and caches updated data + downloaded images to a local cache dir under `%appdata%`. Falls back to the bundle on any failure.
- **SearchEngine** — C# port of the shared search (name search: exact → starts-with → substring → fuzzy) and the structured filter parser (stats, tier, keywords, tribes, with UA/EN aliases). See [§6](#6-search-port).
- **Overlay UI** — a `Topmost` WPF window summoned by the hotkey: a search box + results list (small thumbnail + name + tier/type) + a detail pane (full art, stats, text, golden toggle). Dismiss on Esc / hotkey / focus loss.

---

## 5. Data strategy — the bundle + refresh

**Bundle (ships with the plugin, in `Plugins\HsbgCardLookup\data\`):**
- `cards.json` — exported from `data/production/cards.json` (or a slimmed projection of the fields the plugin uses).
- `thumbs/` — a **small PNG** set (one per card) for instant list rendering offline. Generated by the export script (downscale from `pngs/full/`, or convert from `thumbs/small/` WebP).

**Export script (lives in the web-app repo, NOT the plugin repo):**
A small script under `scripts/` in `E:\RiderProjects\Testing\ConsoleApp1` that emits the bundle: reads `data/production/`, writes the slimmed `cards.json` + the downscaled PNG set into a `dist/` the plugin build can pick up. Keep the plugin a pure consumer; the web-app repo owns data generation. *(To be written when we start — flagged here so it isn't forgotten.)*

**Refresh (runtime, online only):**
- Background pull from `GET /api/v1/cards?pool=all&limit=200&offset=…` (paginated; response carries `total` + `hasMore`).
- Card art on demand: `GET /api/v1/cards/{id}/image?size=medium` (302 → static file). Cache to disk keyed by id+size. PNG only available at `size=full`.
- Respect rate limits (anonymous 120/min per IP) and cache aggressively — card data rarely changes. A daily refresh is plenty.

---

## 6. Search port

Port these TS modules to C# (logic is small and language-agnostic):

- `extensions/shared/src/search/search.ts` — `searchCards()` ranking (exact → starts-with → substring → fuzzy with Levenshtein threshold `max(1, len/3)`, fuzzy only for queries ≥ 4 chars) and `filterCards()` (stats / tier / keywords / textKeywords / tribes / cardTypes / name query). **Note** the keyword filter unions `keywords[] ∪ name ∪ text`, and the name query requires every word to appear in name OR text (AND across words) — mirror this exactly.
- `extensions/shared/src/search/query-parser.ts` — turns a raw query string into the structured `ParsedQuery` (stats like `5/5`, tier, tribes, keywords).
- `extensions/shared/src/search/aliases.ts` — UA/EN alias table for tribes/keywords/types.
- `extensions/shared/src/search/levenshtein.ts` — the distance function.

Excluded from search in both functions: cards with `versionOf` set and `cardType === "developer"`. Sort: tier asc, then name.

> Keep the C# port a **faithful translation**, not a redesign — the website, both bots, and this plugin should return the same results for the same query. If the shared logic changes, this is a parity point to re-sync.

---

## 7. The spike (build this first)

Goal: prove every uncertain thing end-to-end in 1–2 days, with throwaway-quality UI. **Success criteria:**

1. **Plugin loads.** A minimal `IPlugin` implementation appears in HDT's Options → Tracker → Plugins list, loads and unloads without crashing HDT. → *verify: visible in the list; HDT stable.*
2. **Hotkey captured in-game.** Pressing the chosen key **while Hearthstone is the focused/foreground window** triggers the plugin. → *verify: a debug log line or message box fires with HS in front.*
3. **Overlay renders over the game.** A `Topmost` WPF window appears on top of HS (Borderless/Windowed) and dismisses cleanly. → *verify: visible over the game; Esc/hotkey hides it.*
4. **Search works against real data.** The overlay searches a small **hardcoded** set of cards (10–20) and renders name + thumbnail. → *verify: typing filters the list.*

Once 1–4 pass, *stop and look at it* — then decide v1 feature breadth (lean vs. big) and proceed to the full build.

---

## 8. v1 build slices (after the spike)

1. **Plugin skeleton** — full `IPlugin` (Name/Description/Author/Version/MenuItem), clean load/unload, config (hotkey choice) persisted under `%appdata%`.
2. **Bundled snapshot** — export script in the web-app repo + CardStore loading `cards.json` and PNG thumbs at startup.
3. **Search overlay** — the real overlay window: search box + results list + detail pane (full art, stats appropriate to card type, text, golden toggle), driven by the ported SearchEngine. Filters (type/tier/tribe, current/legacy) modeled on `twitch-extension/`.
4. **Background API refresh** — CardStore pulls latest from `/api/v1` when online, caches art on demand, falls back to bundle.
5. **Polish + README** — install instructions, the Borderless-mode requirement, hotkey config, screenshots.

## 9. v2 (later, optional) — game-state awareness

Inside `OnUpdate` (~100 ms), read `Core.Game` (`GameV2`) to know the current BG context (tavern tier, tribes in the lobby, minions on board) and surface relevant cards. Built on top of v1; no rewrite. **What HDT actually exposes (entry points, BG specifics, the `externalId` join key, open questions) is documented in [`hdt-game-state.md`](hdt-game-state.md).** Note: per a later scope decision we do NOT plan to *filter/limit* the pool by lobby — every card stays browsable for synergy/route planning; awareness would *surface/annotate*, not hide.

---

## 10. Distribution

- Ship as a **zip** containing the `HsbgCardLookup\` folder (DLL + `data\`), preserving structure. Users drop it into `%appdata%\HearthstoneDeckTracker\Plugins` (no spaces) and restart HDT.
- No store review (unlike Twitch). Optional: submit to HDT's community **"Available Plugins"** wiki page for discoverability.
- Promote via the same channels as the rest of the project (see the web-app repo's UTM links reference).

---

## 11. Open questions to resolve before/with the spike

- [ ] Confirm the user runs **HDT with Hearthstone in Borderless/Windowed mode** (overlay depends on it). If HDT's own overlay already works for them, they're set.
- [ ] Read `kosorin/hearthstone-hotkeys` source to pick the hotkey mechanism (`RegisterHotKey` vs low-level hook).
- [ ] Decide image format for the bundle: downscaled **PNG** (simplest) vs **WebP + SkiaSharp decoder** (smaller files, extra dep). Lean PNG.
- [ ] Pick the new project/repo name and location (separate from the web-app repo).
- [ ] Confirm exact HDT assembly references needed (`HearthstoneDeckTracker.exe` / `HearthDb`) and where to reference them from a local HDT install for the build.

---

## 12. Suggested layout for the NEW project folder

```
HsbgCardLookup/                     (new repo, separate from the web app)
├─ HsbgCardLookup.sln
├─ HsbgCardLookup/                  (the plugin class library, .NET 4.7.2)
│  ├─ Plugin.cs                     (IPlugin implementation)
│  ├─ Hotkey/                       (global hotkey registration)
│  ├─ Search/                       (C# port of the shared search)
│  ├─ Data/                         (CardStore: snapshot load + API refresh + cache)
│  ├─ Ui/                           (WPF overlay window + detail pane)
│  └─ data/                         (bundled cards.json + thumbs/ — produced by the web-app export script)
├─ README.md                        (install, Borderless requirement, hotkey config)
└─ hdt-plugin-plan.md               (this document)
```

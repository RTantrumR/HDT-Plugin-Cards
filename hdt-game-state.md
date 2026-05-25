# HDT live game-state — what a plugin can read (research)

**Status: research only. Nothing in the plugin reads game state yet, and we deliberately do NOT
filter the browsable pool by lobby** — the full card list always stays visible (players already have
HDT / the in-game library for "what's available"; our value is showing *every* card for synergies and
route planning). This doc records what HDT *can* expose so future "awareness" features (synergy hints,
route suggestions, a parked "reachable cards by chain reaction" idea) can be designed without
re-deriving the API.

**Provenance.** Member names below were dumped from our **linked reference assemblies** with
`ilspycmd` — `HearthstoneDeckTracker.exe` **v1.51.15.7284** and `HearthDb.dll` **v35.2.2** (the
versions we compile against; authoritative for us), cross-checked against the public HDT source
(github.com/HearthSim/Hearthstone-Deck-Tracker). Every item is tagged:

- **[C]** Confirmed from local assembly metadata (name exists in our version).
- **[V]** Needs **live verification** — we can't confirm the *value* reads correctly without being in
  a real Battlegrounds match. (The plugin can't self-test this; the user has to play a game.)

> Rule: reference HDT/HearthDb **enum names**, never hardcoded tag ints — the numeric values below are
> for orientation only and can drift between HearthDb versions.

---

## Live-verified findings (real solo match, 2026-05-25)

Captured by a throttled read-only probe (`HsbgCardLookup/Game/GameStateProbe.cs`, driven from
`IPlugin.OnUpdate`, logs to `gamestate.log`) during an actual match (bought/played a minion + spell,
tiered up, rerolled, used hero power, made a triple, auto-picked a trinket). Results:

- **The `externalId` ↔ `entity.CardId` join works in practice.** Hits on the chosen hero
  (`BG31_HERO_005` → *Zerek, Master Cloner*), all normal minions (e.g. `BG35_801` *Gluttonous Trogg*,
  `BG_GVG_085` *Annoy-o-Tron* [Mech], `BG25_806` *Sly Raptor* [Beast]), **and the trinket**
  (`BG35_MagicItem_931` → *Transcribing Typewriter*, read via `Player.Trinkets`). **CONFIRMED.**
- **One systematic miss — golden minions.** Tripled/golden entities carry a **`_G` suffix**
  (`BG35_801_G`) that our data has no record for (we model golden as `attackGold/healthGold/imageGold`
  on the *base* card). **Fix for any future feature: strip a trailing `_G` from `CardId`, look up the
  base, then use the gold stats.** The pre-pick placeholder hero `TB_BaconShop_HERO_PH` also misses
  (expected — it's not a real card).
- **Lobby tribes are NOT exposed as entity tags — route (a) is DISPROVEN for our version.** Across
  60+ snapshots the player/game entities carried **no** `BACON_SUBSET_*` / `RACE` / tribe tag (the
  probe explicitly watched `BACON`/`RACE`/`SUBSET`/`POOL`). If we ever want lobby tribes, the only
  routes are HDT-internal `BattlegroundsDb.GetRaces()` or inferring from shop offerings — **not** game
  tags. Likewise **no triple-count tag** surfaced despite a triple being made, and **no anomaly tag**
  (this game had none).
- **Tavern tier reads cleanly:** `PLAYER_TECH_LEVEL` progressed `1 → 2 → 3`; `BACON_MAX_PLAYER_TECH_LEVEL=6`
  is the cap. **CONFIRMED.**
- **Tags that DO exist on player/game entities** (orientation): `PLAYER_TECH_LEVEL`,
  `BACON_MAX_PLAYER_TECH_LEVEL`, `BACON_TRINKETS_ACTIVE`, `BACON_BARTENDER_CARD_ID`,
  `BACON_COMBAT_DAMAGE_CAP(_ENABLED)`, `BACON_NUM_FREE_REROLLS_USED`, `BACON_PREMIUM_FREE_REROLLS`,
  `NUM_MINIONS_PLAYED_THIS_TURN`, `HEROPOWER_ACTIVATIONS_THIS_TURN`, `NUM_TURNS_LEFT`, `TURN`, etc.
- **Probe caveat:** the one-time *full* tag dump fired at match-entry while `PlayerEntity`/`GameEntity`
  were still null (logged `(none)`); the per-change *curated* dump still covered the relevant keywords.
  A future full dump *after* entities load would be the final word on "no tribe tag", but coverage here
  is already strong.

---

## Entry point — the current game

`Hearthstone_Deck_Tracker.Core.Game` (static) → returns `GameV2`
(namespace `Hearthstone_Deck_Tracker.Hearthstone`). **[C]**

Useful members on `GameV2`:

| Member | Notes | Tag |
|---|---|---|
| `IsBattlegroundsMatch` | true for Solo **or** Duos | [C] |
| `IsBattlegroundsSoloMatch` / `IsBattlegroundsDuosMatch` | mode split | [C] |
| `IsBattlegroundsCombatPhase` | recruit vs. combat | [C] |
| `CurrentGameType` | `GameType` enum | [C] |
| `GetTurnNumber()` | int; reads `GameEntity` `TURN` tag | [C] / [V] value |
| `Entities` | `Dictionary<int, Entity>` — every entity in the game | [C] |
| `Player` / `Opponent` | `Player` model (below) | [C] |
| `PlayerEntity` / `OpponentEntity` / `GameEntity` | the bare entities (carry player/game-level tags) | [C] |
| `CurrentGameStats` | has `BattlegroundsLobbyDetails` (below) | [C] |
| `GetBattlegroundsBoardStateFor(int entityId)` → `BoardSnapshot?` | opponent board history | [C] / [V] |
| `SnapshotBattlegroundsBoardState()` | capture current board | [C] |

---

## Player model — `Hearthstone_Deck_Tracker.Hearthstone.Player`

| Member | Notes | Tag |
|---|---|---|
| `Minions` | `IEnumerable<Entity>` — board minions (`Board.Where(IsMinion)`) | [C] / [V] |
| `Hero` | `Entity?` — the player's hero | [C] / [V] |
| `Trinkets` | `IEnumerable<Entity>` — BG trinkets (`Board.Where(IsBattlegroundsTrinket)`) | [C] / [V] |
| `Board` | everything in the PLAY zone (minions + hero) | [C] / [V] |
| `Hand` / `Deck` | hand / deck entities | [C] |
| `Id` | player id (1/2) | [C] |

> **Discrepancy worth knowing:** the public HDT wiki claims trinkets aren't exposed to plugins. Our
> linked assembly **does** expose `Player.Trinkets` and `Entity.IsBattlegroundsTrinket` — and a live
> match **confirmed** it populates (read *Transcribing Typewriter* via `Player.Trinkets`). Trust the
> local metadata over the wiki.

---

## Entity model — `Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity`

| Member | Notes | Tag |
|---|---|---|
| `CardId` | string, e.g. `"TB_BaconShop_HERO_16"` — **the join key** (see below) | [C] |
| `Card` | `HearthDb.Card` resolved from `CardId` (lazy) | [C] |
| `GetTag(GameTag)` / `HasTag(GameTag)` | raw tag access | [C] |
| `IsMinion` / `IsHero` / `IsBattlegroundsTrinket` | type helpers | [C] |
| `IsInZone(Zone)` / `IsInPlay` / `IsInHand` | zone helpers | [C] |
| `Attack` / `Health` / `Cost` / `ZonePosition` | current stats | [C] / [V] |
| `IsControlledBy(int)` / `IsPlayer` / `IsOpponent` | ownership | [C] |

---

## Battlegrounds specifics

- **Tavern / tech level [C] name, [V] value:** `PlayerEntity.GetTag(GameTag.PLAYER_TECH_LEVEL)`
  (≈1377). Int 1–7.
- **Anomaly [C]/[V]:** `GameTag.BACON_GLOBAL_ANOMALY_DBID` on the game entity, **or**
  `Core.Game.CurrentGameStats?.BattlegroundsLobbyDetails?.AnomalyDbfId`.
- **Lobby details [C]/[V]:** `Core.Game.CurrentGameStats?.BattlegroundsLobbyDetails`
  (`Hearthstone_Deck_Tracker.Stats`): `LobbyRawHeroDbfIds`, `FriendlyRawHeroDbfId`,
  `FriendlyPlayerEntityId`, `AnomalyDbfId`, `FinalPlacement`.
- **Opponent board:** only the **last-known** snapshot — not live during combat
  (`GetBattlegroundsBoardStateFor` / `BoardSnapshot { Entities, Turn }`). [C]/[V]
- **Tribes available in this lobby — the soft spot. Live result: NOT in entity tags.**
  1. ~~`BACON_SUBSET_*` GameTags on the player/game entity~~ — **DISPROVEN** in a live match (no such
     tags appeared on player/game entities). Do not pursue this route.
  2. HDT-internal `Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsDb.GetRaces()` (computes the
     lobby's race set; also `GetCards(tier, race, isDuos)` etc.) — **the viable route**, this is how
     HDT itself does it. Alternatively infer from shop-offering entities. **[V]** (not yet exercised).
  - **Caveat:** HDT has had race-detection *race conditions at game start* (the lobby race set may be
    empty/unstable for the first moment). Whichever route we pick must tolerate "not ready yet."

---

## Events — `Hearthstone_Deck_Tracker.API.GameEvents` [C]

Lifecycle: `OnGameStart`, `OnGameEnd`, `OnGameWon`/`OnGameLost`/`OnGameTied`, `OnInMenu`,
`OnModeChanged(Mode)`. Turn: `OnTurnStart(ActivePlayer)`. Cards: `OnPlayerPlay(Card)`,
`OnPlayerDraw(Card)`, `OnOpponentPlay(Card)`, `OnPlayerMinionAttack(AttackInfo)`, etc.

Polling: we already implement **`IPlugin.OnUpdate()` (~100 ms)** — the natural seam for reading
`Core.Game` each tick (currently a no-op in `Plugin.cs`).

---

## HearthDb card data — `HearthDb.Card` [C]

`DbfId` (int), `Id` (string — **equals `Entity.CardId`**), `Name`, `Race` / `SecondaryRace`,
`Type` (`CardType`), `Cost`/`Attack`/`Health`, `TechLevel` (BG tier), `IsBaconPoolMinion`,
`Collectible`, `Mechanics`. Enums live in `HearthDb.Enums`: `Race`, `CardType` (incl.
`BATTLEGROUND_QUEST_REWARD`/`_SPELL`/`_ANOMALY`/`_TRINKET`), `Zone`, `GameTag`.

---

## The data join (key finding)

An in-game `entity.CardId` (e.g. `"TB_BaconShop_HERO_16"`) maps to the **`externalId`** field that is
**already present in our `data/cards.json`** — but our `BgCard` model (`Search/BgCard.cs`) does **not
bind it today**, and `CardStore` has no lookup for it.

- Future work (NOT done now): add `public string ExternalId { get; set; }` to `BgCard` (Newtonsoft
  binds `externalId` case-insensitively) and a `CardStore.ByExternalId` dictionary. No data re-fetch
  needed — the field is in the JSON.
- `dbfId` is **mostly null** in our data, so `externalId` (string) is the reliable join, not `DbfId`.
  `HearthDb.Card.DbfId` (int) is a fallback only if we later populate `dbfId`.
- **Golden minions (live-confirmed gap):** tripled minions carry a trailing **`_G`** on the
  `CardId` (`BG35_801_G`) with no record of their own. Strip the `_G`, look up the base card, use its
  `attackGold/healthGold/imageGold`.
- **Risk still to validate:** other skin variants. Sample `externalId`s include `..._SKIN_D` forms;
  an in-game `CardId` might be a skin while our record is the base (or vice versa). Heroes joined fine
  live, but spot-check skinned minions when implementing.

### Tribe-name mapping (HearthDb `Race` → our `MinionTypes` strings)

`BEAST→"Beast"`, `MECHANICAL→"Mech"`, `MURLOC→"Murloc"`, `NAGA→"Naga"`, `QUILBOAR→"Quilboar"`,
`UNDEAD→"Undead"`, `DEMON→"Demon"`, `DRAGON→"Dragon"`, `ELEMENTAL→"Elemental"`, `PIRATE→"Pirate"`,
`ALL→"All"`, none / `INVALID`→`"Neutral"` (our virtual "minion with no tribe").

---

## Recommended first implementation step (for whenever we act)

Before any product feature, build a **read-only `GameState` reader** + a tiny **debug readout**
(on-screen panel or log line) that, in a live BG match, prints: turn, tavern tier, lobby tribes via
**both** candidate routes (so we learn which is reliable), board minions (joined to our cards via
`externalId`), trinkets, and anomaly. The user runs one real match; we confirm what actually reads.
This de-risks everything downstream. **Not built now** — documented as the entry point.

## Out of scope right now

No reader code, no `GameEvents` subscriptions, no `OnUpdate` logic, no pool filtering / lobby
awareness, no `BgCard.ExternalId` binding. "Reachable cards by chain reaction" is parked for a later
design pass.

# Persistence handover — pass 1 done, pass 2 next

**Branch:** `v2` · **Date:** 2026-09-07 · Companion to `docs/save-reload-recon.md` (the original
decompile-grounded design) and the auto-loaded memory `sod-motives-mod.md`.

This is a resume kit for continuing the save/reload persistence work in a fresh session. Trust it,
but **verify any symbol before relying on it** — regenerate interop decompiles with `ilspycmd`
against `<game>\BepInEx\interop\Assembly-CSharp.dll` if needed. Game root:
`C:\Program Files (x86)\Steam\steamapps\common\Shadows of Doubt`.

---

## Status

- **Pass 1 (custom-note reload) — DONE, committed `dd0e046`, verified in-game.** After save→reload,
  mod-authored custom notes (the layoffs "Redundancy List" + the F4 test note) now come back with
  their body text, clickable name-links, and case-board connections fully restored.
- **Pass 2 (interrogation/gossip reload) — NOT STARTED.** This is the next unit: make NPC testimony
  and the F9 aids survive a reload, by rehydrating `EventStore` + the `MurderSelector` per-victim maps
  from the same sidecar. Design below.

Build/test loop: `dotnet build -c Release` (auto-deploys to the game's `plugins` folder only when the
game is CLOSED; restart the game to load). Log at `<game>\BepInEx\LogOutput.log`, filter
`[SODMotives]` (persistence lines are tagged `[SODMotives] persist:`).

---

## Pass 1 architecture (what pass 2 extends)

The note **object** survives a reload (int `id`, its `dds` tree-id override, and its `customName`
title all persist as ordinary serialized evidence — plus the native writer/author fact). Everything
the tree id *points at* is runtime-only and rebuilt from StreamingAssets at boot (Toolbox DDS
tree/block/message, the `Strings` `dds.blocks` body, `AddOrGetLink` ids, our `CreateFact` links), so a
reloaded note renders blank with dead links and no connections. Fix = a mod-owned **sidecar** replayed
on load.

Files:
- **`Persistence.cs`** (new) — the whole mechanism:
  - `Records : List<NoteRecord>` + `RecordNote(...)` — accumulates every custom note placed
    (`{interactableId, treeId, kind, authorHumanId, citizenHumanIds, title}`), deduped by
    `interactableId`.
  - `OnSave(path)` — writes the sidecar `<save>.sodmotives.dat` (line-based, tab-delimited; header
    `SODMOTIVES_SIDECAR\tv1`, one `N\t…` line per note). Deliberately NOT JSON (avoids IL2CPP
    reflection quirks).
  - `MarkReplayPending()` — arms a replay; `Tick()` — polled every frame by `PersistenceRunner`
    (a `MonoBehaviour`, `DontDestroyOnLoad`), waits until `CityData.Instance.citizenDirectory` +
    `savableInteractableDictionary` are live, then rebuilds each note **once** (`_replayedIds`
    guards duplicate connections), retrying while interactables are still streaming in (~15 s /
    900-frame timeout).
  - Sidecar path resolution: `_selectedSavePath` (from the clicked load-menu slot) ?? `_currentSavePath`
    (last save this session).
- **Harmony hooks** (in `Persistence.cs`, auto-registered via `PatchAll`):
  - `Patch_Persist_Save` → postfix `SaveStateController.CaptureSaveStateAsync(string path, bool)`
    ([decomp :1583]) → `OnSave(path)`.
  - `Patch_Persist_Load` → postfix `SaveStateController.LoadSaveState(StateSaveData)` ([:1612],
    **fires only on load, not new-game**) → `MarkReplayPending()`.
  - `Patch_Persist_SlotClick` → postfix `SaveGameEntryController.OnLeftClick()` → capture
    `__instance.info.FullName` (the clicked `.sodb`).
- **`ClueInjector.cs`** — `BuildRedundancyDocTree` gained an optional `fixedTreeId` (re-register under
  the SAME surviving tree id); new `RebuildCustomNote(note, citizens, author, treeId)` does the replay
  (rebuild tree + re-point `SetDDSOverride`/`SetOverrideDDS` + redraw citizen connections); `RecordNote`
  called at both custom-note sites (F4 `SpawnTestCustomNote` kind `"test"`, real `InjectLayoffsCustomText`
  kind `"layoffs"`).
- **`Plugin.cs`** — `Persistence.Register()` in `Load()`.
- **`AffairSim.cs`** — `Persistence.ResetForNewGame()` in `SeedForNewGame()` (clears `Records` on a new
  game; does NOT clear `_replayPending`).

Verified interop signatures (decompiles in scratchpad `decomp-save/`, regenerate if gone):
`SaveStateController.CaptureSaveStateAsync(string,bool)`, `SaveStateController.LoadSaveState(StateSaveData)`,
`SaveGameEntryController.info : FileInfo` + `OnLeftClick()`, `CityData.savableInteractableDictionary :
Dictionary<int,Interactable>` ([CityData :1482]), `CityData.interactableDirectory` ([:1437]),
`CityData.citizenDirectory : List<Citizen>` ([:1227]), `CityConstructor.generateNew : bool` ([:2252]),
`CityConstructor.LoadState.loadComplete` ([:41]).

---

## Pass 2 — rehydrate interrogation/gossip

### Goal

After F6→Professional, F5 (a real workplace case), asking a knower about the victim yields the mod
gossip bubble, and F9 shows the pool + knowers. Today, a save→reload **loses all of this**: `EventStore`
and the `MurderSelector` per-victim maps are plain mod statics, never serialized, so after reload an NPC
asked about the still-active case says nothing and F9/F12 have no data. Pass 2 persists them on the same
sidecar and imports them in the same replay tick.

### Recommended design: import everything, don't re-seed on load (Option B)

On a NEW game, `AffairSim.SeedForNewGame()` (hooked to `MurderController.OnStartGame`) clears `EventStore`
and re-seeds affairs from the live `paramour` graph; workplace cases are materialized on-demand at murder
time. On LOAD we want the world exactly as it was saved, with event ids intact so the `MurderSelector`
maps still line up. So:

1. **Do NOT re-seed on load.** Import the saved `EventStore` (events with their ids + `knownBy`) verbatim
   from the sidecar, plus the `MurderSelector` maps. Affairs come from the sidecar (they were seeded at
   new-game and saved), so nothing needs re-deriving and every reference resolves by id.
2. This hinges on **`SeedForNewGame` not running on load** — see the risk section below. Verify first;
   gate if necessary.

### What to persist (exact shapes — grounded)

`EventStore` (`SocialEvent.cs:391`): private `_events`, `_byKnower`, `_nextId`. Export by reading
`EventStore.All`; per `SocialEvent` (`SocialEvent.cs:28`):
- `id:int`, `type:SocialEventType` (Affair/Promotion/Layoffs), `placeName:string`, `time:float`,
  `companyId:int`
- `a`/`b` → `a.humanID`/`b.humanID` (Human, may be null → -1)
- `group` → `List<int>` of `group[i].humanID`
- `knownBy` → the `HashSet<int>` verbatim (already ids)

`MurderSelector` maps (`MurderSelector.cs:47-61`, all `internal static readonly` → Persistence can
clear+repopulate directly, same assembly):
- `OverriddenVictimIds : HashSet<int>` — verbatim ids.
- `MotiveByVictim : Dictionary<int, MotiveResult>` — per victim id: `MotiveResult{type:MotiveType,
  target:Human→humanID, score:float, detail:string}` (confirm `MotiveResult` fields in `Motive.cs`).
- `PoolByVictim : Dictionary<int, List<SuspectEdge>>` — per victim id, a list of
  `SuspectEdge{suspect→humanID, victim→humanID, score:float, type:MotiveType, detail:string,
  evt→SocialEvent.id}` (`SuspectEdge` is `SocialEvent.cs:18`).
- `AffairByVictim`, `EventByVictim : Dictionary<int, SocialEvent>` — per victim id → the event's `id`.
- `UsedWorkplaceCompanies : HashSet<int>` — verbatim ids.

### Import order (in `Persistence.Tick`, after the id→Human map is built)

1. Build `id→Human` map (already done in `Tick` for pass 1 — reuse it).
2. Rebuild events: `new SocialEvent()` per exported event, set scalar fields, resolve `a`/`b`/`group`
   via the map, refill `knownBy` (note: `group` and `knownBy` are `readonly` — mutate in place with
   `.Add`, don't reassign). Register into a fresh `EventStore` **preserving each `id`** and rebuilding
   `_byKnower` from `knownBy`, then set `_nextId = max(id)+1`. Build a local `id→SocialEvent` map.
3. Rehydrate the `MurderSelector` maps, resolving Humans via the id→Human map and events via the
   id→SocialEvent map.

### API additions likely needed

- **`EventStore`**: an import entry point (private `_events`/`_byKnower`/`_nextId` are inaccessible to
  `Persistence`). Add `internal static void RehydrateFrom(List<SocialEvent> events)` — `Clear()`, add each
  preserving `id`, index each `knownBy` id, set `_nextId`. (Export needs no new API — `All` + public
  fields suffice.)
- **`MurderSelector`**: optional `RehydrateMaps(...)` helper for encapsulation; not strictly required —
  the maps are `internal` and can be cleared/repopulated from `Persistence` directly.
- **`Persistence`**: extend the sidecar with new line types (e.g. `E\t…` per event; `OV`, `MB`, `PB`,
  `AB`, `EB`, `UW` for the maps) written in `OnSave` and parsed in the replay. Keep `v1` readable or bump
  the header to `v2` and branch. **Also: `Tick` currently early-returns when there are zero `N` records —
  change it so event/map import still runs when there are events/maps but no notes.**

### Test (pass 2)

1. F6→Professional, F5 → a workplace case. F9 shows pool + `NEAREST KNOWERS`. F8 (always-answer), F12 to a
   knower, interview → ask about the victim → confirm the trailing gossip bubble appears.
2. Save → quit to menu → reload.
3. Interview the same knower about the victim again → the gossip bubble should **still** appear; F9 should
   still show the pool/knowers. (Also re-open the redundancy-list clue — pass 1 should still restore it.)

---

## THE risk to design around (read before coding pass 2)

`SeedForNewGame` (→ `EventStore.Clear()` + re-seed + `MurderSelector.ResetForNewGame()` +
`Persistence.ResetForNewGame()`) is hooked to `MurderController.OnStartGame` (`AffairSim.cs:71`). Pass 2's
whole design assumes **`OnStartGame` fires on NEW GAME ONLY, not on load** — which is consistent with the
existing "interrogation breaks on reload because `EventStore` is empty" finding (if it re-seeded on load,
`EventStore` wouldn't be empty). **Verify this explicitly** (log a line in the `OnStartGame` postfix and
watch whether it fires during a load).

- If it is new-game-only: no change needed; import in the replay tick as designed.
- If it ALSO fires on load: it will clobber the imported `EventStore`/maps. Mitigate by gating
  `SeedForNewGame` to skip when loading — e.g. check `CityConstructor` `generateNew == false`, or a
  "load in progress" flag set by the `LoadSaveState` hook — and let the sidecar import populate instead.
  (`Persistence.ResetForNewGame` already deliberately does NOT clear `_replayPending`, as a first guard.)

Related, minor: pass 1's `Records` has the same theoretical clobber exposure; it hasn't bitten because
`OnStartGame` appears new-game-only. Confirming this once settles both.

---

## Resume prompt

> Resume the SOD Motives mod (Shadows of Doubt, BepInEx IL2CPP) at `C:\Users\opiate\workspace\SODMotives`,
> branch `v2`. Read the auto-loaded memory `sod-motives-mod.md` and `docs/persistence-handover.md` first;
> trust them but VERIFY any symbol still exists before relying on it (regenerate interop decompiles with
> `ilspycmd` if needed). Persistence PASS 1 (custom-note reload) is DONE, committed `dd0e046`, and verified
> in-game. Start persistence PASS 2: rehydrate the interrogation/gossip layer (`EventStore` +
> `MurderSelector` per-victim maps) onto the SAME sidecar so NPC testimony and the F9 aids survive
> save/reload — follow `docs/persistence-handover.md` §"Pass 2". Interrogate me on the design forks before
> writing code (my usual pattern) — especially whether `SeedForNewGame`/`OnStartGame` fires on load and how
> to gate it.

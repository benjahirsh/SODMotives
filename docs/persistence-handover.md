# Persistence handover — pass 1 & 2 DONE

**Branch:** `v2` · **Date:** 2026-09-08 · Companion to `docs/save-reload-recon.md` (the original
decompile-grounded recon) and the auto-loaded memory `sod-motives-mod.md`.

Save/reload persistence for the mod's runtime-only state is **complete and verified in-game**. This
doc is the as-built record + a resume kit. Trust it, but **verify any symbol before relying on it** —
regenerate interop decompiles with `ilspycmd` against
`<game>\BepInEx\interop\Assembly-CSharp.dll` if needed. Game root:
`C:\Program Files (x86)\Steam\steamapps\common\Shadows of Doubt`.

---

## Status

- **Pass 1 (custom-note reload) — DONE, committed `dd0e046`, verified in-game.** After a save→reload,
  mod-authored custom notes (the layoffs "Redundancy List" + the F4 test note) come back with their
  body text, clickable name-links, and case-board connections restored.
- **Pass 2 (interrogation/gossip reload) — DONE (2026-09-08), verified in-game.** After a save→reload
  the F9 panel keeps its "motivated (mod)" verdict + suspect pool + knowers, and asking a knower about
  the victim still produces the mod gossip bubble. Verified across many NPCs and 4+ save/reload cycles;
  state re-persists identically cycle after cycle (log: `persist2: imported N event(s) + maps (…
  1 injected)` → `persist2: OnStartGame during a load — skipping new-game re-seed`, note rebuilt,
  `replay complete`, zero warnings).

Build/test loop: `dotnet build -c Release` (auto-deploys to the game's `plugins` folder only when the
game is CLOSED; restart the game to load). Log at `<game>\BepInEx\LogOutput.log`, filter
`[SODMotives]` (persistence lines tagged `[SODMotives] persist:` / `persist2:`).

---

## Pass 1 architecture (custom notes)

The note **object** survives a reload (int `id`, its `dds` tree-id override, and its `customName` title
persist as ordinary serialized evidence — plus the native writer/author fact). Everything the tree id
*points at* is runtime-only and rebuilt from StreamingAssets at boot (Toolbox DDS tree/block/message,
the `Strings` `dds.blocks` body, `AddOrGetLink` ids, our `CreateFact` links), so a reloaded note renders
blank with dead links and no connections. Fix = a mod-owned **sidecar** `<save>.sodmotives.dat` replayed
on load.

- **`Persistence.cs`** — the whole mechanism: `Records` (custom notes placed, deduped by
  `interactableId`); `OnSave(path)` writes the sidecar; `MarkReplayPending()` arms a replay; `Tick()`
  (polled by the `PersistenceRunner` MonoBehaviour, `DontDestroyOnLoad`) waits until
  `CityData.Instance.citizenDirectory` + `savableInteractableDictionary` are live, then rebuilds each
  note once (`_replayedIds` guards duplicate connections), retrying while interactables stream in
  (~15 s / 900-frame timeout).
- **Harmony hooks:** `Patch_Persist_Save` → postfix `SaveStateController.CaptureSaveStateAsync(string,
  bool)` → `OnSave`; `Patch_Persist_Load` → postfix `SaveStateController.LoadSaveState(StateSaveData)`
  (fires only on load) → `MarkReplayPending`; `Patch_Persist_SlotClick` → postfix
  `SaveGameEntryController.OnLeftClick()` → capture the clicked `.sodb` path.
- **`ClueInjector.cs`** — `BuildRedundancyDocTree(fixedTreeId)` re-registers under the surviving tree id;
  `RebuildCustomNote(note, citizens, author, treeId)` replays a note (rebuild tree + re-point
  `SetDDSOverride`/`SetOverrideDDS` + redraw citizen connections); `RecordNote` at both custom-note sites.
- **`Plugin.cs`** — `Persistence.Register()` in `Load()`.

---

## Pass 2 architecture (interrogation / gossip) — as built

Same sidecar, same replay tick. On save, `Persistence.OnSave` now also serializes the whole
social-event web + the per-case selection maps; on load, `Persistence.Tick` imports them once (as soon
as `CityData` citizens are live), independently of the note replay. Sidecar header bumped to `v2`.

**The gate (why it's a flag, not `generateNew`).** The watch-first probe proved two things in-game:
`MurderController.OnStartGame` **fires on load** as well as on a new game, and
`CityConstructor.Instance.generateNew` reads **False even on a new game** by the time `OnStartGame` runs
— so it cannot tell the two apart. Gate instead on `Persistence._loadInProgress`:
- Set true in the `LoadSaveState` postfix (`MarkReplayPending`) — fires only on load, and BEFORE
  `OnStartGame` (confirmed by log order).
- The `OnStartGame` postfix (`AffairSim.cs`) calls `Persistence.ConsumeLoadSeedSkip()`; on a load it
  SKIPS `AffairSim.SeedForNewGame()` and lets the import populate. **Consumed only by `OnStartGame`
  (never by the replay tick)**, so it is still set whichever of {tick-import, `OnStartGame`} runs first —
  the observed order is tick-import THEN `OnStartGame`, and the import is never clobbered.
- Belt: the postfix also skips while `IsImportPending()` (guards a hypothetical double-fire).

**Sidecar format (`v2`, tab-delimited).** Line kinds:
- `N` note (pass 1): `id treeId kind authorId citizenIdsCsv title`
- `E` event: `id type aId bId placeName time companyId groupCsv knownByCsv`
- `OV` / `UW`: overridden-victim ids / used-workplace-company ids (csv)
- `MB` motive-by-victim: `victimId type targetId score detail`
- `PB` pool edge (many per victim): `victimId suspectId score type detail eventId`
- `AB` / `EB`: affair-by-victim / event-by-victim: `victimId eventId`
- `IJ`: injected-victim ids (csv) — which victims' clue-set was already placed

**Import (`Persistence.ImportEventsAndMaps`).** Rebuild each `SocialEvent` (resolve `a`/`b`/`group` via
the id→Human map, refill `knownBy` — note `group`/`knownBy` are `readonly`, mutate in place), install via
`EventStore.RehydrateFrom` (ids preserved, `_byKnower` rebuilt, `_nextId = max+1`); build an
id→SocialEvent map; rebuild the six `MurderSelector` maps via `MurderSelector.RehydrateMaps` (edges/AB/EB
resolve events by id → the same installed objects); restore `ClueInjector._injected` via
`RestoreInjectedOnLoad`. **Fallback:** if the sidecar has no `E` lines (pre-pass-2 / vanilla save),
re-seed affairs from the live graph so gossip still works and pass-1 notes still restore. `Records` is
rebuilt from the loaded sidecar so the next save can't inherit a different timeline's notes.

**New API:** `EventStore.RehydrateFrom(List<SocialEvent>)` (`SocialEvent.cs`),
`MurderSelector.RehydrateMaps(...)` (`MurderSelector.cs`), `ClueInjector.GetInjectedVictimIds()` /
`RestoreInjectedOnLoad(...)` (`ClueInjector.cs`), `Persistence.ConsumeLoadSeedSkip()` /
`IsImportPending()`.

**Persisted shapes (grounded):** `SocialEvent` (`SocialEvent.cs`) `{id, type, a/b→humanID (null→-1),
group→ids, placeName, time, companyId, knownBy→ids}`; `MotiveResult` (`Motive.cs`)
`{type, target→humanID, score, detail}`; `SuspectEdge` (`SocialEvent.cs`)
`{suspect→humanID, score, type, detail, evt→event id}` (victim is the map key).

**Adversarial review (general-purpose agent) — 4 fixes applied, 1 accepted:**
- **#1** `Records` rebuilt from the loaded sidecar each load (no cross-save contamination).
- **#3** `_selectedSavePath` consumed per load (a stale load-menu slot can't shadow the current save path).
- **#4** `_injected` persisted (`IJ`) — a re-fired `SetMurderState(post)` on load can't inject a
  duplicate clue-set. Confirmed in-game NOT to re-fire, so belt-and-suspenders.
- **#5** removed the vestigial lazy re-seed at `Interrogation.OnPicked` (it could clear restored data).
- **ACCEPTED, not fixed — #2:** a double `OnStartGame` straddling a replay-tick frame could re-seed
  after import. Unobserved (exactly one fire per load in every test); the pass-2 test doubles as the
  check. Proper fix if it ever manifests: hook `CityConstructor.GenerateNewCity` to clear
  `_loadInProgress` on a genuine new game (its own watch-first exercise).

Interop decompiles for pass 2 in scratchpad `decomp-pass2/` (`CityConstructor`, `MurderController`,
`SaveStateController`); pass-1 decompiles in `decomp-save/`. Regenerate with `ilspycmd` if gone.

---

## Regression test (either pass)

1. New game / sandbox. **F6** → `Professional`, **F5** → a workplace case. **F9** shows
   `CASE TYPE: motivated (mod)` + suspect pool + `NEAREST KNOWERS`.
2. **F8** (always-answer), **F12** to the nearest knower, interview → "Do you know this person?" → pick
   the victim → the trailing gossip bubble appears.
3. **Save.** Pause menu → **Load** that save. (There is no "quit to menu"; pause-menu Load is the reload
   path, and staying in one launch keeps a single log file.)
4. **F9** still shows `motivated (mod)` + pool + knowers; re-interview → gossip bubble still appears;
   the redundancy-list clue still reads (pass 1). Repeat save→load once more to confirm re-persistence.

---

## What's next (roadmap)

Persistence is feature-complete. Open items (details in memory `sod-motives-mod.md` / `docs/roadmap-recon.md`):
- **W5 config knobs** — bind `Interrogation.Enable`; repoint the last affair-only log around
  `Plugin.cs:394`; move `DebugTools.ForceMotiveType` default to `None` for release.
- **Promotion-clue custom-doc polish** — route the promotion letter through the reusable custom-doc engine.
- **V2.2 motive-texture** — real-suspects-only; testimony = pointer / clue = detail; variable specificity
  weighted to the knower's vantage (spec in memory).
- **Release cleanup** — `ObviousTestNames=false`; gate/hide the F-key debug tools.

---

## Resume prompt (paste after /clear)

> Resume the SOD Motives mod (Shadows of Doubt, BepInEx IL2CPP) at `C:\Users\opiate\workspace\SODMotives`,
> branch `v2`. Read the auto-loaded memory `sod-motives-mod.md` and `docs/persistence-handover.md` first;
> trust them but VERIFY any symbol still exists before relying on it (regenerate interop decompiles with
> `ilspycmd` against `<game>\BepInEx\interop\Assembly-CSharp.dll` into a scratchpad if needed).
> Save/reload PERSISTENCE IS DONE and verified in-game: pass 1 (custom notes) committed `dd0e046`; pass 2
> (interrogation/gossip — EventStore + MurderSelector maps + `ClueInjector._injected` on a `v2` sidecar,
> gated by `Persistence._loadInProgress`) committed on `v2` (see `git log`). Next, pick up the roadmap
> (handover §"What's next" / memory): W5 config knobs, promotion-clue custom-doc polish, or V2.2
> motive-texture. Interrogate me on the design forks before writing code (my usual pattern).

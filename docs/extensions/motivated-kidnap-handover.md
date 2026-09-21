# Motivated kidnapping cases — handover / resume kit

**Written:** 2026-09-21 · Branch `v2` · **Goal for the next session: make KIDNAPPING cases motivated by the
mod** (currently they run as vanilla). Companion: auto-memory `sod-prerelease-progress.md` +
`sod-motives-mod.md`, and the sniper recon `docs/extensions/sniper-recon.md` (on the **`sniper-wip`** branch).

## PROGRESS 2026-09-21 (branch `kidnap-wip`, off `sniper-wip` + merged `v2`)

Steps 1–3 of the plan below are DONE; the rest (playtest → constrain) is pending the USER.
- **Cpp2IL re-read** (cached dump reused): the kidnap holding location is a **"den"** —
  `MurderMO.allowDen`/`denFurniture`/`denItems` + `MurderPreset.pickDen`/`blockVictimFromLeavingLocation`.
  The den is the `Murder.location` (no separate field). `TryPickNewVictimSite` is SNIPER-only (uses
  `TryGetSniperVantagePoint`); `IsValidLocation` is the location gate; `Tick` is the scheduler. Full write-up:
  **`docs/extensions/kidnap-recon.md`**.
- **Tooling built + deployed (NOT playtested):** F2 = `ForceKidnapCase` (F1 is reserved by the game) (reuses generalized `ForceCase`);
  `[Troubleshooting] MotivateKidnaps` (default OFF) relaxes the override caseType guard for kidnap;
  `ForceCase` now logs the live preset/MO's `pickDen`/`allowDen`/meet/phase fields. Existing `[trace]`
  state hook already covers forced-kidnap victims. Commit `787dd3a` (+ merge `fb709d6`).
- **NEXT = USER PLAYTEST:** mature save, EnableDebugKeys, press F2, read `LogOutput.log`
  (`[force:F2] preset/MO …` + `[trace] … state=>…`) + F9 — reaches `post` or stalls at `waitForLocation`?
  See the playtest protocol in `kidnap-recon.md`. Then constrain the pool to kidnap-viable pairs (killer
  controls a den) with a vanilla fallback (never hang), and only then flip `MotivateKidnaps` default-ON.

## Current shipped state (don't disturb)

- **v1.0.2 is LIVE on Thunderstore** (docs-only release; DLL is the clean v1.0.1 code). GitHub: latest release
  commit is **`23c40f2` on `v2`** — *check whether it was pushed + tagged `v1.0.1`/`v1.0.2`* (push may have
  been pending at clear time).
- Release channels: Thunderstore `opi/Better_Leads_and_Motives`, GitHub `benjahirsh/SODMotives` (branch v2),
  Steam guide `id=3804929108`.
- ⚠️ **NO AI attribution on this repo** — do NOT add `Co-Authored-By: Claude` (or 🤖) trailers to commits.
- Build: `dotnet build -c Release` (auto-deploys to `<game>/BepInEx/plugins/SODMotives` when the game is
  CLOSED; restart game to load). `[Debug] EnableDebugKeys` gates dev F-keys; **F9 = always-on case overlay**
  (shows `CASE TYPE: motivated (mod)` vs `vanilla / not overridden`, `MURDER STATE`, killer/victim).

## The goal

Right now the override motivates only `caseType == murder` (regular procedural murders). Kidnappings run
100% vanilla. Make the mod pick a **victim with real enemies + a killer from that pool** for kidnap cases too,
keep the kidnap MO's mechanics intact, and inject the usual clues — the same value the mod adds to murders.

## Why this is HARD (read before coding)

The naive approach — just let the override handle `caseType == kidnap` and swap in a motivated pair — will
almost certainly **hang the case**, exactly like the sniper attempt did:

- **Sniper analog (already tried, this session):** a force-created motivated sniper stalled in a
  **`travellingTo` loop** — the motive-chosen killer could never complete the shot (kept re-targeting sites:
  apartment → street → street). See `sniper-recon.md` on `sniper-wip`.
- The **original guard comment** in `Plugin.cs` says forcing a motivated pair onto a kidnapping **"hangs it at
  `waitForLocation`."** Kidnap needs a viable **holding location** (somewhere the kidnapper can hold the
  victim) and an abduction setup; a motive-chosen killer/victim may not satisfy those → stuck at
  `waitForLocation` forever.
- Kidnap is **multi-phase**: abduction → hold → ransom calls → (optional) kill. A swapped pair has to survive
  ALL of it, not just the initial pick.

So this is real work, not a flag flip. The likely shape of the solution: **intersect the motive pool with
kidnap-viability** (only motivated victims/killers whose situation satisfies the kidnap MO's site/holding
requirements), and **fall back to vanilla** when none qualify (never hang). Or drive/repair the state machine.

## What we confirmed this session (Cpp2IL + in-game)

- **`MurderController.ExecuteNewMurder(murderer, victim, preset, mo, victimSite)` is the universal murder
  creation entry point** — tiny body: `Murder..ctor(...)` + `SetUpdateEnabled(true)`; called by `Tick` (the
  scheduler). **You can create ANY case type by calling it directly** (this is how the sniper force-key works).
- The override (`Patch_ExecuteNewMurder_Override` prefix, `Plugin.cs`) gates on `procGenLoopActive &&
  preset.caseType == MurderPreset.CaseType.murder`. For `kidnap`/`sniper` it returns early (logs
  `override: special case type '<type>' … leaving vanilla, untouched`). **To motivate kidnap: relax that
  `caseType` guard for kidnap (behind a default-OFF toggle) AND handle the hang.**
- **`CaseType` enum = { murder, sniper, kidnap }** only.
- **`TriggerKidnappingCase()` does NOT spawn a kidnap** — it advances an EXISTING kidnap's objectives
  (`PinToCasePanel` + `Objective.Trigger`); calling it cold **NREs**. (The `kidnap-test` branch's F2 key used
  this and was a dead end — delete/ignore it.)
- **The 4 sandbox settings** (Game bools): `enableMurdererInSandbox` (= UI "Procedural Murders", MASTER),
  `enableRegularMurderCases` (= "Regular murders", the caseType==murder cases the mod motivates),
  `enableSniperCases`, `enableKidnapperCases`. Mod needs Procedural(master)+Regular ON; both default on.
- **A vanilla kidnap can coincidentally involve a pair the mod seeded a feud on** (city-wide seeding) — that is
  NOT the mod motivating it (F9 shows `vanilla / not overridden`). Don't mistake that for "kidnaps already work."

## Kidnap MO surface (from the interop `Assembly-CSharp.dll`, signatures only)

Per-`MurderController.Murder`: `kidnapKillPhase` (bool), `ransomAmount` (int), `ransomPhase`
(`KidnapRansomPhase`), `sniperVictimSite`. On `MurderController`: `pauseBeforeKidnapperKill` (float),
`kidnapperNotCaughtCall`, `kidnapperTauntCallTriggered/Time`, `kidnapperTauntPhone/FromPhone`,
`TriggerKidnappingCase()`, `TriggerRansomDelivery()`, `TriggerRansomFail()`, `ResetKidnapper()`,
`_CleanUpKidnapCall_d__67` (coroutine). `MurderState` enum: `none, acquireEuipment, research,
**waitForLocation**, travellingTo, executing, post, escaping, unsolved, solved`. `MurderMO` has
`allowAnywhere/allowHome/allowWork/allowPublic`, `compatibleWith` (List<MurderPreset>), `disabled`.

## Suggested plan for next session

1. **Re-fetch Cpp2IL** (official GitHub `2022.1.0-pre-release.21`, Windows exe; this session's dump is gone —
   scratchpad cleaned). Run: `Cpp2IL.exe --game-path "<game>" --exe-name "Shadows of Doubt" --use-processor
   attributeanalyzer,attributeinjector,callanalyzer --output-as dll_il_recovery --output-to out` (clean
   `[Calls]`/`[CalledBy]` call-graph attributes; complex bodies stub to `throw null`) AND `--output-as isil`
   (raw logic, numeric field offsets). Decompile the recovered `out/Assembly-CSharp.dll` with `ilspycmd -t
   MurderController` for the call graph.
2. **Read the kidnap flow**: `PickNewMurderer` kidnap branch (how vanilla picks the kidnapper + victim + holding
   location), the `Murder` state machine around `waitForLocation` for kidnap (what makes it advance vs stall),
   and how the holding location is chosen. This tells you what a motivated pair must satisfy.
3. **Build a gated force-kidnap key + toggle** (mirror the sniper F2 on `sniper-wip`): a debug key that calls
   `ExecuteNewMurder(motivatedKiller, motivatedVictim, kidnapPreset, kidnapMO, null)` (get the preset/MO via
   `Resources.FindObjectsOfTypeAll<MurderMO>()` filtered to `caseType == kidnap`), and a `[Troubleshooting]`
   `MotivateKidnaps` toggle (default OFF) that relaxes the override's caseType guard for kidnap.
4. **Playtest**: does the forced motivated kidnap reach `executing`/`post`, or stall at `waitForLocation`? Use
   F9. If it stalls, the holding-location/abduction setup is the blocker → constrain the victim pool to
   kidnap-viable victims, or repair the state machine, with **vanilla fallback when none qualify (never hang)**.
5. Only flip the toggle default-ON once it's proven solid in-game (no stalls, clues inject, case solves).

## Branches

- **`v2`** — release line (at `23c40f2`; v1.0.2 shipped). Do the kidnap work here or on a new branch off it.
- **`sniper-wip`** (`02acb79`) — F2 force-sniper key (`DebugTools.ForceCase`), FastMurderCadence fix, and
  `docs/extensions/sniper-recon.md`. **Reuse its force-case pattern for kidnap.** Sniper motivation is the same
  deferred problem (stalls); if you solve kidnap's hang, the sniper fix likely rhymes.
- **`kidnap-test`** (`aaf3306`) — throwaway; its F2 = the dead `TriggerKidnappingCase()` approach. Ignore/delete.

## Pending loose ends (carried over)

- **Push `v2` + tag `v1.0.2`** to GitHub if not done at clear time.
- User to add the Steam-guide feedback line to the Steam guide page.
- Sniper motivation still deferred (same problem as kidnap).
- Extension backlog (gossip name style, red-herring clues, theft motive, call-history, layoffs granularity) —
  see `docs/post-release-handover.md` + `docs/extensions/`.

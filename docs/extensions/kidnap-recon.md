# Motivated kidnapping cases — recon + tooling

**Written:** 2026-09-21 · Branch `kidnap-wip` (off `sniper-wip`) · Companion to
`docs/extensions/motivated-kidnap-handover.md` (the resume kit) and `sniper-recon.md`.

Goal: motivate KIDNAP cases the way the mod motivates ordinary murders — pick a victim with real
enemies + a killer from that pool — without hanging the case at `waitForLocation`.

## The key architectural finding: kidnappers need a "den"

Reading the MO/preset asset surface (interop `Assembly-CSharp.dll`) makes the kidnap requirement clear:

- **`MurderMO`** has den fields: **`allowDen`**, `denFurniture`, `denItems`, `denStyleOverride`
  (alongside the usual `allowHome/allowWork/allowPublic/allowStreets/allowAnywhere`).
- **`MurderPreset`** has **`pickDen`** (bool), **`blockVictimFromLeavingLocation`** (bool),
  **`killerMeetsVicim`** (bool, sic), `requiresResearchPhase`/`requiresAcquirePhase`,
  `nonHomeMaximumOccupantsTrigger`/`nonHomeMaximumOccupantsCancel`.

⇒ A kidnap holds the victim in a **den** — a private holding location the kidnapper controls. The
`Murder` object has no separate `den` field; the den is just the murder's **`location`** (the fields
are `location`, `currentVictimSite`/`victimSiteID`/`victimSiteIsStreet`, plus the kidnap-only
`ransomSite`/`ransomSiteID` for the money drop). So for a kidnap, the `waitForLocation` state is the
game resolving a valid **den** as `Murder.location`.

**This is why the naive pair-swap is expected to hang:** the override sets `victimSite = null` and lets
the game derive the location. For an ordinary murder that's the victim's home (always exists). For a
kidnap with `pickDen`, the game must seat a **holding den for the KILLER** (a private, low-occupancy,
controllable place). A motive-chosen kidnapper may have no such place → `waitForLocation` never resolves.

## Cpp2IL / call-graph findings (dll_il_recovery + ISIL, 2026-09-21)

Bodies stub to `throw null` in dll_il_recovery but the `[Calls]`/`[CalledBy]` attributes give the graph;
ISIL has the raw logic but numeric field offsets (painful — not fully decoded).

- **`MurderController.Tick(float)` is the scheduler/driver.** It calls (per the call graph)
  `EuipmentCheck`, `PickNewVictim`, `PickNewMurderer`, `ExecuteNewMurder`, `SetMurderState`,
  `CreateMurderGoal`, `TriggerRansomFail`, `NewAIController.SetRestrained`, `RemoveFakeNumber`. The
  `Murder` state machine advances from here + from `MurderController.Update`.
- **`ExecuteNewMurder(murderer, victim, preset, mo, site)` is the universal creation entry point** (tiny
  body: `Murder..ctor` + `SetUpdateEnabled(true)`). The `Murder..ctor` itself calls
  **`GenerateRansomDetails`** and `SetMurderState` — so ransom setup happens at construction for a kidnap.
- **`Murder.TryPickNewVictimSite(out NewGameLocation)` is SNIPER-specific**, not the kidnap den picker:
  its body calls `Toolbox.TryGetSniperVantagePoint` (both overloads), `FindSafeTeleport`,
  `PathFinder.GetPath`. Do NOT confuse it with kidnap location selection.
- **`Murder.IsValidLocation(NewGameLocation)` is the location gate** (`CalledBy` `Update`, x2). ISIL shows
  it comparing the candidate location's building/room against a game singleton (looks like a street/outside
  check) and branching on the murder's preset/caseType — i.e. it validates a candidate den before the case
  advances. Full offset decode not done; the empirical playtest is the faster oracle.
- **`SetMurderState` calls `TriggerKidnappingCase`** — i.e. once the abduction state is reached, the
  kidnap-case objectives (ransom calls, taunts) are wired up. `TriggerKidnappingCase()` only advances an
  EXISTING kidnap's objectives (it NREs if called cold — that was the dead `kidnap-test` F2 approach).
- **`CaseType` enum = { murder, sniper, kidnap }** only.

## Tooling added this session (branch `kidnap-wip`, built clean, deployed, NOT playtested)

1. **F2 = force-kidnap key** — `DebugTools.ForceKidnapCase()` → the generalized
   `ForceCase(CaseType.kidnap, "F2")` (same path as the sniper force key, now F3): grabs a loaded kidnap preset+MO via
   `Resources.FindObjectsOfTypeAll<MurderMO>()`, picks a motivated pair with `TryPickVictimCentric`, sets
   `currentMurderer/Victim`, calls `ExecuteNewMurder(...)` directly (bypasses the slow scheduler). Works
   regardless of the sandbox kidnap toggle or `MotivateKidnaps` (assets stay loaded). Bound in
   `[Debug Keys] ForceKidnapCase` (default F2 — F1 is reserved by the game — gated by `EnableDebugKeys`).
2. **`[Troubleshooting] MotivateKidnaps` toggle** (default OFF) — relaxes the override's caseType guard
   (`Plugin.cs` `Patch_ExecuteNewMurder_Override`) so NATURAL kidnaps also get a motivated pair. Sniper
   always stays vanilla. `MurderSelector.MotivateKidnaps`.
3. **Asset-detail dump** — `ForceCase` now logs the chosen preset/MO's case-defining fields
   (`pickDen`, `blockVictimFromLeavingLocation`, `killerMeetsVicim`, research/acquire phases,
   nonHomeMaxOccupants, and the MO's allow-home/work/public/streets/den/anywhere flags). These are
   ScriptableObject asset data only readable at runtime → this is how the playtest reveals the REAL
   requirements of the live kidnap preset(s).

The existing state trace (`Plugin.cs` `Patch_MurderStateInject` → `[SODMotives][trace] … state=>X`) already
fires for forced-kidnap victims (they're in `OverriddenVictimIds` via `TryPickVictimCentric`), so F9 +
`LogOutput.log` show exactly where the case sits.

## Playtest protocol (next — USER)

1. Load a mature save, config overlay (`` ` ``): `[Debug] EnableDebugKeys = true`.
2. (Optional) `[Troubleshooting] MotivateKidnaps = true` to also test the natural path.
3. Press **F2** (F1 is reserved by the game). Read `LogOutput.log` (filter `[SODMotives]`):
   - the `[force:F2] preset …` + `MO …` lines → the live kidnap preset's real requirements (esp.
     **`pickDen`** and **`allowDen`**).
   - the `[trace] … state=>…` lines → does it reach `executing`/`post`, or park at **`waitForLocation`**?
   - watch F9 MURDER STATE the same way.
4. Report back: which preset/MO was used, its `pickDen`/`allowDen` values, and the last state reached.

## EMPIRICAL RESULTS — playtest 2026-09-21 (CONFIRMED: den never seats)

Two forced kidnaps (F2) + observation, all with preset `Kidnapper` / MO `FinancialKidnapper`
(`allowDen=True` and every other allow-flag False, `pickDen=True`, `blockVictimFromLeavingLocation=True`,
`killerMeetsVicim=True`, research+acquire phases required, occupancy caps 99/99 = not the blocker):

- **Marta Castillo#177 → Riku Shimizu#260:** `acquireEuipment → research → waitForLocation` then **stuck**
  (`loc=<null>` the whole time). Never reached `travellingTo`/`executing`. F9 SCENE stayed `?` (no location),
  so scene teleport was impossible. User manually solved the case to move on (→ `solved`, still `loc=<null>`).
- **Breonia Frazier#219 → Rina Takagi#218:** same path, stuck at `waitForLocation`, `loc=<null>`. **Killer and
  victim were COHABITING** — so "killer has no home" is NOT the cause. A kidnap den almost certainly cannot be
  the victim's own residence (you can't hold someone where they can't leave *at their own home*), and when the
  pair cohabit the killer's only private residence is also the victim's → disqualified → no seatable den.
- **Contrast — a regular mod murder worked (with a watchdog assist):** `Matthaiso#249 → Sóley#36`
  (`TheCoporateKiller`) went `… → waitForLocation → travellingTo(Queensworth & Associates) → executing`, then
  **soft-locked in `executing`** (killer chased with a hammer, victim HP 100% after 24h) until the **stall
  watchdog intervened at 24.0h and force-finished the kill** → post → solved. So the watchdog works; 24h is
  just slow (hence the new End = fast-forward key).

⇒ **The blocker is den SEATING at `waitForLocation`, confirmed.** The location resolver (Update →
`IsValidLocation` gate) never finds a valid den for the motive-chosen kidnapper. Next: find the den-selection
code path (where the kidnap `location` candidate is generated) and either (a) constrain the kidnap victim pool
to killers who own a den-eligible private residence that ISN'T the victim's home, or (b) seat a den ourselves,
always with a vanilla fallback (never hang).

## Expected outcome + the fix if it hangs

Most likely: it parks at `waitForLocation` because the motive-chosen kidnapper has no seatable den.
Then the fix (matches the handover's recommended shape):

- **Constrain the kidnap victim/killer pool to kidnap-viable pairs** — e.g. require the KILLER to own /
  control a private low-occupancy residence (a den candidate), intersect that with the motive pool.
- **Fall back to vanilla when none qualify** (never hang): if no motivated pair is kidnap-viable, let the
  override leave the case untouched (it already no-ops when `TryPickVictimCentric` returns false).
- If the block is instead the abduction "meet" (`killerMeetsVicim`/`meetGoal1/2`) never happening, drive
  or relax that path, or pick pairs whose routines intersect.

Only flip `MotivateKidnaps` default-ON once a forced + a natural kidnap both reach `post` and solve clean.

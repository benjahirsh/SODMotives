# Motivated sniper cases — recon

**Written:** 2026-09-21 · Branch `v2` · Recon only, no code yet. Companion to the extensions backlog.
Goal: understand the vanilla sniper murder pipeline well enough to decide whether the mod's
victim-centric override can drive a sniper case without the `waitForLocation` hang that the existing
guard ([Plugin.cs](../../Plugin.cs) `Patch_ExecuteNewMurder_Override`) was written to avoid.

## Why this is worth a look

The old guard skips **both** `sniper` and `kidnap` special case types with a single comment
("forcing a motivated pair onto e.g. a kidnapping hangs it at `waitForLocation`"). That hang was
observed/assumed for **kidnap**; whether **sniper** hangs the same way was never actually tested.
The player's real-world observation is the tell: vanilla sniper cases they've seen were **street
assassinations from a shared-area balcony** — no victim-home or sniper-home involved. If the sniper
victim site is chosen from the victim's **routine / public locations** (not their home), then a
motive-selected victim should support a sniper case fine, and the guard is over-broad for sniper.

## Case-type model (confirmed)

`MurderPreset.CaseType` has exactly three values: `murder`, `sniper`, `kidnap`. The mod fully
motivates `murder` (all the ordinary weapon/signature killer MOs are this type). `sniper` and
`kidnap` are the only specials it skips.

## Sniper architecture (confirmed from interop signatures)

Method/field signatures in `Assembly-CSharp.dll` (via `ilspycmd`; bodies NOT yet read):

Per-`Murder` (the `MurderController.Murder` inner object):
- `NewGameLocation sniperVictimSite` — the location the victim is shot **at**.
- `Vector3Int sniperKillShotNode` — the node the shot resolves against.
- `bool TryPickNewVictimSite(out NewGameLocation newTargetSite)` — **picks the site for the kill.**
  This is the crux: it returns a location, and its logic (routine-based vs home-based) decides
  whether an arbitrary motivated victim is usable.

On `MurderController`:
- `List<NewGameLocation> sniperVictimSites` — candidate sites (plural → a pool, consistent with
  "pick a public place the victim visits").
- `CachedSniperLocation { NewWall location; float score }` — a scored **wall** (a window/balcony edge
  with line of sight = the nest). Scored ⇒ the game ranks vantage points.
- `float sniperShotDelay`, `Vector3Int sniperKillShotNode`.
- `void ExecuteSniperShot(Human victim, Human killer, Ray confirmationRay, RaycastHit confirmationHit,
  Transform victimTargetTransform, bool forceKill = false)` — performs the shot once victim + nest +
  line of sight line up.

On `Toolbox` (the vantage solver — **strong confirmation the system is target-site-first**):
- `bool TryGetSniperVantagePoint(Human sniper, NewGameLocation requiredTargetSite, out NewWall vantagePoint,
  out float vantageScore, List<NewNode.NodeAccess> accessCheckList = null)` — given **where the victim will
  be** (`requiredTargetSite`), find a wall to shoot from. The target site drives the search; the vantage is
  derived from it, not the reverse.
- `bool ScanBuildingForSniperVantagePoints(Human sniper, NewBuilding building, NewGameLocation
  requiredTargetSite, out NewWall vantagePoint, out float vantageScore, ref List<NewNode.NodeAccess> …)`.
- `bool TryGetSniperVantagePoint(NewGameLocation vantageLocation, out …, out List<NewGameLocation>
  possibleTargetSites, …)` — the inverse (given a vantage, list target sites it covers).
- `int sniperLOSMask` — the line-of-sight raycast mask.

⇒ The sniper picks a **target site** (a place the victim is exposed — their routine/public locations), then
finds a nearby vantage wall. A motive-selected victim with any exposed routine location should therefore
support a sniper case; **no home vantage is required in the general path.** This is the balcony-street pattern.

On `MurderMO` (the killer archetype preset):
- `bool requiresSniperVantageAtHome` — **a per-MO flag, not the general rule.** Only MOs with this set
  need the victim's *home* to have a vantage. The street-assassination MO the player has seen almost
  certainly has this **false**, using routine/public sites instead. (Needs body confirmation, but the
  plural `sniperVictimSites` + scored `CachedSniperLocation` + this being a *per-MO* toggle all point
  the same way.)
- `allowAnywhere / allowHome / allowWork / allowPublic` — where the kill may happen. A public/anywhere
  sniper MO is exactly the balcony-street pattern.

## Murder state flow (confirmed)

`MurderState`: `none → acquireEuipment → research → waitForLocation → travellingTo → executing →
post → escaping → unsolved/solved`.

`waitForLocation` is where a sniper/kidnap sits until a valid site is found. The kidnap hang is a
case stuck here forever (no viable holding/site for the swapped pair). **The open question for sniper
is whether `TryPickNewVictimSite` reliably yields a site for a motive-chosen victim** — if yes, the
case advances past `waitForLocation` normally and there is no hang.

## The override tension

The mod's override ([Plugin.cs](../../Plugin.cs)) runs in an `ExecuteNewMurder` **prefix**, swaps
`newMurderer`/`newVictim`, and sets `victimSite = null` (for ordinary murders the game re-derives the
site from the victim's home). For a sniper, the site is instead produced later by
`Murder.TryPickNewVictimSite`. So the question is purely: **does that method find a site from the
victim's routine (robust) or demand a home vantage (fragile)?**

- If **routine-based** (expected): the late swap should Just Work for sniper — we swap *who*, vanilla
  still picks *where* from the new victim's routine. Low risk.
- If **home-vantage-gated** for the enabled MO: we'd need to intersect {has enemies} ∩ {home has a
  vantage} or fall back to vanilla — same shape as the kidnap problem.

## Test / debug hooks (confirmed — enables a fast sniper loop)

- `MurderController.debugMurderPreset` (MurderPreset) and `debugMO` (MurderMO) — the game's own
  force-a-specific-case fields. Setting these before `TriggerNextMurder()` should let us force a
  **sniper** case on demand (vs F4/`TriggerNextMurder` which currently yields `murder`). Need the
  `TriggerNextMurder` body to confirm it honours `debugMurderPreset`, and a way to fetch a sniper
  `MurderPreset`/`MurderMO` asset from `Toolbox`.
- `MurderController.TriggerKidnappingCase()` — a direct kidnap trigger (for the later kidnap phase).
- `ExecuteSniperShot(...)`, `SetMurderState(...)`, `SetMurderLocation(...)` — manual drivers if we
  ever need to unstick or force-resolve a case during testing.

## Open questions (need method BODIES — Cpp2IL 2022.1.0-pre-release.21, not on disk right now)

1. `Murder.TryPickNewVictimSite` — routine/public-location based, or home-vantage gated? **(the
   feasibility decider)**
2. `MurderController.PickNewVictim` sniper branch — does it pre-filter victims by vantage/site
   eligibility? If so, our victim must pass the same filter.
3. `TriggerNextMurder` — does it consume `debugMurderPreset`/`debugMO`? (for the test loop)
4. Default value of `requiresSniperVantageAtHome` on the commonly-enabled sniper MO(s).

## Cpp2IL findings (2026-09-21, bodies read)

Re-fetched Cpp2IL `2022.1.0-pre-release.21` (official GitHub) to the session scratchpad and dumped the game
assembly two ways: `--output-as dll_il_recovery` (DLLs whose `[Calls]`/`[CalledBy]` attributes give a clean
call graph; complex bodies stub to `throw null`) and `--output-as isil` (raw ISIL per method — has the logic
but uses numeric field offsets, no field names). Key results:

- **`ExecuteNewMurder(murderer, victim, preset, mo, site)` is the universal creation entry point.** Its body
  is tiny: it calls `Murder..ctor(Human, Human, MurderPreset, MurderMO, NewGameLocation)` then
  `SetUpdateEnabled(true)`. It is `CalledBy` `MurderController.Tick` (the scheduler) and `ChapterIntro`
  (story). So the scheduler's whole job is PickNewMurderer → PickNewVictim → **ExecuteNewMurder**, and the
  `Murder` ctor + its update loop drive the state machine (research → … → executing → post). **⇒ We can
  create a case of ANY type by calling `ExecuteNewMurder` directly, bypassing the scheduler.** (This is what
  the new F2 force-sniper key does.)
- **`TriggerNextMurder()` (what F4 calls) is a dev inspector `[Button]`, `CallerCount = 0`.** Its body just
  zeroes a field (a scheduling timer) and `List.Find`s an `Objective` to `Complete()`. It does NOT call
  ExecuteNewMurder — it only nudges the scheduler's timer, which is why F4 never produced an immediate murder
  and only ever re-initialised the loop (and re-ran our OnStartGame seed).
- **Why a fresh-game murder stalls at "murderer assigned, victim null":** the scheduler assigns
  `currentMurderer` early but only calls PickNewVictim → ExecuteNewMurder later on its own timing; in a fresh
  city that is game-days out, and F4 can't shortcut it. The F2 force key sidesteps this completely.
- **Sniper is target-site-first (feasibility looks good):** `Toolbox.TryGetSniperVantagePoint(sniper,
  requiredTargetSite, …)` derives the vantage from where the victim will be, and `requiresSniperVantageAtHome`
  is a per-MO flag — consistent with the street/balcony pattern. Whether a motive-selected victim reliably
  yields a target site is what the F2 test measures directly (executes vs stalls at `waitForLocation`).

**Force-sniper key (F2), shipped 2026-09-21:** `DebugTools.ForceCase(caseType, tag)` finds a loaded MO +
compatible preset of the case type via `Resources.FindObjectsOfTypeAll<MurderMO>()` (works even if the
sandbox toggle is off — assets stay loaded), picks a motivated pair via `MurderSelector.TryPickVictimCentric`,
sets `currentMurderer/Victim`, and calls `ExecuteNewMurder(...)`. Generalises to kidnap later. NOT yet
playtested.

## Recommended path

Two ways to close Q1/Q2 (the only ones that gate feasibility):

- **Empirical (fastest):** add a gated, default-OFF config toggle that lets the override handle
  `sniper` (keep `kidnap` guarded). Build, playtest a forced/real sniper case, watch the F9 overlay +
  logs for whether it reaches `executing` or sticks at `waitForLocation`. This answers "does it hang?"
  directly — the thing that actually matters — without reading bodies.
- **Static (authoritative):** download Cpp2IL and dump `MurderController` + `Murder` bodies to read
  the site-selection logic before writing code. Heavier; needs the tool fetched + run.

Given the player already wants to test, **empirical first**, fall back to Cpp2IL only if it hangs.

## 2026-09-24 — sniper diagnostics + tailing teleports built (RESUME HERE)

**Context:** kidnaps shipped as 1.1.0. Resumed sniper work. (The ransom-payment victim glitch did NOT reproduce
on a later test, so it is a WATCH item, not being fixed now.) Branch `sniper-wip` (fast-forwarded to `v2`).
Prior playtest finding stands: a FORCED motivated sniper (F3) does NOT hang at `waitForLocation` like kidnaps —
it gets a site but **loops in `travellingTo`, re-picking sites, never firing** (scene bounced apartment->
street->street). So the crux is the `travellingTo -> executing` leg (reach a vantage + take the shot).

**Built + deployed (builds clean 0-warn, ALL gated behind `[Debug] EnableDebugKeys`):** a sniper observer + live
sampler in `MurderWatchdog.Tick`, firing for ANY sniper case (forced F3 OR vanilla), to capture the intended
flow and diagnose the loop:
- `[sniper-obs]` (once per case): OURS vs VANILLA, killer<->victim relationship, preset + MO name, MO flags
  (`requiresSniperVantageAtHome` + allow home/work/public/streets/anywhere), killer/victim homes, the initial
  `sniperVictimSite`, and whether a vantage wall exists for (killer, site) via the game's own solver
  `Toolbox.Instance.TryGetSniperVantagePoint`. Also flips on the game's verbose `Murder:` narration (-> Player.log).
- `[sniper-live]` (throttled 0.05 game-h, cap 200): state, `sniperVictimSite`, victim@site?, dist(victim->site),
  killer@loc, dist(killer->site), dist(killer->victim), `sniperKillShotNode`, and the live vantage probe. Lets us
  SEE whether the site keeps changing / no vantage is ever found (= the re-pick loop) vs a vantage exists but the
  killer never reaches it (= a travel/positioning problem) vs killer reaches it but never fires (= weapon?).
- **Tailing aid:** F8 now teleports to the KILLER's CURRENT position and F12 to the VICTIM's CURRENT position
  (were killer-knower / victim-home). Jump straight to whoever you are tailing.

**API confirmed this session (ilspycmd vs interop):** `Toolbox.Instance` (singleton) + `bool
TryGetSniperVantagePoint(Human sniper, NewGameLocation requiredTargetSite, out NewWall vantage, out float score,
List<NewNode.NodeAccess> = null)`; `Murder.sniperVictimSite` (NewGameLocation), `Murder.sniperKillShotNode`
(Vector3Int), `Murder.TryPickNewVictimSite(out NewGameLocation)`; `MurderController.ExecuteSniperShot(...)`.

**NEXT — USER playtest (vanilla first, to learn intended behaviour):** sandbox with Sniper case type ON,
`[Debug] EnableDebugKeys=true`, NORMAL speed. Let a VANILLA sniper occur and TAIL the killer + victim (F8/F12).
Read `[sniper-obs]`/`[sniper-live]` (BepInEx `LogOutput.log`) + the `Murder:` narration (`Player.log`). Watch:
does the victim go to the `sniperVictimSite` (a routine/public spot)? does the killer walk to a vantage wall
(vantage=FOUND) and fire (`executing -> post`)? Then force one with F3 (OURS) and DIFF where it diverges (no
vantage for the chosen site? killer never reaches the wall? site re-picks forever? no weapon?). The diff points
to the fix (constrain the victim to a sniper-viable exposed site, drive the killer to the vantage, or supply a
weapon). Weapon/inventory logging is NOT in yet — add it for the OURS pass once the vanilla baseline is known.

### Vanilla sniper flow — OBSERVED IN-GAME (2026-09-24, save/load position comparison)
User ran several VANILLA sniper cases, save/reloading to freeze + compare the killer's and victim's positions at
each state. Confirmed flow:
1. **acquireEquipment** — the KILLER starts here (acquires the rifle).
2. **waitForLocation** — the VICTIM travels to the murder scene (the `sniperVictimSite`). So waitForLocation is
   the game WAITING FOR THE VICTIM to reach an exposed, shootable location — not (as the name might suggest) the
   killer waiting for a nest.
3. **travellingTo** — the KILLER moves to the sniping / vantage position (the nest wall).
4. Killer **shoots** the victim -> **unsolved**.

**THE KEY FINDING — vanilla pairs look GEOMETRY-constrained, not just motive/routine:** the game appears to pick
the killer/victim pair from the RELATIVE POSITIONING of their locations — the victim must be VISIBLE FROM A
WINDOW of a vantage the killer can reach. One run shot the victim at their WORKPLACE (not home), so the site can
be home OR work OR (historically) a street. In normal play the user historically saw victims IN THE STREETS with
the sniper firing from a PUBLICLY ACCESSIBLE ROOFTOP only one or two storeys up.

**Why our motivated sniper loops (now explained):** vanilla guarantees a line-of-sight-viable pair by
construction; our override picks the pair by RELATIONSHIP and ignores geometry, so the motive-chosen killer
usually has NO reachable vantage covering any site the victim visits -> the site/vantage search keeps failing ->
the `travellingTo` re-pick loop. This is the SAME shape as the kidnap "walk-reachable den" problem: a
relationship-chosen pair breaks a geometric/access precondition vanilla never violates.

**Fix direction (for when we return):** constrain the motivated-sniper choice to a VANTAGE-VIABLE pair/site —
only motivate a sniper when the killer HAS a reachable vantage onto a site the victim actually visits
(home/work/routine); else fall back to vanilla. Tools: the `[sniper-live]` vantage probe
(`Toolbox.Instance.TryGetSniperVantagePoint(killer, site, ...)`) + `Murder.TryPickNewVictimSite(out site)` — pick
the victim's exposed site, confirm a vantage exists for the killer, and only then swap the pair (mirrors
`EnsureKidnapDen` / walk-reachable-den). NEXT playtest (forced F3 + the sniper diagnostics) should show
`vantage=NONE` for our pairs, validating this before building the constraint. Open question: can we pre-filter
the victim pool by "some site of theirs has a killer-reachable vantage", or do we pick the pair then search for a
viable site among the victim's routine locations?

### 2026-09-24 (later) — branch brought current + weapon probe added (RESUME: run the confirmation playtest)
- **Merged `v2` into `sniper-wip`** (commit `ee862be`): inherits the shipped 1.1.1 kidnap save/load fixes
  (`EnsureVictimDenGoal` rebuild + `KidnapReachedHold` re-arm) and the F2 force-kidnap key removal. The merge was
  region-disjoint (kidnap fix vs sniper diagnostics vs F8/F12 tailing) so it auto-resolved; verified F2 gone
  (keys jump F3->F4), F3/F8/F12 sniper+tailing bindings intact, and the kidnap fix methods present. Builds clean.
- **Added a WEAPON/equipment probe** to the sniper diagnostics (commit `d7c5b11`): `DescribeSniperWeapon(murder)`
  reads `Murder.acquiredEquipment` / `weaponPreset` / `weapon` (the real Interactable) / `weaponStr` and appends to
  both `[sniper-obs]` (once) and `[sniper-live]` (throttled). Rationale: a motivated sniper that reaches
  `travellingTo` has PASSED `acquireEquipment`, so it should read `acquired=True held=<a rifle>`; if it instead
  reads no weapon, the loop is an acquire failure, not geometry, and the fix is different. Dev-only (gated behind
  `[Debug] EnableDebugKeys`). Build clean 0-warn, auto-deployed.
- **Watchdog note (so the playtest isn't cut short):** a forced sniper victim IS in `OverriddenVictimIds`, so the
  generic `waitForLocation` stall-cancel (`WaitLocationStallHours=12`) applies. It reverts our forced sniper to
  vanilla after 12 *game-hours* accumulated in `waitForLocation` (the clock does NOT reset across a
  waitForLocation<->travellingTo re-pick loop). That is ample: the `[sniper-live]` sampler covers ~10 game-hours
  (200 samples x 0.05h), so a forced sniper yields plenty of loop samples before the clean revert. (Cosmetic: the
  cancel log line is kidnap-worded — "never WALKED into the den" — misleading for a sniper; the vantage-viable
  redesign will replace this path, so left as-is.)
- **`MotivatedSniperShare` slider added (commit `c6e44f6`):** a `[Motive Mix] MotivatedSniperShare` config slider
  (default **0**), the sniper analogue of `MotivatedKidnapShare` — `MurderSelector.MotivatedSniperShare` +
  `ShouldMotivateSniper()` + an `allowSniper` branch in `Patch_ExecuteNewMurder_Override`. Set it to **1** to
  motivate NATURAL sandbox sniper cases (no F3 needed). Default 0 because motivated snipers still loop; this is a
  test enabler (and the slider half of the eventual fix — the vantage-viable constraint is the other half).
  Test setup for sniper-only cases: sandbox Procedural Murders ON + Sniper cases ON + Regular murders OFF +
  Kidnapping OFF (so every generated case is a sniper, mirroring the kidnap-only setup), `MotivatedSniperShare=1`,
  `[Debug] EnableDebugKeys=true`, NORMAL speed.
- **IMMEDIATE NEXT (USER playtest, then I read the logs myself):** either force with **F3** OR set
  `MotivatedSniperShare=1` and let a natural sniper occur; `[Debug] EnableDebugKeys=true`, NORMAL speed. Tail with F8
  (killer) / F12 (victim). Then I read `LogOutput.log` for `[sniper-obs]`/`[sniper-live]` to CONFIRM the hypothesis:
  `OURS` pairs should show `vantage=NONE` (validating geometry as the loop cause) with `weapon: acquired=True`
  (ruling out an acquire failure). If instead `vantage=FOUND` but the killer never reaches it, or `acquired=False`,
  the fix direction changes. Only after this confirmation do I build the vantage-viable constraint (Plugin.cs
  override un-guard behind a default-0 `MotivatedSniperShare` slider + a viability check in `MurderSelector`).

### 2026-09-24 (playtest 1 of the slider) — A MOTIVATED SNIPER FIRED (big result). Read logs, findings below.
User set `MotivatedSniperShare=1`, ran two natural sandbox snipers, fast-forwarded to trigger the shot (which then
chained a second case because `[Troubleshooting] FastMurderCadence` was 0). I read `LogOutput.log` + `Player.log`.

**THE HEADLINE: a motivated sniper actually fired and killed the victim** — the recon's "motivated snipers never
fire, they loop forever" fear is WRONG for this MO.
- Both cases used **preset `Sniper` / MO `VoyeurSniper`**, and `VoyeurSniper` has **`requiresSniperVantageAtHome=True`**
  (allow home/work/public/streets/anywhere = T/T/F/F/F). So this MO does NOT use a routine/public `sniperVictimSite`
  at all — it shoots the victim AT HOME. `SetMurderLocation` = the VICTIM'S HOME; `Murder.sniperVictimSite` stays
  `<null>` the whole time. (This answers recon Q4: the loaded sniper MO is the home-voyeur type, not the street type.)
- **Case 1 = Samantha Richardson#120 -> Violet Andrews#257, `[workTeam]` coworkers (like~0.59).** Killer waited in
  their OWN apartment (1302 Zeng Terrace) with the rifle (`held=Hamilton Rifle`, `acquired=True`); victim at home
  (1501 Etheridge Heights, ~55m away). State sat in `travellingTo` for ~194 samples, then the shot fired ->
  `unsolved`. `killShotNode=(45,28,13)` (real geometry). Autopsy: **"A bullet wound from high calibre ammunition;
  .309 or deer slug"** + an **Entry Wound** on the body. A REAL, solvable-ish sniper kill.
- **Case 2 = Ru Bai#204 -> Finley Noel#133, strangers (NO EDGE).** Killer 402 Etheridge Heights, victim 704 Plaza
  Orchid Hotel (different buildings). Only 32 samples, still `travellingTo`, never fired in the captured window =
  the loop case (plausibly no vantage onto the victim's home). This is the pair the vantage-viable constraint filters.

**So the determinant is GEOMETRY, as hypothesised — but "at home", not at a routine site:** case 1's coworkers happen
to live ~55m apart with LOS (killer's apartment overlooks the victim's), so the shot connects; case 2's strangers are
in different buildings, so it loops. The fix = only motivate a `VoyeurSniper` when the killer has a vantage onto the
VICTIM'S HOME (`Toolbox.TryGetSniperVantagePoint(killer, victim.home, out wall, out score)`), else vanilla.

**TWO PROBLEMS to resolve (from the same root):**
1. **No window bullet-hole / trajectory evidence.** The kill produced only an Entry Wound + high-calibre autopsy; a
   WHOLE-`Player.log` sweep found NO broken-window / bullet-hole / trajectory evidence object at all. Yet
   `killShotNode` WAS computed. Leading hypothesis: our pair-swap override bypasses the game's normal vantage-first
   setup (pick site -> score a `CachedSniperLocation{NewWall}` vantage wall -> `ExecuteSniperShot` through that wall
   spawns the window evidence). Because `sniperVictimSite` stayed null and the case ran off `murder.location`, the
   shot resolved WITHOUT establishing the vantage wall, so no window evidence spawned -> weaker as a solvable sniper
   case. UNCONFIRMED vs a fast-forward artifact — the normal-speed retest decides (see NEXT).
2. **Slow (waits in `travellingTo`), and the fast-forward + `FastMurderCadence=0` chained a second case immediately.**
   The wait is somewhat inherent (the voyeur sniper lingers at the vantage until the victim is shootable at a window);
   user will retest with `FastMurderCadence=OFF` and NO fast-forward. (Backlog item stands: FastMurderCadence doesn't
   gate kidnaps either.)

**DIAGNOSTIC BUG FIXED (commit `4ca928e`):** `[sniper-obs]`/`[sniper-live]` were probing the vantage against
`sniperVictimSite` (null for VoyeurSniper) so `vantage` always read "killer/site null" — measured nothing. Now they
probe `site = sniperVictimSite ?? murder.location` AND `victim.home` explicitly (logging `siteVantage` + `homeVantage`),
and the distances/`V_AT_SITE` use that real site. So the next run will actually show FOUND/NONE.

**NEXT (USER, normal speed):** `MotivatedSniperShare=1`, `[Debug] EnableDebugKeys=true`, **NORMAL speed, NO
fast-forward, `FastMurderCadence=OFF`.** Let a motivated sniper run to the kill on its own. Two questions the logs +
your eyes answer: (a) does `homeVantage=FOUND` for a firing pair and `NONE` for a looping one (confirms the
constraint predicate)? (b) at normal speed, does the kill leave a WINDOW BULLET HOLE / trajectory evidence, or still
none (fast-forward artifact vs a real setup gap)? Then I build: the vantage-viable constraint, and — if (b) shows no
window evidence — establish the vantage wall so the game spawns proper sniper evidence (decode how ExecuteSniperShot
spawns it).

### 2026-09-24 (playtest 2, corrected diagnostic) — CONFIRMED: game force-resolves a no-LOS kill. PIVOT to a street MO.
Ran a natural motivated sniper with the corrected diagnostic (`siteVantage`/`homeVantage`).
- **Case: Jimena Isaac#192 -> Jayla Price#166, `[familiarResidence]` (building neighbours).** killer.home=**1701 Zeng
  Terrace**, victim.home=**1503 Zeng Terrace** = the SAME building, different floors. `SetMurderLocation`=1503 Zeng
  Terrace (victim home). MO again `VoyeurSniper`.
- **`homeVantage=NONE` for ALL 200 samples** — the killer has NO reachable vantage onto the victim's home (no
  cross-unit LOS within one building). The corrected diagnostic works and proves the geometry.
- **The kill still happened, and it was the GAME, not our watchdog.** Player.log: `MurderController.ExecuteSniperShot`
  was invoked ~21x, then `Murder: Set murder state: executing -> post -> escaping -> unsolved`. NO `[SODMotives][watchdog]`
  line at all; and our executing force-finish has a co-location gate (killer must be AT the victim) which this pair
  fails, so it could not have fired. **So the game's own sniper resolution force-fires the shot even with
  `homeVantage=NONE`** (retries ExecuteSniperShot then resolves) -> the nonsensical kill the user saw: victim shot in
  a back room, no window broken, no line of sight. (Vanilla never hits this because vanilla picks LOS-viable pairs by
  construction; our relationship pair-swap breaks that invariant.)

**KEY REFRAME (drives the fix):** the loaded home-voyeur MO (`VoyeurSniper`, `requiresSniperVantageAtHome=True`) needs
a RARE home-to-home vantage, and when forced without one the game produces a nonsensical kill. The player's
instinct is right: motivated snipers should use a STREET/rooftop MO that shoots the victim at a PUBLIC/routine site
(works for any pair regardless of where they live), i.e. an MO with `requiresSniperVantageAtHome=False`. Candidate
from the asset bundle: **`ExCopSniper`** (flags unconfirmed). The home-voyeur MO should only be used when the killer
genuinely has a vantage onto the victim's home (rare), else use the street MO, else vanilla.

**BUILT this session (deployed, committed):**
- **MO enumerator `[mo-dump]`** (commit `4cf667f`): logs every loaded sniper-compatible MurderMO + its
  `requiresSniperVantageAtHome` and allow flags on game start (behind `EnableDebugKeys`). Gives the authoritative MO
  list (is there a street MO? what are its flags?).
- **F3 now prefers a STREET sniper MO** (commit `67c3372`): `ForceCase` picks a sniper MO with
  `requiresSniperVantageAtHome==false` if one exists (fallback = first match). So F3 tests the rooftop/public pattern.

**NEXT (USER):** relaunch (all of the above is deployed). On startup `[mo-dump]` prints the sniper MO list — I read it
to confirm a street MO + flags. Then press **F3**: it now forces a STREET sniper (if one exists). Let it run at
NORMAL speed (FastMurderCadence OFF). I read `[sniper-obs]`/`[sniper-live]` (does `siteVantage=FOUND` on a public
`sniperVictimSite`? does it shoot from a rooftop and leave a window/trajectory clue?) to see whether the street MO
gives a coherent, solvable sniper case for an arbitrary motivated pair. That decides the fix: route motivated snipers
to the street MO (+ a `siteVantage`-viable site check), and only use VoyeurSniper when `homeVantage=FOUND`.

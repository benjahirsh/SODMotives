# Vanilla sniper (ExCopSniper) pipeline, decoded from IL2CPP

**Written:** 2026-09-25 · Branch `sniper-wip` · Source: Cpp2IL `2022.1.0-pre-release.21` ISIL dump of the
Aug-23 `GameAssembly.dll` (unchanged since). Three methods decoded from ISIL and cross-checked against
in-game logs (the Augustin#165 -> Jayla#166 cohabiting case). Confidence is called out per claim; the
structural flow is high-confidence, some timer constants and field names are inferred.

Goal: understand exactly how the base game runs a sniper murder so our motivated override can mimic it
instead of fighting it. This supersedes the guesswork in the earlier "probe-based" attempts.

## TL;DR (the load-bearing findings)

1. **Vanilla guarantees sniper viability at SELECTION time, not at run time.** `PickNewVictim` chooses the
   victim AND fills `sniperVictimSite` using the vantage solver, so a shootable exposed site with a reachable
   nest already exists before the case ever enters the state machine. Our override swaps in a motive-chosen
   victim and sets `victimSite = null`, discarding that guarantee. That is the root cause of every stuck case.
2. **ExCop's target-site pool is the victim's COMMUTE PATH + STREETS + PUBLIC locations. The home is never an
   ExCop target site.** So a homebody (or a victim with no vantage-viable exposed routine) has no ExCop site,
   and the case cannot progress no matter how long it waits.
3. **There is no give-up timeout for an un-executed sniper.** The game waits indefinitely; the only cancel is
   post-kill cleanup. Our 12h patience cap was the only thing ending the stuck case. (Log-confirmed:
   `CancelCurrentMurder()` was called exactly once, by us.)
4. **The vantage solver is NOT the fire oracle, and it is randomized.** `Toolbox.TryGetSniperVantagePoint`
   runs only at setup, scores windows with `Random.Range`, and is geometry-only over home/work. The actual
   shot is a live `Physics.RaycastAll` in `Update`. So our probe was querying a nondeterministic setup-time
   heuristic over the wrong sites; that is why it read NONE for cases the game snipes fine.
5. **A clean sniper kill leaves real, findable forensics** (shell casing at the nest owned by the killer,
   broken window, bullet decals, gunshot audio for witness leads, an entry wound linked to the case).

## The pipeline, end to end

### 1. Selection — `MurderController.PickNewVictim` (MurderController.txt 24871)
Picks the sniper victim and, using the vantage solver, pre-computes a viable site:
- Calls the sniper-centric `TryGetSniperVantagePoint(killer, site)` over the victim's fixed frequented sites
  (home `+0x298`, job/workplace `+0x2E0` chain) to confirm a vantage exists, and the location-centric
  overload with `killer.home` as the nest for the voyeur-from-home case.
- The chosen site is passed into `ExecuteNewMurder(..., site)` and stored on the `Murder` (`sniperVictimSite`
  `+0x170`). **Vanilla never starts a sniper case without a solver-verified site.** (High confidence that the
  solver runs here; medium on the exact victim-viability rejection path, which we did not fully decode.)

### 2. State machine — `MurderController.Update` (MurderController.txt 30720)
`Murder.state` is at `+0x34`. Flow: `3 waitForLocation -> 4 travellingTo -> 5 executing -> 6 post -> 7 unsolved`.

- **State 3 (waitForLocation):** killer idles (`murder.location == null`, no destination). Each tick:
  - `if (IsValidLocation(victim.currentGameLocation))` -> `SetMurderLocation(victim.currentGameLocation)` +
    `SetMurderState(4)`. This LOCK is what gives the killer a place to travel to.
  - else if it has waited too long AND `sniperVictimSite != null`: HERD the victim, i.e. create an AI GoTo
    goal walking the victim to `sniperVictimSite` (narration "Waiting too long! Creating GoTo VictimSite
    routine for victim").
  - KEY nuance: `IsValidLocation` is the allow-flag validator (Murder nested 7172-7377). For ExCopSniper
    every flag (home/work/public/streets) is true, so **almost any location the victim stands in is "valid"
    and gets locked immediately, and the herd branch is only reached when the victim is somewhere NOT
    allowed.** So for ExCop the game mostly relies on the victim's NATURAL routine to carry them to an
    exposed spot; the herd is a backstop, not the main driver. (This is why exposure/routine is everything.)
- **State 4 (travellingTo):** killer travels to a rooftop/vantage over the locked location.
  - **Fire gate (all required):** victim at a valid location; `victim.currentGameLocation == sniperVictimSite`;
    victim not dead/fleeing; aim cooldown `sniperShotDelay` (`MurderController+0xF8`) elapsed; and a live
    `Physics.RaycastAll` from the killer's gun to the victim's body is clear (passes through glass/window
    layers; the first solid hit must be the victim, "Sniper shot should hit:" vs "Sniper would hit wrong
    victim!"). Only then `ExecuteSniperShot`.
  - **Re-target ("waited too long, change site"):** past a dwell threshold, calls `TryPickNewVictimSite`
    (see 3). On success: set the new `sniperVictimSite`, `SetMurderLocation(null)`, `SetMurderState(3)` (the
    observed workplace -> null -> street churn). On FAILURE: force-kill, "Sniper unable to find a new victim
    site; execute fake murder" -> `ExecuteSniperShot(forceKill: true)`.
  - If the killer reaches the vantage but the victim is not there: "Cancelled murder because victim is not at
    X when killer arrived at the vantage point" -> null the location -> back to state 3.
- **The only give-up:** `CancelCurrentMurder` lives in state 7 (unsolved), i.e. AFTER the kill, when the
  murderer has left the scene. **No timeout cancels an un-executed sniper.** (High confidence: it is the sole
  `CancelCurrentMurder` in `Update`.)

### 3. Site re-picker — `Murder.TryPickNewVictimSite(out site)` (Murder nested 18313)
Called only from `Update` (state 4 re-target). For ExCopSniper (`requiresSniperVantageAtHome == false`) it
builds candidates from three disjoint, MO-gated sources, each with a fresh dedup set:
- **Block A - commute path (gated by `allowWork` 0x9B):** the victim's HOME -> WORKPLACE route.
  `FindSafeTeleport` both ends, `PathFinder.GetPath(fromHome, toWork, victim, null)`, iterate the path's
  nodes -> each node's game-location is a candidate.
- **Block B - streets (gated by `allowStreets` 0x9D):** a city street-location list.
- **Block C - public (gated by `allowPublic` 0x9C):** the `CityData` public-location directory (filtered by
  an accessibility flag).
- Per candidate: skip if seen; accept as new best iff `TryGetSniperVantagePoint(murderer, candidate)` returns
  true AND out `NewWall != null` AND `score >= bestScore` AND `candidate != current sniperVictimSite`.
- **Selection = deterministic max vantage score** (no randomness in this method itself; the solver it calls is
  randomized). Order A -> B -> C.
- **The home is NOT in the ExCop pool** (only as the incidental first commute node, which the
  `!= sniperVictimSite` guard rejects once the site already equals home).
- **Returns true only if it found a site DIFFERENT from the current one; false = "nothing new/valid, keep the
  current site."** It does not mutate the case; `Update` does the assignment.
- VoyeurSniper path (`requiresSniperVantageAtHome == true`): no pool; computes `possibleTargetSites` from
  `victim.home` via the location-centric overload requiring a line to `murderer.home`, then picks
  `victim.home` (if `allowHome`) or the workplace.

### 4. Vantage solver — `Toolbox.TryGetSniperVantagePoint` (Toolbox.txt 96191 / 100272)
- **Sniper-centric** `(Human sniper, NewGameLocation site, out NewWall, out float score)`: collects buildings
  around the site (buildings owning the site's walls, or the building facing each of the site's windows via
  `GetFacingBuildingFromWindow`), then `ScanBuildingForSniperVantagePoints` scores each window: `score =
  Random.Range(0, C9) - C7 + nodeWeight`, plus a bonus per successful `DataRaycastController.NodeRaycast` LOS
  from the window to the site's nodes. Accept if `score > 0` and it beats the best. **Randomized, geometry
  only. No victim read, no time read, no reachability/GetPath gate inside the solver.**
- **Location-centric** `(NewGameLocation vantageLocation, ..., out List<NewGameLocation> possibleTargetSites,
  NewGameLocation requiredTargetSite)`: the inverse ("from this nest, what can it see"). Used with
  `killer.home` as the nest for voyeur viability.
- Neither overload is called from `Update`. They are setup-time only.

### 5. Shot + forensics — `MurderController.ExecuteSniperShot` (MurderController.txt 44019)
Live raycast + evidence spawn (this is what makes a kill solvable):
- Gunshot world audio at the killer/nest (source of "heard a shot" witness leads).
- A shell/bullet interactable dropped AT THE NEST, owned by the killer (`InteractableCreator.CreateWorld
  Interactable`, marked trash, physicalized) - the findable rooftop evidence.
- Bullet trajectory raycasts -> broken window / bullet hole (`BreakableWindowController.AddBulletHole`) and
  surface impact decals (`Toolbox.CreateBulletSurfaceContactFX`).
- Entry wound on the victim (`CreateWoundClosestToPoint`) linked to the current Murder record.
- NOTE: no explicit `SetDead`/health write is inside this method; the surrounding pipeline applies death. So
  a live solvability play-through is still worth doing, but the physical clue set is all present.

## Field offsets used (for the mod)
- `Murder`: state `+0x34`, dwellTimer `+0x3C` (float, inferred), meetTime `+0x98`, preset `+0xF0`, mo `+0xF8`,
  murderer `+0x100`, victim `+0x108`, location `+0x118`, sniperVictimSite `+0x170`.
- `MurderController`: sniperVictimSites (List) `+0xD0`, sniperShotDelay `+0xF8`.
- `MurderMO`: requiresSniperVantageAtHome `+0x88`, allowAnywhere `+0x99`, allowHome `+0x9A`, allowWork `+0x9B`,
  allowPublic `+0x9C`, allowStreets `+0x9D`, allowDen `+0x9E`. (ExCop = home/work/public/streets all true;
  Voyeur = requiresSniperVantageAtHome true.)
- `Human`: home `+0x298`, residence `+0x2A0`, den `+0x2A8`, job `+0x2E0`, currentGameLocation `+0x168`
  (inferred).

## Why our motivated cases fail (mapped to the above)
- We swap the pair and set `victimSite = null`, so the case enters the machine with `sniperVictimSite == null`
  and NO selection-time viability guarantee.
- The victim was home; `IsValidLocation(home)` is true for ExCop, so state 3 locked `murder.location = home`.
- The killer was sent to a vantage over the home; none exists -> he idled (log: constant 87.6m).
- `sniperVictimSite == null` -> no herd target. The victim (a homebody in that window) never left, so the
  location never went invalid to bounce it into re-targeting.
- `TryPickNewVictimSite`'s ExCop pool is commute/street/public, and for this killer none yielded a passing
  vantage, so it kept returning false -> the site never moved off the home. Deadlock. Our patience cap ended
  it. (Neither the re-target nor the force-kill fired within 12h; the dwell threshold for the re-pick/
  force-kill is longer than 12h or gated in a way we have not pinned - UNCERTAIN.)

## Implications for mimicking vanilla
1. **Do NOT pin an arbitrary site, and do NOT use `TryGetSniperVantagePoint(killer, site)` as a go/no-go
   oracle** (randomized, setup-time, wrong sites). Both are already stripped; keep them stripped.
2. **The fix is at SELECTION, mirroring vanilla:** only motivate a sniper for a victim who has a vantage-viable
   exposed routine and a candidate killer with a reachable nest, the same guarantee `PickNewVictim` provides.
   Concretely that means running the ExCop pool scan (commute path + streets + public, via the solver, with a
   few retries to absorb the randomness) for a candidate killer and only motivating if it yields a site; else
   leave the sniper vanilla. This is a selection filter, not a pin: once a viable pair is chosen, we defer
   site/vantage/shot to the game exactly as now, and the state machine runs vanilla-identically.
3. **Homebodies and no-exposed-routine victims are intrinsically un-snipeable by ExCop** (state 3 locks the
   home, no vantage, no herd). Vanilla avoids them by selection; we must too. There is no seeding trick around
   this.
4. **Cohabiting is a red herring** for the mechanism: the failure is exposure/vantage viability, not the
   shared home per se. (A cohabiting victim who commutes past a snipeable street would work.)
5. **Force-kill fallback exists but is undesirable** (it is the "fake murder" no-LOS resolution that produces
   the nonsensical kill with weak forensics). Prefer a clean selection filter + vanilla fallback over relying
   on it.

## Open uncertainties (flagged, not asserted)
- The dwell-timer semantics (`Murder+0x3C`) and the re-pick / force-kill threshold constants.
- Exact victim-viability rejection inside `PickNewVictim` (does vanilla hard-reject a no-site victim, or just
  rarely pick them?). Would confirm whether a strict selection filter is truly what vanilla does.
- `sniperShotDelay` count direction; `NodeAccess.type == 5` = window (inferred); a couple of unresolved
  getters in the street/public blocks; whether death is inside `CreateWoundClosestToPoint` or the caller.
- Whether the herd branch can ever fire for an ExCop victim in practice (needs the victim at a non-allowed
  location, which is rare for all-flags-true ExCop).

# Sniper nest derivation + LOS scoring, decoded from IL2CPP

**Written:** 2026-09-25 (late) - branch `sniper-wip`. Source: Cpp2IL ISIL dump of the Aug-23 `GameAssembly.dll`
(the same dump used by `sniper-vanilla-excop-decode.md`). Two parallel decode passes (5 + 3 agents) cross-checked
against the in-game logs (the Brooks Street / Lovelace View stall, Emilia Oliver#129 -> Jace Morgan#189). This doc
answers the one question the whole redesign hinged on: can the mod control WHICH WINDOW the sniper shoots from, and
how do we score windows by real line of sight ourselves.

## TL;DR (the load-bearing findings)

1. **There is NO settable nest field.** The killer's nest (the window/wall he walks to and fires from) is
   RE-DERIVED every time his sniper AI action activates (`NewAIAction.OnActivate`), by calling
   `Toolbox.TryGetSniperVantagePoint(...)` and stamping the returned `NewWall`'s node into the action's transient
   movement-destination (`NewAIAction + 0x40`). Poking a node into the goal/action is clobbered on the next
   activate. So a redesign that assumes a writable nest coordinate is wrong.

2. **The window is controlled through the solver's inputs + a patch:**
   - Coarse levers (deterministic-ish): `Murder.sniperVictimSite` (+0x170) and `MurderMO.requiresSniperVantageAtHome`
     (+0x88) decide the site and whether the solver searches the killer's home (voyeur) or any reachable building
     (ExCop). Given a site whose geometry has a single clear answer, the solver's wall is stable.
   - **The authoritative lever: a Harmony patch on `Toolbox.TryGetSniperVantagePoint`.** It is the single choke
     point called at BOTH selection time and fire time; its `out NewWall` becomes the killer's nest node. A postfix
     that rewrites `out vantagePoint` (+ `out vantageScore`) for our case makes the game route the killer to OUR
     window, with no re-derivation fight and no random reshuffle.

3. **The solver's window pick is RANDOM near ties.** Per window the score is
   `UnityEngine.Random.Range(0, C_rand) + gameLocation.weight - C_los + C_los * (site nodes with clear LOS to the
   window)`. It is plain `UnityEngine.Random` (NOT the seeded `Toolbox.GetPsuedoRandomNumber`), re-rolled per window
   per call. So repeated probes of the same site legitimately return different "best" windows (this is exactly why
   our single probe grabbed Lovelace 1st-floor while the game's later scan called 3rd-floor best, and why ExCop keeps
   snapping to the one dominant rooftop whose deterministic term is far above the pack). `C_rand @ 0x183A57DC8`,
   `C_los @ 0x183A58004` are `.rdata` floats; read them at runtime if we want the exact values.

4. **The vantage SCORER and the live FIRE GATE use different LOS systems.** The scorer uses the node-graph
   `DataRaycastController.NodeRaycast` (windows implicitly transparent). The live shot uses `Physics.RaycastAll`
   from `killer.transform.position` to the victim body anchor, allowing only `RainWindowGlass` through. A window that
   scores well on NodeRaycast can still fail the physics gate ("Sniper has collider blocking LoS" forever). To make
   the shot ACTUALLY fire we should validate our chosen window with a physics ray that mirrors the fire gate, not
   just NodeRaycast.

5. **A pinned site is honored only until the game's own dwell timeout, then CLOBBERED.** State 4's re-target calls
   `Murder.TryPickNewVictimSite` and overwrites `sniperVictimSite` (+0x170) with the global-best rooftop once the
   killer has waited too long without firing. Our pin was doubly doomed: the killer sat at a no-LOS window, never
   fired, timed out, and the game replaced our local site with Mingo. This is the "more Mingo missions" mechanism.

6. **The herd (walk the victim to the site) only fires when the victim's current location is INVALID.** State 3's
   "Creating GoTo VictimSite routine" branch requires `IsValidLocation(victim.currentGameLocation) == false` AND
   `sniperVictimSite != null` AND a wait timer elapsed. For ExCop every location is valid, so the herd almost never
   fires for a pinned street; the victim never arrives -> `V_AT_SITE=False` forever -> stall. To bring the victim to
   a chosen site we must either pick a site on the victim's natural routine OR create the walk goal ourselves.

## The pipeline (site -> nest -> shot)

### Selection (before our override runs)
- `MurderController.PickNewVictim` scores candidate sites with a scratch `Dictionary<NewGameLocation,
  CachedSniperLocation>` (selection-only, not persisted), calling `TryGetSniperVantagePoint` per candidate, and
  commits only the winning SITE to `Murder.sniperVictimSite` (log "Suggested sniper victim site of:"). The winning
  wall is NOT persisted.
- `MurderController.PickNewMurderer` (line 6866) uses the location-centric overload with `killer.home` as the
  vantage to guarantee the killer can snipe at least one site (log "Unable to find any valid sites for"). For the
  voyeur MO the nest building is effectively pinned to `killer.home` before PickNewVictim runs.

### Fire time - `NewAIAction.OnActivate` (the nest is (re)computed here every activation)
- Branch on `mo.requiresSniperVantageAtHome` (+0x88):
  - VOYEUR (!= 0): `TryGetSniperVantagePoint(killer.home, out wall, out score, out possibleTargetSites,
    requiredTargetSite = sniperVictimSite)` (overload 2).
  - EXCOP (== 0): `TryGetSniperVantagePoint(killer, requiredTargetSite = sniperVictimSite, out wall, out score,
    accessCheckList = null)` (overload 1).
- On success: `action+0x40 = wall.node` (the killer's walk-to node). On failure: "unable to find a valid sniper
  vantage point:" and the action aborts. **This is our postfix injection point: override `out wall`.**

### Fire gate - `MurderController.Update` state 4 (unchanged from the ExCop decode)
- Live `Physics.RaycastAll` from `killer.transform.position` (Murder.murderer +0x100 -> +0x70) to the victim body
  anchor (`CitizenOutfitController.GetBodyAnchor`, body-part index cycled). Only `RainWindowGlass` may block; first
  solid hit must resolve to the victim (else "Sniper would hit wrong victim!" / "collider blocking LoS"). Fires only
  when `victim.currentGameLocation == sniperVictimSite` exactly.

## Callable interop (the shopping list for the redesign)

LOS / raycast:
- `DataRaycastController.Instance.NodeRaycast(NewNode fromNode, NewNode toNode, out List<NodeRaycastHit> path,
  NewDoor startingDoor = null, bool debugMode = false) -> bool` - node-graph LOS; **true = clear** (windows
  implicitly transparent); the game's own scorer ignores the out path and branches on the bool. This is the exact
  primitive to score a window by LOS to the victim's stand node.
- `DataRaycastController.Instance.EntranceRaycast(NodeAccess from, NodeAccess to, out path, debug) -> bool` -
  forwards to NodeRaycast; only needed when both ends are window/door accesses.

Ready-made vantage scoring (the game already implements "score windows by LOS to a required site"):
- `Toolbox.Instance.TryGetSniperVantagePoint(Human sniper, NewGameLocation requiredTargetSite, out NewWall
  vantagePoint, out float vantageScore, List<NewNode.NodeAccess> accessCheckList = null) -> bool` (overload 1,
  ExCop). Extra nodes in `accessCheckList` add to the score if the window can see them.
- `Toolbox.Instance.TryGetSniperVantagePoint(NewGameLocation vantageLocation, out NewWall vantagePoint, out float
  vantageScore, out List<NewGameLocation> possibleTargetSites, NewGameLocation requiredTargetSite = null) -> bool`
  (overload 2, voyeur/location-centric): given a vantage (e.g. killer.home) returns all sites it overlooks. This is
  what `SniperVoyeurViable` should lean on.
- `Toolbox.Instance.ScanBuildingForSniperVantagePoints(Human sniper, NewBuilding building, NewGameLocation
  requiredTargetSite, out NewWall vantagePoint, out float vantageScore, ref List<NewNode.NodeAccess>
  accessCheckList) -> bool` - best window of ONE building for the site; call per candidate building to rank nests.
  (Still returns a single best per call, and includes the random term.)
- `Toolbox.GetFacingBuildingFromWindow(NewNode.NodeAccess windowAccess, out Vector3 windowDir) -> NewBuilding`
  (static) - the building across the street from a window; core primitive to enumerate candidate nests ourselves.

Enumeration / geometry:
- `NewGameLocation.entrances` = `List<NewNode.NodeAccess>` at +0xC8; filter `accessType (+0x30) == 5` for windows.
- `NewGameLocation.nodes` = `List<NewNode>` at +0x78; candidate stand-node pool for a site.
- `NewNode.NodeAccess`: wall +0x28, accessType +0x30 (**Window == 5**, confirmed), fromNode +0x38, toNode +0x40,
  walkable +0x48, worldAccessPoint (Vector3) +0x4C, doorway (NewDoor) +0x20.
- `NewNode.position` (Vector3) +0x18; `NewNode.floorHeight` (int) +0x88 (discrete floor index - use to tell a
  1st-floor window from a 3rd-floor window); `NewNode.gameLocation`; `NewWall.node` +0x30.
- `StreetController.GetDestinationNode() -> NewNode` - a representative walkable node of a street (the natural "where
  the victim ends up" node for a street site). `StreetController.GetNeighboringStreets()`.
- `NewAIController.CreateNewGoal(AIGoalPreset preset, float triggerTime, float duration, NewNode passedNode = null,
  Interactable passedInteractable = null, NewGameLocation passedGameLocation = null, SocialGroup passedGroup = null,
  Murder murderRef = null, int passedVar = -2) -> NewAIGoal` - to walk a human (e.g. herd the victim to the site).
- `PathFinder.Instance.GetPath(NewNode from, NewNode to, Human, null)` + `NewPath.GetNodeAhead(i)` - enumerate the
  victim's commute / stand nodes to score against (already used in `MurderWatchdog.FirstViableLocalSite`).

Fields already in the mod's interop and reused: `Murder.sniperVictimSite` +0x170 (settable, honored until dwell
timeout), `Murder.mo` +0xF8, `MurderMO.requiresSniperVantageAtHome` +0x88, `Human.home` +0x298,
`victim.job.employer.placeOfBusiness`, `Human.FindSafeTeleport(loc,bool,bool)`.

Uncertain / read-at-runtime: the two solver constants (`C_rand @ 0x183A57DC8`, `C_los @ 0x183A58004`); the exact
game dwell-timeout threshold vs our `SniperPinStallHours`; several `[?off]` sub-offsets flagged by the decoders
(NodeAccess sub-fields and room offsets are the most-used and cross-confirmed; treat rarer ones as inferred).

## Implications for the redesign (see the handover for the chosen plan)
- The right lever is a postfix on `TryGetSniperVantagePoint` that, for our active motivated sniper case, returns a
  wall WE scored by true LOS to the victim's actual stand node - not the game's random single-best. Scope the
  postfix tightly to our killer/case so vanilla snipers and selection scoring are untouched.
- Score windows by suitability to the victim's site (proximity + LOS to the specific stand node), avoiding the
  random term that produces the single-dominant-rooftop monoculture; prefer a nearby overlooking public window, and
  validate the winner with a physics ray so the live fire gate actually passes.
- Bring the victim to the site: prefer a site on the victim's natural routine, or create the walk goal ourselves via
  `CreateNewGoal`, since the game's herd will not fire for a "valid" pinned street.
- Keep `sniperVictimSite` asserted so the dwell-timeout re-target cannot drift it to Mingo before the shot lands.

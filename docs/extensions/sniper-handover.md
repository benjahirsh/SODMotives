# Motivated snipers — HANDOVER / RESUME (2026-09-24)

Focused resume point for the motivated-sniper feature. Full play-by-play (playtests 1-7 + vanilla decode) is in
`docs/extensions/sniper-recon.md` (bottom, dated sections). This file = the current state + what to do next.

**Code-level vanilla decode (2026-09-25): `docs/extensions/sniper-vanilla-excop-decode.md`** — the full ExCop
pipeline reverse-engineered from IL2CPP (PickNewVictim, Update state machine, TryPickNewVictimSite, the vantage
solver, ExecuteSniperShot). Bottom line: vanilla guarantees a vantage-viable EXPOSED site at SELECTION time;
ExCop's target pool is commute/street/public (never the home); there is no give-up timeout; the fire is a live
raycast, not the (randomized, setup-time) solver our probe used. The mimic fix is a selection-time viability
filter, not a site pin. Read that doc before touching the override.

## ⭐⭐ CURRENT STATE (2026-09-25 latest — RESUME HERE) — BETTER NEST CALC built, awaiting playtest, on `sniper-wip` LOCAL/uncommitted
Full IL2CPP decode (2 workflows) is written up in `docs/extensions/sniper-nest-decode.md`. Load that first. Key
confirmed facts:
- The sniper NEST is NOT a settable field: the game re-derives it every `NewAIAction.OnActivate` by calling
  `Toolbox.TryGetSniperVantagePoint`, whose `out NewWall` becomes the node the killer walks to and fires from. We
  control it via a Harmony POSTFIX on that solver (scoped to our killer).
- That solver scores windows with a `UnityEngine.Random` term, so it can hand the killer a wrong-facing / no-LOS
  window and its choice is not stable; and its single global-best is almost always the far dominant rooftop, which
  is why ExCop kept funnelling to one street.
- A pinned `sniperVictimSite` is honoured only until the game's state-4 dwell timeout, then `TryPickNewVictimSite`
  CLOBBERS it to the global-best rooftop (the "more Mingo" mechanism). `SniperPinStallHours` (8h) releases a stalled
  pin ourselves before that.
- The state-3 HERD (walk the victim to the site) only fires when `IsValidLocation(victim.currentGameLocation)` is
  false, so targeting a STATIC location the victim already occupies (home/workplace) is more reliable than a street.

**PLAYTEST of the re-sample window-fix (2026-09-25 latest) RESULT:** it worked mechanically (no force-kills; 21
clean LOS-confirmed shots; Brooks Street was a clean LOCAL kill) but the user judged it no better. Two reasons,
both confirmed in the log: (1) re-sampling the game solver cannot surface local windows -- for a site with one
overlooking building the solver returns the SAME window every call (Lovelace 1st-floor 40+ times), so we could not
do better than that window; (2) most ExCop cases still fell to the game default (Chandani -- this city's per-victim
best rooftop, which shifts by victim, not a fixed city spot). Chandani = the new Mingo.

**USER'S DECISION (authoritative, supersedes the re-sample approach):** sniping a moving commuter is too hard to
line up. Use VANILLA'S mechanism -- pin the victim to a STATIC location they reliably occupy (HOME first, then
WORKPLACE, then a street) and let the game send the shooter to solve it. The mod's job is a BETTER NEST CALCULATION
when the ExCop clause runs: find a public, accessible rooftop/window that genuinely overlooks the victim's
home/workplace, with real raycast LOS. Do NOT chase commute sniping. (Interesting PAIRS is the eventual goal; the
site mechanism stays vanilla's reliable pin-and-solve.)

**WHAT'S BUILT NOW (the better nest calculation; builds 0-warn, DEPLOYED):**
- `MurderWatchdog.FindBestPublicNest(killer, targetLoc, targetNode, out wall, out dist, out public)` = the real
  enumeration. Candidates: (a) the buildings FACING targetLoc's own windows (`Toolbox.GetFacingBuildingFromWindow`
  per window entrance where `accessType==5`), each scored by the game's PER-BUILDING window scorer
  (`Toolbox.ScanBuildingForSniperVantagePoints`, sampled 3x past its random term) -- these are the close local
  windows the global solver hides; plus (b) the game's global-best vantage (a genuine rooftop). Every candidate node
  is LOS-checked with `DataRaycastController.NodeRaycast` to the victim's node; pick PUBLIC (nest != killer home)
  then closest within `SniperMaxNestMeters` (55m). Reentrancy-guarded (`_inNestScan`).
- `FirstViableLocalSite(killer, victim, out nestWall)` now tries HOME then WORKPLACE (user's order; commute
  targeting dropped), via FindBestPublicNest; returns the site to pin + the chosen nest wall.
- Seeding pins that site (`sniperVictimSite`) and PRE-WARMS the postfix cache (`_moNestWall`) so the killer travels
  to OUR nest. The postfix (`Patch_SniperVantage_ForceLosNest`; `TryForceLosNest` now just returns the cached /
  on-demand FindBestPublicNest wall) forces that nest at fire time. Falls to `SeedSniperDefault` (game default
  rooftop) only when no local public nest overlooks home OR workplace. Toggle `[Troubleshooting] SniperForceLosNest`
  (default on) gates the postfix.

Effect: ExCop tries to snipe the victim AT HOME (then workplace) from a verified public nest across the street
(static target = reliable, and it lands LOCAL nests the old global-solver probe missed = the fix for "always
Mingo"). NOTE: ExCop shooting a victim at HOME from a public nest is a NEW pattern (vanilla ExCop never targets
home, but it honours a set site and IsValidLocation accepts home for ExCop) -- confirm in-game that it fires.

**PLAYTEST THIS BUILD:** `[Motive Mix] MotivatedSniperShare=1`, `[Debug] EnableDebugKeys=true`, sniper-only
sandbox, NORMAL speed. In BepInEx watch for `[SODMotives][sniper] LOCAL nest for <home/work>: <nest> (<n>m,
public/accessible, verified line of sight)` + `pinned LOCAL site` (good: local nest found) vs `no public nest with
a clear shot overlooks <loc>` + `seeded ... game default` (fell through to the rooftop). Goal: MORE local pins,
FEWER game defaults, and the pinned cases should FIRE (Player.log reaches executing/post at the home/workplace). I
read the logs myself. If home-as-site does not fire in the game's machinery, that is the next thing to fix.

## ⭐ CURRENT STATE (2026-09-25 late) — motivated snipers WORK + SOLVABLE, on `sniper-wip`, LOCAL/uncommitted
Extensively playtested across 2 cities (Rebeka's city + Charlotte Heights). Motivated snipers fire and are
SOLVABLE. Build `dotnet build -c Release` (deploys when the game is CLOSED). Config: `[Motive Mix]
MotivatedSniperShare` (default 0; set 1 to test), `[Debug] EnableDebugKeys=true`, normal speed. F3 forces one.

**How it works now (all in `Plugin.cs` override + `MurderWatchdog.cs`):**
1. SELECTION (`Plugin.cs` sniper branch): swap in the motivated pair, PREFERRING (soft `preferKiller`, no motive
   cost) a killer whose HOME overlooks the victim so the case can be VoyeurSniper; else ExCopSniper. `SniperVoyeurViable`
   in MurderWatchdog. `MurderSelector.TryPickVictimCentric` gained a soft `preferKiller` param + LastPreferViableCount
   diagnostic.
2. SEEDING (`MurderWatchdog.Tick`, `_sniperSeeded` once-guard): for ExCop, `FirstViableLocalSite` scans the victim's
   WORKPLACE + home->work COMMUTE streets (via `PathFinder.Instance.GetPath` + `PathData.GetNodeAhead`) + HOME,
   PREFERRING a PUBLIC nest (nest gameLocation != killer.home) over a killer-home-window nest, and REJECTING a nest
   farther than `[Troubleshooting] SniperMaxNestMeters` (default 55m) from the site (kills the solver's cross-city
   false-positives, e.g. "Mingo rooftop score 225 sees Medallion Corp 2 blocks away"). If a local site is found ->
   PIN it (`murder.sniperVictimSite = site`; a SET site is HONOURED by the game, no re-assert needed). Else ->
   `SeedSniperDefault` = the game's own `Murder.TryPickNewVictimSite` (its best rooftop, e.g. Mingo/Karlsson), with a
   non-home anchor / revert-to-vanilla fallback so it can't strand.
3. STALL-RELEASE: a pin that hasn't fired within `[Troubleshooting] SniperPinStallHours` (default 8h) releases to the
   game default (`_sniperPin`/`_sniperPinSince`). Voyeur cases use `SeedSniperDefault` directly (no pin).
4. NO patience net (removed), NO force-kill reliance for the common path.

**Key findings this session (all confirmed):**
- The single-dominant-sniper-street (Mingo / Karlsson Blvd) is 100% VANILLA + city-universal (vanilla ExCop Thomas
  case went home->null->Mingo over a game-day; and Steam community threads report the SAME Karlsson Blvd single-spot
  behaviour, "killer only lived there 1 of 4 times" = it's the vantage geometry, not the killer). Our seed-Mingo is
  faithful + faster (skips the game's game-day search).
- VOYEUR supply is ~2% (only ~1 in ~45 motivated suspects has killer-home LOS to the victim; motives cluster around
  NEIGHBOURS/close-proximity NPCs, the antithesis of sniper LOS). So voyeur is real but rare.
- Local PUBLIC nests are GEOMETRICALLY SCARCE (few streets have an overlooking public building; that's why everyone
  funnels to Mingo/Karlsson). The commute scan does find some (Beta Smog Group, Sherman Blvd, Tawny Corp fired +
  SOLVED). But many "local pins" resolve to a KILLER-HOME-window nest (F9 reads them as voyeur-style), not a public
  nest — user wants more PUBLIC-nest variety specifically.
- SOLVABILITY: ExCop public-nest cases SOLVE cleanly (Case: `Resolve()` needs "evidence placing the killer at a crime
  scene" + "where killer lives" + "murder weapon"; a public rooftop nest is a clean crime scene). One voyeur case
  wouldn't resolve once — traced to a resolve-UI spam-race (`RevealResolveController.OnDestroy` error), NOT a voyeur
  flaw (voyeur cases solved fine on the calm retry).

**NEXT (in priority order):**
1. USER'S IDEA (best next refinement): score/pick nests by SUITABILITY TO THE VICTIM'S SITE (proximity + LOS),
   not the game's global vantage score, so a near overlooking window beats the far Mingo rooftop. The distance gate
   (SniperMaxNestMeters) is a crude version. Fuller = enumerate candidate nest-buildings near the site ourselves +
   check LOS (the game solver only returns its single global-best nest). Once per case at selection, so cost is fine.
   This is how to raise the PUBLIC-nest hit rate (the current bottleneck).
2. Tune `SniperMaxNestMeters` (55m start) from the logged nest distances; measure public-nest vs killer-home-window
   vs Mingo-default hit rates.
3. PRE-SHIP CLEANUP: strip `[sniper-obs]`/`[sniper-live]`/`[mo-dump]` diagnostics (keep F9); set the ship default for
   `MotivatedSniperShare`; then MERGE `sniper-wip` -> `v2`, bump version, ship. (⚠️ NO Co-Authored-By trailers.)

## Status
- Branch **`sniper-wip`** (LOCAL, NOT pushed). Clean tree. Builds 0-warn, auto-deploys to the game's
  `BepInEx/plugins/SODMotives` when the game is CLOSED (`dotnet build -c Release`; restart game to load).
- Shipped already (branch `v2`, not this branch): 1.0.2 + motivated KIDNAPPINGS 1.1.0/1.1.1.
- Motivated snipers are the CURRENT feature. Not shipped, not merged to v2.

## What the code does now (current design — "mimic vanilla: seed + voyeur-test + force-kill") ✅ VALIDATED (ExCop)
Rewritten 2026-09-25 after the full IL2CPP decode (`docs/extensions/sniper-vanilla-excop-decode.md`). The old bug
was `victimSite = null`: vanilla ALWAYS enters the state machine with a solver-chosen site (PickNewVictim), and a
null site strands the case on the victim's un-snipeable home. Fix:
1. `Patch_ExecuteNewMurder_Override` (Plugin.cs) swaps in the motivated pair, then picks the MO by VANTAGE FLAVOUR:
   `MurderWatchdog.SniperVoyeurViable(killer,victim)` = NOT cohabiting AND the killer's OWN home has LOS (game's
   location-centric solver, 3 retries for its randomness) to the victim's home/work -> **VoyeurSniper** (shoot the
   victim at home/work from the killer's window; works even for a homebody victim). Else -> **ExCopSniper**
   (street/rooftop). `victimSite` stays null (seeded next).
2. `MurderWatchdog.Tick` SEEDS the site once per case (via `_sniperSeeded`): `murder.sniperVictimSite =`
   `murder.TryPickNewVictimSite(out picked)` (the GAME'S OWN picker, MO-aware: voyeur->home/work,
   ExCop->commute/street/public; up to 4 retries), else the victim's WORKPLACE (a force-kill anchor the victim is
   away from often enough to reach SITECHECK), else `victim.home` (last resort). Then it DEFERS everything (re-pick,
   herd, the live-raycast shot, AND the game's own force-kill "fake murder" fallback) to the game.
3. NO patience net, NO pin: vanilla has no timeout for an un-executed sniper (only a post-kill cleanup cancel and a
   rare SITECHECK force-kill). Removed `ShouldBlockSniperCancel`, `Patch_BlockSniperCancel`, `SniperPatienceHours`,
   `SniperSince`, and the dead `TryPickSniperSite`.

**PLAYTEST 2026-09-25 (F3, MotivatedSniperShare=1): WORKS END TO END.** Remas Said#231 -> Hiroto Ono#283,
`[Infidelity]` (killer's lover Koen is the victim's partner). Voyeur not viable -> ExCopSniper; watchdog seeded
`sniperVictimSite = Mingo Street` via the game's own picker (interop field-WRITE confirmed to take). State ran
`waitForLocation -> travellingTo(Mingo Street) -> executing -> post -> escaping -> unsolved` = a clean kill. Player.log:
a REAL sniper shot ("Best sniper for vantage point over Mingo Street in The Fathoms Zone Indigo ... Rooftop: 225",
picked over the 101 rooftop), body on the street, witnesses + enforcers + a proper case, affair-email clue injected.
NOTE: Mingo Street + those exact rooftops (225 vs 101) is the SAME site that oscillated forever under the OLD PIN
approach; seed-and-defer made it commit and fire on the first try. `[sniper-obs] NO EDGE (strangers)` is misleading
(the motive is a real affair triangle, just not a direct social edge).

STILL TO CONFIRM (future runs): a VoyeurSniper case (shot from the killer's window), the FORCE-KILL path on a
genuinely un-snipeable pair (how prompt is it?), end-to-end SOLVABILITY (play to an arrest), and hit rate.

**Adversarial review of the change (2026-09-25, workflow) + HARDENING applied.** The interop field-write
(`murder.sniperVictimSite = seed`) and the voyeur/ExCop MO logic were verified sound (matching the playtest); the
interop-crash / no-op / voyeur-hang worries were all REFUTED. The one real issue was the NON-VIABLE-pair path: no
recovery net + the old `seed = victim.home` fallback was a permanent soft-lock for a homebody (home==site never
reaches SITECHECK, so the game's force-kill never fires). FIXED (built clean, deployed): the fallback now seeds a
NON-HOME anchor (victim workplace, else killer home, else killer workplace) so SITECHECK is reachable and the game's
force-kill can fire; if NO non-home anchor exists at all (no viable site, victim AND killer both work-from-home/
unemployed, cohabiting = essentially never) it reverts that one case to vanilla so it cannot freeze the single-active
murder loop (viability-based, not a timer). Stale config/comment text that promised a `watchdog reverts to vanilla`
timer was corrected. RESIDUAL TRADEOFF to weigh with playtest data: the game's force-kill for a genuinely un-snipeable
pair is slow (dwell threshold unknown) and leaves weaker forensics (the "fake murder"); if that proves common or ugly,
revisit a selection-time viability filter or the regular-murder conversion (both considered; user chose force-kill).

Config: `[Motive Mix] MotivatedSniperShare` (default 0; set 1 to motivate natural sandbox snipers). `[Debug]
EnableDebugKeys` gates the diagnostics + F-keys. F3 forces a motivated sniper. F9 overlay shows SNIPE SITE / MO.

## The two biggest findings (why the design is what it is)
1. **My `Toolbox.TryGetSniperVantagePoint` probe is UNRELIABLE** — it returns NONE for vanilla snipers the game
   creates and RUNS (e.g. a killer shooting from their own apartment). So gating / pinning / MO-deciding on it was
   wrong; all of that was stripped. Do NOT reintroduce a vantage GATE based on that probe.
2. **The game's ExCopSniper DYNAMICALLY RE-TARGETS the site until the victim is exposed, then shoots from a rooftop**
   (decoded from a forced vanilla ExCopSniper, Thomas#229->Ashley#286, SAME building). `[flow] SetMurderLocation`
   went: victim's WORKPLACE (Zeta Labs) -> waits -> `<null>` (clears) -> `Mingo Street` (a street the victim walks) ->
   killer goes to a rooftop over it -> ExecuteSniperShot -> executing/post/unsolved (clean kill). The KILLER IDLES AT
   HOME during the wait; it only travels to the nest once the game locks a viable exposed site. The "long wait" =
   the game re-picking sites (`Murder.TryPickNewVictimSite`). It waits patiently, no give-up — SAME-BUILDING pairs
   ARE snipeable this way. **This is exactly why we must NOT pin the site** (our old pin froze it and broke the
   re-target). Vanilla snipers are motiveless STRANGERS, ~all VoyeurSniper; ExCopSniper is rare naturally (its
   murdererJobBoost favours Retired killers) — that's why we force ExCopSniper for cohabiting pairs.

## Dev/test tooling
- `[Motive Mix] MotivatedSniperShare = 1` + sandbox Sniper cases ON (+ Regular/Kidnapping OFF for sniper-only) +
  `[Debug] EnableDebugKeys = true`, NORMAL speed, `FastMurderCadence = OFF`. Watch F9 + read logs.
- **F3** = force a MOTIVATED sniper immediately (bypasses the scheduler).
- `[Troubleshooting] ForceVanillaSniperMO` = force the game's OWN next sniper to ExCopSniper (via the prefix ref;
  the game's debugMurderPreset/debugMO fields are NOT honored) WITHOUT swapping the pair -> observe a pure vanilla
  ExCopSniper. (Toggle on; needs Sniper cases ON.)
- Diagnostics (behind EnableDebugKeys, fire for VANILLA + ours): `[sniper-obs]` (once: pair, relationship, MO+flags,
  home/work + vantage probes, weapon), `[sniper-live]` (throttled: state, site, victim@/killer@ + distances,
  killShotNode, vantage probes, weapon), `[mo-dump]` (on game start: every sniper MO + flags + job-boosts).
- Logs: our lines -> BepInEx `LogOutput.log` (`C:\Program Files (x86)\Steam\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`);
  game's `Murder:` narration -> `%USERPROFILE%\AppData\LocalLow\ColePowered Games\Shadows of Doubt\Player.log`.
  READ THE LOGS YOURSELF (Grep/Bash); do not ask the user to relay lines. Log resets on relaunch.

## Sniper MOs (from [mo-dump], exactly 2)
- **ExCopSniper**: `requiresSniperVantageAtHome=False`, allow home/work/public/streets. STREET/rooftop; re-targets to
  a street where the victim is exposed. murdererJobBoost `[Retired]+20` (a SCORE boost, NOT a hard gate).
- **VoyeurSniper**: `requiresSniperVantageAtHome=True`, home/work only. Snipes the victim at home/work from a
  vantage. The game's usual pick.

## NEXT (do this on resume)
1. **USER PLAYTEST (the pending test):** run a MOTIVATED sniper (`MotivatedSniperShare=1`, normal speed,
   FastMurderCadence off). Ideally a cohabiting pair -> our forced ExCopSniper should behave like the decoded vanilla
   flow: killer idle at home -> `[flow] SetMurderLocation` goes work -> null -> street -> rooftop shot -> clean kill.
   Read `[flow] SetMurderLocation` + `[sniper-live]` + `Murder: Best sniper for vantage point over <street>` +
   whether it reaches executing/post. Confirm non-cohabiting pairs still do a coherent VoyeurSniper.
2. **SOLVABILITY check (#1, still pending):** once a motivated sniper kills cleanly, play it to the arrest — does it
   leave the findable sniper forensics (entry wound + a window/trajectory/nest clue) so a player can actually solve
   it? (Vanilla ExCopSniper leaves a rooftop nest + bullet; confirm ours does too.)
3. **Hit rate:** how often do motivated snipers fire vs fall back to vanilla? If too many fall back, investigate.
4. **PRE-SHIP CLEANUP:** gate/strip the `[sniper-obs]`/`[sniper-live]`/`[mo-dump]` diagnostics like the kidnap
   cleanup (keep F9); decide `ForceVanillaSniperMO`'s fate (dev-only); set the `MotivatedSniperShare` ship default;
   then merge `sniper-wip` -> `v2`, bump version, ship.

## RULES (this repo)
- ⚠️ NO `Co-Authored-By: Claude` trailers on commits (history was scrubbed; user wants no AI attribution).
- NEVER use em dashes in anything written for the user (files, docs, chat).
- Build `dotnet build -c Release` (auto-deploys when game CLOSED). Keep sniper work on `sniper-wip` until it lands
  and the user approves; do not touch `v2`.

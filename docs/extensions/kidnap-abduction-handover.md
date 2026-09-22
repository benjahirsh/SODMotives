# Motivated kidnappings — abduction handover (RESUME HERE)

**Updated:** 2026-09-22 · Branch `kidnap-wip` · This is the AUTHORITATIVE resume point for making
motivated KIDNAPPINGS complete. Companion detail: `kidnap-recon.md` (den decode), `sniper-recon.md`.
Read this FIRST, then `kidnap-recon.md`. **User is committed to solving this — do not drop it.**

## The goal
Make the mod motivate KIDNAP cases the way it motivates murders: swap in a motive-backed killer→victim pair,
and have the case run to completion (victim abducted + held) — matching vanilla. Gated behind
`[Troubleshooting] MotivateKidnaps` (default OFF) + also forceable with F2.

## STATUS: ~90% there, stuck on ONE thing (the abduction "seat")
Working (CONFIRMED in-game): the natural override motivates a kidnap (no F2 needed), a den gets assigned, and
the MEET happens (killer + victim both arrive at the restaurant). **The case then gets stuck in
`waitForLocation` forever**: the game repeatedly teleports the victim to the den (THOUSANDS of times) but the
location never "seats" (never advances to `travellingTo`/`executing`), so the victim is never restrained/held.
A real VANILLA kidnap seats + restrains almost immediately (≈3 teleports) and completes. **We do not yet know
why vanilla seats reliably and ours loops. Figuring that out is THE task.**

## ⚠️ DO NOT OVER-ASSUME — what's CONFIRMED vs OPEN
### CONFIRMED (evidence in logs this session)
- **A kidnap's only valid holding location is `murderer.den`** (`Human.den`, a `NewAddress` @0x2A8).
  `Murder.IsValidLocation(loc)` returns true only for a kidnap MO iff `loc == murderer.den` (decoded from ISIL;
  see `kidnap-recon.md`). A motive-chosen killer has `den==null` → 0/870 locations valid → instant hang. We fix
  that by assigning a den via `Human.SetDen(addr, mo)` (also decorates it). After assigning, the den-probe
  shows exactly **1 VALID** location (the den). ✓
- **The natural override path works** (log: `override: MotivateKidnaps ON — motivating a KIDNAP case`, then
  `MOTIVATED MURDER`, then `[den] assigned …`). No F2 needed.
- **The MEET works for our pairs.** Log `[kidnap entry]` at `waitForLocation` showed `goal1set=True
  goal2set=True; killer@Raven Restaurant; victim@Raven Restaurant` — BOTH attended. (Earlier I wrongly claimed
  the victim "won't attend"; corrected — sometimes both attend and it STILL loops.)
- **The den TYPE is NOT the differentiator.** We tried a hotel guest room (`701/502 Plaza Orchid Hotel`) AND a
  basement (`Basement 02 Hodge Projects`). BOTH loop (18,604 and 3,058 teleports respectively). Vanilla's den
  (`Vacant address, Dunn Heights`) took only 3 teleports and worked. So it is NOT hotel-vs-basement (both were
  even in the same building in one case). The den-name preference (prefer "Vacant"/"Basement") is committed but
  did NOT fix the loop.
- **The game gets the victim to the den by TELEPORT**, via its own fallback: log
  `Murder: Waiting for too long! Creating GoTo den routine for victim <V> to: <den>` then repeated
  `Found safe teleport for <den> (NewAddress) teleporting to (x,y,z)`. Vanilla dens teleport to Y≈−5.4
  (underground/basement); our looping cases also teleport (Y varies). The teleport COUNT is the tell:
  vanilla ≈3, ours thousands.
- **`FindSafeTeleport(den)` succeeds** for our dens (we require it in the picker), so a safe node exists.
- **Fast-forward (End) breaks kidnaps** — during a fast-forwarded session, even VANILLA kidnaps looped 320×
  with ZERO restrains. At NORMAL speed a vanilla kidnap completed cleanly. So all kidnap testing MUST be at
  normal speed (fast-forward OFF).
- **Relationship varies and doesn't obviously gate it:** our completed/attempted pairs have been
  `[lover,groupMember]`, `[regularCustomer]`, `[workOther]`, and once `NO EDGE (strangers)`. Vanilla's were
  `[groupMember]`. NOT yet a clear signal. Do NOT assume "vanilla picks socially-connected pairs" — unproven.
- **Vanilla setup spawns a `(V) Lure Note`** (+ Victim List, journals, vmails). Our forced `ExecuteNewMurder`
  path does NOT spawn these. UNKNOWN if the Lure Note matters for the mechanism (it may be flavor/evidence).

### OPEN / HYPOTHESES (NOT confirmed — investigate fresh)
- **USER'S KEY OBSERVATION (2026-09-22): the victim is NEVER visibly at the den in-game.** The user went to the
  den room and "saw nothing" — no victim there. So **maybe the teleport is silently FAILING** (the log prints
  "teleporting to (x,y,z)" but the victim doesn't actually end up/stay there), rather than "victim teleports in
  then walks out." My earlier "victim walks back out" was an ASSUMPTION — treat it as unproven. **CHECK: does
  the victim's `currentGameLocation`/`currentNode` actually become the den after a teleport line? Add logging
  of the victim's real position right after a teleport.**
- **Why does the location never SEAT (waitForLocation→travellingTo) for us but does for vanilla?** UNKNOWN.
  Leading idea (unproven): the seat needs the KILLER at the den (the killer never travels there in our case
  because the case never leaves waitForLocation → chicken/egg). Vanilla's killer DOES reach the den. WHY — via
  the meet escort? a murder-goal that points at the den? — is unconfirmed.
- **Does the murder GOAL point the killer at the den?** In one run we saw `Murder: Setting murder goal location
  to <den>`; in another we did not. Confirm whether/when our killer's goal targets the den and whether the
  killer ever physically travels there.
- **Does our swapped-in killer lack traits/setup a game-chosen kidnapper has** (so the AI won't drive the
  abduction)? The MO (`FinancialKidnapper`, `MadScientistKidnapper`) is assigned, but the killer may not meet
  its trait expectations. Unproven.
- **Is our late den-assignment (in the ExecuteNewMurder prefix) too late** vs vanilla assigning it during
  `PickNewMurderer`? Unproven.

## THE VANILLA REFERENCE RUN (a kidnap that COMPLETED, normal speed) — preserve this
Observer: `VANILLA KIDNAP: Mu Tan Dong#56 -> Heidi Kennedy#287`, relationship `[groupMember] like≈0.4
known≈0.5`, `killer.den = Vacant address, Dunn Heights` (denIsKillerHome=False, denIsVictimHome=False).
Game narration (`Player.log`) state flow (line #s = log verbosity, NOT time; whole thing was ~1 in-game day):
```
Set murder state: acquireEuipment
Chosen Lovelace View as a ransom delivery location
Set murder state: research
Set murder state: waitForLocation
Set murder state: travellingTo
Set murder state: executing
Victim is knocked out and restrained     <-- THE ABDUCTION (this is what ours never reaches)
Set murder state: post
Set murder state: escaping
Set murder state: unsolved                <-- case now live for the player to solve
```
Vanilla den teleport count in that session ≈ **3** (`Found safe teleport for Vacant address … Dunn Heights`),
then the victim STAYED and was restrained. Vanilla setup also spawned `(V) Lure Note`, `(F) Victim List`,
`(P) Journal`, vmails ("Enforcer Report VMail", "Doctor VMail"), and chose a ransom delivery location.

## OUR FAILING RUN (latest, normal speed, natural override) — the thing to fix
Observer: `MOD-FORCED KIDNAP: Krisha Lakhani#48 -> Zula Mutsi#151`, relationship `[workOther]`,
`[den] assigned Krisha Lakhani#48.den = Basement 02 Hodge Projects [vacant, teleport-viable]` (MO
`MadScientistKidnapper`). den-probe: `1 VALID: Basement 02 Hodge Projects`. `[kidnap entry]`: meet set up,
BOTH at `Raven Restaurant`. Flow: `acquireEuipment → research → waitForLocation` … then STUCK.
`Waiting for too long! Creating GoTo den routine for victim Zula Mutsi to: Basement 02 Hodge Projects` then
**3,058× `Found safe teleport for Basement 02 Hodge Projects`** — never seats, never restrains. F9 SCENE stayed
`?`. User did NOT see the victim at the den at any point.

## TOOLING (all in place; use it next session)
- **Observer** (`MurderWatchdog.LogKidnapObservation`, fires for ANY kidnap incl. vanilla, once): logs
  `[kidnap-obs]` relationship (both directions, connections/like/known) + den (+ denIsKillerHome/VictimHome) +
  `[kidnap obs]` meet/position line. READ-ONLY on vanilla.
- **Game's OWN verbose murder narration**: gated by `Game.Instance.printDebug` (@0x4F) + `debugPrintLevel`
  (@0x54) ≥ the message level (murder msgs = level 2). `DebugTools.EnableGameVerboseLogging()` sets both.
  Enable persistently via `[Troubleshooting] GameVerboseLogging = true` (applied every frame). **CRITICAL: these
  `Murder:` messages print to the GAME's log, `%USERPROFILE%\AppData\LocalLow\ColePowered Games\Shadows of
  Doubt\Player.log`, NOT the BepInEx log.** F2 also auto-enables it.
- **F2** = `DebugTools.ForceKidnapCase` (force a kidnap now; note: F2 while a VANILLA kidnap is active OVERRODE
  it and tangled two dens — prefer the natural-sandbox test to avoid that).
- **Den probe** (`MurderWatchdog.ProbeValidLocations`): counts how many of the 870 city locations pass
  `IsValidLocation` (should be exactly 1 = the assigned den).
- **F-key map** (see `DebugTools`): F2=force kidnap, F3=force sniper, F4=trigger murder, F6=cycle force,
  F7=ghost+always-answer, F8=killer-knower TP, F9=overlay, F10/F11/F12=teleports, Home=City Hall TP (works),
  End=fast-forward (KEEP OFF for kidnap tests). F1 reserved by the game.

## HOW TO REPRODUCE (clean test, no F2 tangle)
Config (overlay `` ` ``): `[Troubleshooting] MotivateKidnaps=true` + `GameVerboseLogging=true`,
`[Motive Mix] MotiveCaseShare=1`. New sandbox: **Procedural Murders ON + Kidnapping ON** (turn Regular murders
+ Sniper OFF to get kidnaps sooner). **Fast-forward OFF.** Let a kidnap get scheduled. Read BOTH logs:
`[SODMotives]` lines in BepInEx `LogOutput.log`, and `Murder:`/`Found safe teleport` in `Player.log`.

## CODE STATE (branch `kidnap-wip`, all built + deployed)
Commits (newest first): `190207d` den name-preference; `55a413b` FIX prefix param `motive` (a wrong name
`newMotive` had failed the WHOLE plugin load — F-keys dead — watch for this class of bug); `0415321` wire
`EnsureKidnapDen` into the natural override; `e02ceae` teleport-viable den + watchdog leaves reachable-den
kidnaps alone; `fca0c6e` observer; `1eabbcd` verbose-log-on-F2; earlier: den decode + fix (`cd5cfc7`,
`0c3a83d`), recovery/probe (`a40e39b`), tooling. Files: `MurderSelector.cs` (`EnsureKidnapDen`,
`PickVacantDen`/`PickTeleportViable` — prefers "Vacant"/"Basement" names, requires
`victim.FindSafeTeleport(den)!=null`), `Plugin.cs` (override `Patch_ExecuteNewMurder_Override` prefix now takes
`MurderMO motive` + calls `EnsureKidnapDen(m,v,motive)` for kidnap; `[Troubleshooting] MotivateKidnaps`/
`GameVerboseLogging` binds), `MurderWatchdog.cs` (observer + den probe + `KidnapDenReachable` — watchdog now
LEAVES a reachable-den kidnap alone, only cancels a no-den hang; the old meet-assist was removed — forcing
`meetTime` was inert because the game recomputes it), `DebugTools.cs` (F2, verbose-log, den/kidnap logging).
**⚠️ NO `Co-Authored-By: Claude` trailers on this repo. Build `dotnet build -c Release` (auto-deploys when game
CLOSED; restart to load).**

## KEY DECODED FACTS (Cpp2IL `2022.1.0-pre-release.21`; dump cached at an OLD session scratchpad
`…\73b964de-…\scratchpad\cpp2il\` — `out/` = dll_il_recovery call-graph, `isil/IsilDump/` = raw logic w/
numeric offsets, `mc_body.txt` = MurderController recovered)
- Murder field offsets: `0x34 state, 0x94 meetTimeTotal, 0x98 meetTime, 0xF0 preset, 0xF8 mo, 0x100 murderer,
  0x108 victim, 0x110 murderGoal, 0x118 location, 0x170 sniperVictimSite, 0x178 ransomSite, 0x180 meetRestaurant,
  0x188/0x190 boothSeat1/2, 0x198 meetGoal1, 0x1A0 meetGoal2`. `preset+0x20 = caseType` (murder0/sniper1/kidnap2).
- Human: `0x298 home, 0x2A0 residence, 0x2A8 den` (all `NewAddress`/controller). `Human.SetDen(NewAddress,
  MurderMO)`. `Human.FindAcquaintanceExists(Human, out Acquaintance)`; `Acquaintance.connections/like/known`.
- MurderMO allow-flags: `0x99 allowAnywhere, 0x9A allowHome, 0x9B allowWork, 0x9C allowPublic, 0x9D allowStreets,
  0x9E allowDen`. Kidnap MOs (`FinancialKidnapper`, `MadScientistKidnapper`) have ONLY `allowDen` true, plus
  `pickDen`, `blockVictimFromLeavingLocation`, `killerMeetsVicim` on the preset.
- `MurderController.Update()` is the giant state driver (1351 unknown calls; ISIL only). Meet setup ~ISIL
  L31890 (creates meetGoal1/2 → boothSeat1/2 at a scored restaurant). Meet completion ~ISIL L38806 (completes
  goals + `SetMurderState` when a timer passes; the game RECOMPUTES meetTime just before checking, so writing
  meetTime is useless). The "GoTo den routine for victim" fallback teleports the victim to the den via
  `victim.FindSafeTeleport(den)`.

## NEXT INVESTIGATION IDEAS (things to CHECK, not assume)
1. **Verify the teleport actually lands the victim** (user's lead): log `victim.currentGameLocation` /
   `currentNode.position` on the frame after a `Found safe teleport` line for our case vs a vanilla case. If
   the victim's real position is NOT the den, the teleport is failing → find why (a game guard? the victim
   `blockVictimFromLeavingLocation` conflict? our den not fully registered?).
2. **Find what seats the location for a kidnap** — read the `waitForLocation` branch of `Update()` (ISIL) for
   what triggers `SetMurderLocation`/`SetMurderState(travellingTo)`: is it the VICTIM at the den, the KILLER at
   the den, or a timer? That's the crux of vanilla-vs-ours.
3. **Track the KILLER's movement** for our case: does the killer ever travel to the den? Does the murder goal
   point there? If not, why (goal priority 0 at waitForLocation?).
4. **Compare a vanilla and a mod kidnap side-by-side in ONE session** (both observer + game narration on) at
   normal speed — diff the exact `Murder:` lines from where they diverge (both reach the "GoTo den routine",
   then vanilla restrains in ~3 and ours loops).
5. **Consider whether to reproduce more of vanilla's setup** (Lure Note / whatever `PickNewMurderer` does for a
   kidnapper) vs. directly driving the abduction (force killer to den + force restrain) — decide after #1–#3.
6. Watch for the plugin-fails-to-load class of bug (a wrong Harmony prefix param name kills the whole plugin =
   dead F-keys) — check `LogOutput.log` line ~22 for `Failed to patch … Parameter "x" not found`.

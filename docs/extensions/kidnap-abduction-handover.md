# Motivated kidnappings — abduction handover (RESUME HERE)

**Updated:** 2026-09-24 · Branch `kidnap-wip` · This is the AUTHORITATIVE resume point.
Read this FIRST, then the auto-memory (sod-prerelease-progress.md).

## ✅ DONE (2026-09-24) — motivated kidnappings WORK END TO END, native-walked, player-solvable (all UNCOMMITTED)
The whole loop is native and verified in-game:
- Victim WALKS from the public meet to a private, unowned, door-having "Vacant address" den; killer walks in and
  restrains (real physical trail, no teleport). Den doors are closed but UNLOCKED (matches vanilla).
- **The killer DELIVERS the ransom note himself**, walking to the victim's home and playing the delivery
  animation (VERIFIED in-game 2026-09-24, both vanilla and ours). Our `MurderWatchdog.SpawnRansomNote` is now
  only a FALLBACK: it skips if the note is already registered (`activeMurderItems[U]`), which the killer's own
  delivery does, so there is no duplicate.
- Case goes `post -> unsolved` (the game's scanner materialises it a beat after post), and is solvable,
  rescuable, and payable. A player solved one unaided.
- Config cleaned: kidnap `[Troubleshooting]` bools removed; single mixer slider `[Motive Mix] MotivatedKidnapShare`
  (default 1). Commit-assist / teleport hacks are inert internal statics now (walk-native is hardcoded on).
- Affair-gossip leak FIXED (`SocialEvent.Distribute` drops the betrayed partner from the affair audience; they
  stay a suspect but no longer narrate the secret affair).
- The ransom "thanks for the money" call on a RESCUE is VANILLA behaviour, NOT ours (confirmed by 4 side-by-side
  runs + the `[ransom-trace]` logging). Nothing to fix on our side.
- Version bumped to **1.1.0** (manifest + BepInPlugin + csproj); README + CHANGELOG updated (NO em dashes).

**SHIP STATUS 1.1.0 (2026-09-24):**
1. **DIAGNOSTICS STRIPPED (done, builds clean 0-warn).** Removed: the 5 ransom-trace Harmony patches
   (`Patch_Trace_*`) + `MurderWatchdog.RansomTrace`; the dead teleport/commit code (`KidnapSeatAssist`/
   `KidnapWalkNative`/`KidnapDenCommitAssist` statics, `TryTeleportIntoDen`, `TryDenCommit`, `KidnapDenReachable`,
   `SafeLoc`, their collections + all `!KidnapWalkNative` branches — all inert since walk-native was hardcoded on);
   the dead `CurrentKidnapIsOurs` (no callers) and the dead kill-time leftovers (`KidnapKillGraceHours`,
   `_killTimeTarget`); the extra `[kidnap-live]` fields (`KILLER_AT_VICTIMHOME`, room/door detail) + their helpers.
   The kidnap DIAGNOSTIC LOGGING that remains (the `[kidnap-obs]` observer — which also flips on the game's own
   verbose narration — the `[kidnap-live]` sampler, and the `[den probe]`) is now GATED behind `[Debug]
   EnableDebugKeys` so a shipped log stays clean; flip that on for a bug report. F7 `GiveTestCash` kept (already
   behind `EnableDebugKeys`). Load-bearing kidnap logic (kill-block, ransom-note spawn, den seal, waitForLocation
   stall-cancel, executing force-finish) is unchanged and always on.
2. **COMMITTED** `69cfcc8` on `kidnap-wip` (NO `Co-Authored-By: Claude` trailer), FAST-FORWARD-MERGED to `v2`.
3. **SHIPPED 1.1.0** — released on Thunderstore (by user); `v2` pushed to origin (`69cfcc8`) + lightweight tag
   `v1.1.0` pushed. GitHub `v2` and the Thunderstore page are both live at 1.1.0.

## 1.1.1 (2026-09-24) — kidnap SAVE/LOAD fix (committed `59b8193` on v2; pending reload-test + upload)
**Bug:** the kidnapper kill-block (`MurderWatchdog.ShouldBlockKidnapKill`) only fires while the victim is in
`KidnapReachedHold`, which is in-memory + cleared on EVERY game start (including a load) and was only ever set on
the SetMurderState transition INTO the hold. A kidnapping loaded already in the hold never re-transitions, so the
block never re-armed after a reload and the held victim could be killed before the ransom deadline. **Fix:**
re-arm `KidnapReachedHold` each tick in the watchdog's hold branch (post/escaping/unsolved), restoring the block
on the first tick after load (only in the hold states, so it never mislabels the abduction's own pre-hold
travellingTo/executing). Version bumped 1.1.0 -> 1.1.1 (manifest/plugin/csproj), CHANGELOG updated, zip built.
**PENDING USER:** reload-test (F2 kidnap -> victim held at unsolved -> SAVE -> RELOAD -> victim stays held,
survives to the ransom deadline, case still solvable), then `git push origin v2` + tag `v1.1.1` + upload the zip.
(Current NEW-feature dev = motivated snipers on branch `sniper-wip`; see `docs/extensions/sniper-recon.md`.)

### Executing-reload bug (2026-09-24) — CONFIRMED OURS, decoded from side-by-side logs
Reloading a save taken during the `executing` phase (the killer actively restraining the victim in the den,
BEFORE the "knocked out and restrained" that flips to post) breaks OUR kidnaps: on load the victim walks to
their own home and the killer walks to theirs, `executing` never completes, the case soft-locks. (Our watchdog
does NOT force-finish kidnaps, so nothing recovers it.)
- **VANILLA does NOT break** (user tested multiple executing reloads): the `[kidnap-live] VANILLA [executing]`
  lines show `KILLER_AT_DEN=True` + `VICTIM_AT_DEN=True` throughout — the vanilla killer STAYS in the den with
  the victim for the whole restrain, leaving only at post/unsolved (`KILLER_AT_DEN=False`, victim stays). Our
  `OURS [executing]` post-reload lines show both drifting to their homes (`VICTIM_AT_DEN=False victim@<home>`).
- **Den persists fine** for both (`den=` unchanged across reloads); the den was NOT the cause.
- **The HOLD persists fine for us too:** once the case reaches post/unsolved the victim is RESTRAINED (a
  persistent state) and stays at the den across reloads — that's why the 1.1.1 hold-reload fix works. Only the
  pre-restrain `executing` window is fragile.
- **Root cause:** our swapped/forced pair never gets the full persistent kidnap-abduction setup a real vanilla
  kidnapper gets (the setup is distributed across the game's own PickNewMurderer/PickNewVictim, which run before
  our swap). During normal play the LIVE abduction AI goals carry it through; on RELOAD those live goals are
  lost and, unlike vanilla, ours aren't re-driven from persistent state.
- **Also seen:** our forced kidnaps show `killTime=0.0` the whole time (never a real deadline); the vanilla one
  set `killTime=51.8` at unsolved. So the block holds forever for forced cases (victim never auto-killed).
  Separate from the reload bug; verify whether a NATURAL motivated kidnap gets a real killTime.
- **FIXED (approach B, 2026-09-24) — VERIFIED IN-GAME.** Root nailed via the `[kidnap-reload]` diag (diff of a
  vanilla vs an ours reload): a VANILLA victim keeps a `GoTo@den` goal (pri10) through waitForLocation +
  travellingTo that survives save/load; OUR swapped victim loses it on reload (`currentGoal=null`, all routine
  goals pri0), so nothing pins them. The killer is fine either way (`murderGoal@den` restores). FIX =
  `MurderWatchdog.EnsureVictimDenGoal`: during the pre-restrain phases, find the victim's den-targeting goal or,
  if it's gone (post-reload), recreate it from the game's own `RoutineControls.Instance.toGoGoal` preset
  (`newPassedGameLocation`=den, `newMurderRef`=murder) and pin it on top (re-asserted each tick). It stops at
  `executing` (the abduction then injects `Flee@den(pri12)`, which pins them). A WALK, not a teleport — physical
  trail unchanged. Log confirmed: `[kidnap-hold] rebuilt victim GoTo-den goal ...`, and post-reload
  `victim currentGoal=GoTo@den(pri100000)`; save/reload now holds at travellingTo AND executing (and the earlier
  hold-reload kill-block re-arm covers post/unsolved). Ships in 1.1.1. The `[kidnap-reload]` diag is kept, gated
  behind `[Debug] EnableDebugKeys`, as the tool for any future save/load regression.

## WATCH ITEM (did NOT reproduce on retest — not being fixed) — ransom-payment victim state
**Symptom (seen once):** after a kidnap victim was freed by the player PAYING the ransom (not by a rescue), the
victim ran around their apartment as if still being attacked/restrained. On a later test it did NOT recur, so it
is parked. First question if it returns: **is this OURS or vanilla?** (The rescue path was proven vanilla
earlier; the PAYMENT path hasn't been isolated.)
**Plan:**
1. **Reproduce + compare.** Force a kidnap (F2, normal speed, EnableDebugKeys on), pay the ransom, watch the
   freed victim. Then do the same on a VANILLA kidnap (Kidnapping type on, MotivatedKidnapShare 0, or just let
   one occur) and compare. If vanilla does the same jittery post-free behaviour → it's not ours, document + drop.
2. **If it's ours,** it's almost certainly leftover restraint/kidnap state our swap never cleared on release.
   The ransom resolves via `Murder.ransomPhase` (`none/travellingToRansom/collectedRansom/freeingVictim/
   finishedSuccess`) and `MurderController.KidnapperCollectsRansom`/`KidnapperCollectedRansom`; on
   `freeingVictim`/`finishedSuccess` vanilla clears the victim's restrained/kidnapped state + resets its AI.
   Our swapped victim likely keeps a restraint flag (a victim-side bool ~`victim+0x1F` was seen during the
   kill-block RE) or a stuck AI goal. Find where vanilla clears it (decompile the ransom-release path) and
   replicate for our overridden victim on release (a new one-shot in `MurderWatchdog` gated to our kidnap
   victims once `ransomPhase` hits `freeingVictim`/`finishedSuccess`, mirroring how we already gate the kill-block).
3. **Verify in-game:** pay a forced kidnap's ransom → freed victim behaves normally (walks home / resumes
   routine, not the attack-flail). Test at NORMAL speed. Note: our `[kidnap-*]` diagnostics now need
   `[Debug] EnableDebugKeys` on to log.
Toolchain unchanged: Cpp2IL `2022.1.0-pre-release.21` for method bodies, `ilspycmd` vs the interop DLL for
signatures. Deferred backlog (post-this): rare-NPC kidnap/murder bias, red-herring clues, theft motive.

**OPEN / BACKLOG (in `docs/post-release-handover.md`, none block shipping):**
- Verify the victim-post-free state: after being freed by ransom PAYMENT (not rescue), the victim ran around
  their apartment as if still being attacked. Check whether vanilla does the same; if it is ours, clear the
  kidnap/restraint state on release.
- Bias the murder/kidnap mix AWAY from rare NPCs (bosses + landlords) as murder-victims / arrested-killers but
  TOWARD them as (savable) kidnap-victims.
- "Ask your partner to come to the door" tweak.

Everything below is the historical build-up to the above and is kept for reference.
Companion detail: `kidnap-recon.md` (den decode), `sniper-recon.md`. **Test kidnaps at NORMAL speed only.**

## ⭐ NEW DIRECTION (2026-09-22c) — REBUILD to match vanilla (walk-based), iteratively; START WITH THE PUBLIC MEET
**Why the pivot:** the teleport-based approach below (seat-assist, killer-to-den, ransom-note spawn, killTime
handling, kill-block) makes a motivated kidnap COMPLETE + solvable — VERIFIED end-to-end in-game (abduct → hold →
den → ransom note → "Examine the ransom note at <home>" objective up front → deadline that matches the note →
kidnapper returns + kills if unsolved). **BUT it breaks the PHYSICAL EVIDENCE LAYER**, which the user (rightly)
won't ship: we TELEPORT the victim + killer to the den (both for the hold and the kill-return), so there's no
CCTV/proximity trail to the den, and the victim isn't placed in the den's back room like vanilla. For a detective
game that's built on following the physical trail, that's a real regression. Contrast: motivated MURDERS (the
shipped core) are 100% native — the killer WALKS to the victim, full trail — which is why they feel right.
**User's bar:** the physical layer must stay intact; if we mimic the game's method it must be an EXACT replication.

**The plan (agreed):** iteratively rebuild the kidnap so the NPCs WALK (leaving trails) like vanilla, **starting
with the PUBLIC MEET.** The meet is a REAL game mechanism (NOT the lure note): `Murder.meetRestaurant` +
`meetGoal1/2` + `boothSeat1/2`; the game gives the killer + victim AI goals to walk to a restaurant booth, and
in the logs BOTH were observed at the restaurant (they walked → real trail). So the meet leg is already native +
walked. Build outward from there: verify the meet, then work out how vanilla gets the victim from the meet to the
den (walked? — and make ours walk, which needs a WALK-REACHABLE den; the whole seat problem is that a motive-
chosen killer's den often isn't reachable, which is exactly why we resorted to teleporting). Each piece: replace
the teleport hack with the vanilla walked behavior, or keep the teleport only where truly unavoidable + flagged.

## ⭐⭐ 2026-09-22d — MECHANISM DECODED FROM LIVE LOGS + WALK-NATIVE MODE BUILT (deployed, NOT yet playtested)
**The meet→den leg is fully decoded — no more guessing.** The previous session's BepInEx `LogOutput.log` had our
`[kidnap-live]` sampler running over BOTH a completed OURS kidnap and a real VANILLA one side by side. Extracted
the victim's distance-to-den trajectory for each (den anchor distance, metres):
- **VANILLA (Mu Tan Dong#56 → Remas Said#231, den `Vacant address 1, Zeng Terrace`, rel `[groupMember]` like≈0.43):**
  victim starts AT THE MEET (`Glutton Grub`, a restaurant), then **WALKS** — dist `55.7 → 76.9 → 75.6 → 12.7 → 8.0`
  (seats: waitForLocation→travellingTo) → killer arrives (executing) → held. `WALK_INSIDE_DEN=True` throughout.
  **NO `[kidnap-assist]` lines** (we never touch vanilla). During the hold (`unsolved`) **`KILLER_AT_DEN=False`** —
  the kidnapper LEAVES the den and wanders the city (Mingo St, Brooks St, the hotel); the restrained victim stays.
- **OURS (Annabella Donovan#241 → JiHo Ko#135, den `Basement 01 Hodge Projects`):** victim at home (82 m), then
  **`[kidnap-assist] teleported victim JiHo Ko into den (try 1/60)`** + **`teleported killer Annabella into den`** —
  **our teleport fires on the FIRST waitForLocation tick, pre-empting BOTH the meet-walk and the den-walk** →
  executing at 1.8 m. Zero trail. (The meet goals WERE set — `[kidnap entry] restaurant=Raven Restaurant goal1set=
  True goal2set=True` — but the victim was still at home when we teleported, so they never walked to the restaurant.)

**Conclusions (now solid, not hypotheses):** (1) vanilla moves the victim meet→den by a **normal WALK** (the game's
own "GoTo den routine" walk goal), which succeeds because the vanilla den is **WALK-REACHABLE**; (2) our teleport
seat-assist was only ever COMPENSATING for non-walk-reachable dens, and it fires so early it also kills the meet
attendance; (3) the vanilla killer **does not stay** at the den during the hold — so the old "pin the killer at the
den" teleport was wrong too. Notably our `Basement 01 Hodge Projects` was itself `WALK_INSIDE_DEN=True` this run —
so a walk-reachable den IS findable by the picker; we just teleported instead of letting the walk happen.

**BUILT THIS SESSION — WALK-NATIVE mode (`[Troubleshooting] KidnapWalkNative`, default ON; deployed, builds clean
0-warn). When ON:**
- **Den picker requires a WALK-REACHABLE den** (`MurderWatchdog.KidnapDenWalkReachable` = `victim.FindSafeTeleport(
  den, false, /*allowTrespass*/false)` returns a node whose `gameLocation == den`, i.e. the victim can path INSIDE
  without trespassing = the `WALK_INSIDE_DEN` probe). `MurderSelector.PickTeleportViable` now enforces this (scans
  120 candidates vs 40, since walk-reachable is stricter than teleport-viable); the `killer.home` fallback must be
  walk-reachable too. If none found → `EnsureKidnapDen` returns false → case falls through to the watchdog cancel →
  vanilla (never hangs). `[den]` log states walk-reachability at assignment (`DescribeDenTeleport` → `WALK_INSIDE_DEN`).
- **NO teleporting.** The victim seat-assist (waitForLocation), the killer-to-den pin (travellingTo + executing) are
  all gated `!KidnapWalkNative` — the game's own walk goals carry both NPCs, leaving the real CCTV/proximity trail.
- **Safety net kept:** a walk that genuinely stalls past `WaitLocationStallHours` (12 h — vanilla walks meet→den in
  <1 h, so a progressing walk is never pre-empted) still cancels → vanilla.
- **Kept as-is (not teleport hacks, don't affect the trail):** the ransom-note spawn + kill-block/killTime handling
  (they make the case solvable). Set `KidnapWalkNative=false` to restore the full teleport baseline as reference.
- **F9 overlay now shows the walk live:** the `DEN` line gains `walk-reachable=<bool>  victim@den=<bool>  dist=<n>m`
  so you can WATCH the victim's dist-to-den shrink as they walk (no log-tailing needed).

**NEXT PLAYTEST (USER) — the "START WITH THE MEET" observation, now with the teleport out of the way:**
1. Config overlay (`` ` ``): `[Debug] EnableDebugKeys=true` (already set), `[Troubleshooting] KidnapWalkNative=true`
   (new default). Normal speed (fast-forward OFF — End key). Mature save.
2. Press **F2** (force kidnap). Immediately read the `[SODMotives][den]` line in `LogOutput.log`: did it seat a den
   with `WALK_INSIDE_DEN=True`? (If it logs "no WALK-reachable vacant den … watchdog will cancel", F2 again — the
   picker didn't find one this time; report how often that happens.)
3. Open **F9**. Watch the `MEET` + `DEN` lines. **Does the victim walk to the restaurant (F11 to jump there and
   look)?** Then does the `dist=<n>m` on the DEN line **shrink toward 0** as the victim walks meet→den, and
   `victim@den` flip true, and MURDER STATE go `waitForLocation → travellingTo → executing → post → unsolved`?
4. Report the `[kidnap-live] OURS` trajectory (dist over time) + whether any `[kidnap-assist]` lines appear (there
   should be NONE now). The question this answers: **do our (often stranger) forced pairs actually walk the meet→den
   leg like vanilla's socially-connected pairs, given a walk-reachable den?** If yes → the physical layer is restored
   and we build outward (ransom trail, den back-room placement). If the victim stalls (won't attend the meet / won't
   walk to the den), that's the next thing to solve (meet attendance for stranger pairs) — decision point in step 4.

## ⭐⭐ 2026-09-23 — MEET CONFIRMED NATIVE; meet→den leg is a GOAL-THRASH DEADLOCK; DEN-COMMIT ASSIST built
**Playtest result (walk-native, F2, `Daloris Harmon#77 → Finn Carroll#76`, den `Basement 02 Hodge Projects`
`WALK_INSIDE_DEN=True`):**
- **THE MEET IS FULLY NATIVE ✅** — user watched both victim + killer WALK to Raven Restaurant, sit, then leave.
  Zero `[kidnap-assist]` lines (no teleport). The meet leg leaves a real trail.
- **The meet→den leg does NOT complete — it DEADLOCKS.** User tailed the victim: after the meet he headed toward
  his own apartment (home routine), then **got stuck on the stairs, only turning side-to-side + flipping outfits
  back and forth** = classic AI **goal thrash** (the NPC re-evaluating + swapping goals every frame, committing to
  neither). Player.log: **"Waiting for too long! Creating GoTo den routine for victim Finn Carroll" ×17+** (vs a
  vanilla victim's **1** — `Holly Hart`, prev session, one GoTo then walked in). `[kidnap-live]` dist-to-den:
  `77m → 36m → (bar/payphone) 42m → 75m` — approached then RETREATED; never seated (0 restrains all session).
  Killer path (from the sampler, no tailing needed): `Raven → Jorgensen St → Brooks St → Raven` — never went to
  the den either (correct: the killer is only sent to the den AFTER the victim seats, which never happened).
- **Cohabiting is NOT the cause** — that pair cohabited, but the user also saw a non-cohabiting pair thrash the
  same way. Ruled out.
- **Root cause:** the game's "GoTo den routine" creates a den-walk goal but it keeps LOSING the victim's priority
  sort to their own routine (home/needs), so the victim never commits; the game re-fires the goal every ~40s and
  it thrashes. Vanilla's victim commits on the first GoTo (its den-walk goal out-prioritises routine).

**DECODED the goal API (ilspycmd on interop):** `Actor.ai` → `NewAIController` (Human:Actor). `NewAIController`:
`List<NewAIGoal> goals`, `NewAIGoal currentGoal`, `AITick(bool forceUpdatePriorities, bool ignoreRepeatDelays)`
(sorts goals by priority, runs the top one — there's a `Comparison<NewAIGoal>` sort), `CreateNewGoal(AIGoalPreset,
triggerTime, duration, NewNode, Interactable, NewGameLocation passedGameLocation, SocialGroup, Murder, int)`.
`NewAIGoal`: `preset`, `float basePriority`, `float priority` (effective, what the sort uses), `bool isActive`,
`NewGameLocation gameLocation` + `passedGameLocation` (the GoTo-den goal's target = the den), `passedNode`.

**BUILT — DEN-COMMIT ASSIST (`[Troubleshooting] KidnapDenCommitAssist`, default ON; compiles clean, NOT yet
deployed — game was holding the DLL open):** `MurderWatchdog.TryDenCommit(victim, den, vid)`, called every tick
while walk-native + our kidnap + `waitForLocation` + victim-not-at-den. It finds the victim's GoTo-den goal in
`victim.ai.goals` (matched by `passedGameLocation`/`gameLocation == den`) and pins `basePriority=priority=100000`,
`isActive=true`, `ai.currentGoal=denGoal` so the AI COMMITS to walking to the den (re-boosted each tick to beat
the game's ~40s re-fire). **Still a WALK — real trail, no teleport.** Logs `[kidnap-commit]` once per victim. The
`waitForLocation` stall-cancel (12h) → vanilla remains the safety net, so it still can't hang.

**NEXT PLAYTEST (deploy first — see below):** F2, normal speed, DON'T reload (reload resets to `research`). After
the meet, does the victim now **beeline to the den** (F9 `dist` shrinks to ~0, `victim@den=true`) and seat
(`waitForLocation → travellingTo → executing → "restrained"`)? Watch for `[kidnap-commit]`. THEN the next open
question: does the **KILLER** walk to the den in `travellingTo` to abduct (its own murder goal), or does that leg
also need a commit-assist? (Not touched yet — prove the victim seats first, then handle the killer if it stalls.)
**DEPLOY:** the build is done but the running game locked the DLL — CLOSE the game, re-run `dotnet build -c
Release` (auto-deploys), relaunch.

## ⭐⭐⭐ 2026-09-23b — FIRST FULL WALKED ABDUCTION (native, no teleport). Remaining problem = DEN ACCESS.
**BREAKTHROUGH (in-game):** with the den-commit assist ON and a WALK-REACHABLE den (`Public bathrooms`), a forced
kidnap ran the COMPLETE abduction natively: user watched the victim (Lili Roux#285) head home, then WALK to the
den, and the killer (Leah Kristensen#261) walk there and restrain her. Player.log: `travellingTo -> executing ->
"Victim is knocked out and restrained" -> post`. `[kidnap-commit]` fired (the priority pin made the victim commit
to the walk). **So the winning combo is: commit-assist + a den the victim can actually WALK INTO — real trail, no
teleport.** The physical layer is intact.

**WHY the bathroom worked and private units don't = ACCESS (user's lead, confirmed by the API):** the victim only
walks into a den it isn't TRESPASSING in (`Human.IsTrespassing(NewRoom,...)` gates AI pathing). A public venue
(`IsPublicallyOpen(false)=true`, e.g. bathrooms) is enterable by anyone → victim walks in. A locked/OWNED unit
(basement, hotel room) → the committed victim freezes on the street outside (confirmed: `Kelly Walker` froze at
`Lara Street` 51.9m out, never entering). `FindSafeTeleport(allowTrespass=false) INSIDE_DEN` is a FALSE POSITIVE
(finds an interior node but not a walkable, access-legal route). The bathroom's only flaw: it's PUBLIC — the killer
can't lock it, and a victim held in a public toilet is thematically wrong.

**The real target = a den that's walk-reachable AND private.** Vanilla uses `Vacant address N` units — ABANDONED /
UNOWNED, so nobody trespasses (accessible like the bathroom) but PRIVATE (no residents, no public traffic). Access
model decoded (interop): `NewAddress.AddOwner(Human)`, `AddInhabitant(Human)`, `AddGuestPass()`,
`CalculateRoomOwnership()`; `NewGameLocation.entrances`/`streetAccess`/`GetMainEntrance()`; `PathFinder.Instance
.GetPath(origin,dest,human)->PathData.pathfindSuccessful` (a real path test, not yet used).

**BUILT (deployed clean 0-warn):** `PickVacantDen` now sources `CityData.addressDirectory`, PREFERS unowned
"Vacant address" units (private, should be trespass-free), FALLS BACK to an NPC-open venue (bathroom — proven), and
EXCLUDES basements. Logs `[den] den-pool: N vacant -> X 'Vacant address' + Y NPC-open venue + Z basement` plus
examples. Commit-assist stays ON (it's needed — it fired for the successful run).

**NEXT PLAYTEST:** F2, commit-assist ON, normal speed. Read `[den] den-pool` — do "Vacant address" units exist
(X>0)? If a `Vacant address` den is assigned, does the victim WALK IN and get restrained (private den = the win)?
Or does it freeze outside (then the unit is still access-gated → apply an explicit grant: `den.AddOwner(killer)` or
`AddGuestPass()` + `CalculateRoomOwnership()` in `EnsureKidnapDen`, letting the pair in). If X=0, fall back is the
bathroom (still completes) and I'll switch to `PathFinder`/access-grant to synthesize a private reachable den.

## ⭐⭐⭐ 2026-09-23c — VANILLA OBSERVED: den access + placement + doors decoded; our behaviour tuned to match
**Confirmed from a real VANILLA kidnap (our sampler + user's eyes; `Mu Tan Dong#56 -> Heidi Kennedy#287`, den
`Vacant address 1, Dunn Heights`):**
- **The vanilla den reads `NPC_OPEN=False` yet the victim WALKED all the way in** (dist `92→89→78→1.8→seated`).
  ⇒ IsPublicallyOpen is NOT the walk-in signal; a "Vacant address" unit is walk-in-able because it's UNOWNED
  (no trespass), even though not "publicly open". **Our "Vacant address"-first den picker is therefore correct**
  (matches the vanilla den type); the NPC-open venue (bathroom) is only a fallback.
- **Bathroom placement is EMERGENT, not designed** — user saw the victim restrained in the MAIN room one run and
  cornered in the BATHROOM another (she ran there evading the knockout). So vanilla does NOT place the victim in a
  back room. ⇒ **Do NOT build forced bathroom placement — ours already matches (restrained where caught).**
- **Doors: vanilla's killer CLOSES the doors on the way out (default NPC flee behaviour) but does NOT lock** the
  front door (the den is unowned — no key). (User's earlier "vanilla locks it" was wrong; run-2 was unlocked.)

**Our behaviour (`MurderWatchdog.SealDen`, replaces LockDen):** once the abduction is done (post+) and the killer
has LEFT the den, we always **close** the front door(s) — reliable match for vanilla's default, since our swapped
killer sometimes left them wide open — and do **NOT lock** them. Vanilla leaves the unowned den UNLOCKED —
confirmed `locked=0` across 2 sandboxes AND a web-search (vacant-address kidnap dens stay unlocked) — so
`[Troubleshooting] KidnapLockDen` is **DEFAULT OFF** (matches vanilla). The optional lock (a "vanilla+" sealed
hold) stays as a toggle for anyone who wants it, but off by default. Logs `[kidnap-seal] closed N … front door(s)`.
(If the lock is ever turned on, note the OPEN RISK: a locked UNOWNED den may block the killer's return at killTime —
fix by giving the killer den ownership, `NewAddress.AddOwner(killer)` + `CalculateRoomOwnership()`.)

**Also this session:** `post -> unsolved` CONFIRMED (the game's scanner materialises the case a bit after post —
so the case IS solvable, just delayed). Commit-assist retired (default OFF; accessible den seats on its own).
Enhanced `[kidnap-live]` sampler now logs `victimRoom=<type>[BATHROOM]` + `frontDoors=N open=X locked=Y` for any
kidnap — deployed so a VANILLA run can settle definitively whether vanilla ever locks (watch `locked=`).

**Still to verify / do:** (1) vanilla lock question via the new `locked=` log (a couple more vanilla runs); (2) does
OUR killer close doors by default without SealDen (if so, SealDen's close is just a safety); (3) if KidnapLockDen
is ever turned on, confirm the killer can still return to kill at killTime through the locked unowned den.

**Architecture reality (why this is hard):** there is NO single `doKidnapMurder(killer, victim)`. The kidnap
SETUP (claim/decorate a den, place the victim inside it, register the killer's den-knowledge that the "where's
your den?" interrogation reads, ransom) is DISTRIBUTED across the game's OWN victim/killer selection
(`PickNewVictim`/`PickNewMurderer`) + world state, which runs BEFORE our `ExecuteNewMurder` swap. Our swap
inherits a case set up for a DIFFERENT victim, so we've been manually re-creating each piece (that's the whack-a-
mole + the broken physical layer + the dead den dialogue). The ELEGANT fix would be to make the game NATIVELY set
up the whole kidnap for our motivated pair — but no clean hook was found (`Human.SetDen` is `virtual` with no
traceable caller; the den may be pre-existing world state tied to which NPC the game picks). Worth re-examining
`PickNewMurderer`/`PickNewVictim` (can we influence the pick, or run the native setup for our pair?).

**NEW DEV TOOLING (this session, built + deployed):** F9 panel now shows the KILLER's + VICTIM's HOME addresses
next to their names, and for a kidnap also a `MEET:` line (`meetRestaurant`) + `DEN:` line (`killer.den`).
**F11 = teleport to the kidnap MEETING location** (was victim's work). **F12 = teleport to the VICTIM's home**
(was nearest case-knower). F8 still = nearest killer-knower. (`TeleportToNearestKnower` is now unbound/dead.)

**Den-dialogue (issue #2) — researched:** it's gated behind a dialogue sequence (arrest for MURDER → ask why →
THEN the den option appears) and is finicky/BUGGY even in VANILLA; players find the den by canvassing NPCs with
the victim's photo or pinning a location on the case board. Ours is dead because our teleport-set-up killer lacks
the internal den-knowledge the option reads. Low priority vs. the physical layer. Sources: Steam discussions
986130 (kidnap case issues / kidnapper-not-collecting-suitcase / phone-number bug).

**Everything below is the TELEPORT-BASED baseline** — it works but is the thing we're replacing/refining. Keep it
as a fallback + reference; decide per-piece whether to keep or make walk-native. Nothing is committed on this
branch beyond earlier commits (the recent seat/ransom/kill/tooling work is built + deployed, UNCOMMITTED).

## The goal
Make the mod motivate KIDNAP cases the way it motivates murders: swap in a motive-backed killer→victim pair,
and have the case run to completion (victim abducted + held) — matching vanilla. Gated behind
`[Troubleshooting] MotivateKidnaps` (default OFF) + also forceable with F2.

## STATUS: CORE SOLVED ✅ (2026-09-22) — kidnap COMPLETES + is fully solvable; now polishing 4 follow-ups
**The seat was the whole blocker and it's fixed.** Root cause (confirmed in-game + ISIL): the game seats a
kidnap only when the VICTIM's `currentGameLocation == murderer.den`, and it gets the victim there with a WALK
goal — which fails when the den isn't walk-reachable (a motive-chosen killer's den is a locked basement the
victim can't path into; the "teleport" belief was a misread — the game only WALKS the victim). FIX = SEAT-ASSIST:
`MurderWatchdog` teleports the victim into the den (`Actor.Teleport`) when stuck. **VERIFIED IN-GAME (F2):** one
teleport → `waitForLocation → travellingTo → executing → "knocked out and restrained" → post → escaping →
unsolved`; the player can complete the case and get ALL bonuses (incl. den location) correct. Motivated kidnaps
now work end-to-end.

### Follow-up polish — IN PROGRESS
- **KILLER-TO-DEN: DONE + VERIFIED IN-GAME.** The killer also couldn't walk to the walk-unreachable den, so the
  victim was held with no captor. The seat-assist now teleports the KILLER into the den during
  `travellingTo`/`executing` too — confirmed `KILLER_AT_DEN=True`, killer holds the victim. Also: `executing`
  force-KILL now skips kidnaps (a kidnap ends in restraint, never death).
- **#4 motive wording: DONE (no code).** Affair-kidnap where the killer is the PARAMOUR (affair with the victim's
  partner) and the victim could expose it. Correct but dense; optional reframe to "clear the rival out of the
  picture." Left as-is unless the user asks.
- **#1 RANSOM NOTE + #2 "investigate the victim's home" objective — ROOT FOUND, EXPERIMENT DEPLOYED (not yet
  playtested). USER PRIORITY.** These are the SAME lead: vanilla delivers a ransom note to the victim's home and
  adds the objective **"Examine the ransom note found at &lt;home&gt;"** (= the investigate-home lead). CONFIRMED
  via ISIL + logs: the kidnap subplot is driven by the game's **Case → Objective chain** —
  `MurderController.TriggerRansomDelivery()` (composes the note text + creates that objective + adds the ransom
  phone number) is **`[CalledBy] Objective.Complete`**, i.e. it fires when the player completes a prior kidnap
  objective. Our victim-SWAP happens at `ExecuteNewMurder`, *after* the game set the kidnap Case up for ITS
  chosen victim, so our swapped victim never enters that chain: `TriggerRansomDelivery`/its narration never fire,
  the "find ransom note" objective in `TriggerKidnappingCase` is gated out (it needs the note item registered in
  `Murder+0x128[key 20]`, which stays empty), and eventually `MurderController.Tick` force-fails the ransom
  (`TriggerRansomFail`) → the victim is killed. `KidnapRansomPhase` enum = none / travellingToRansom /
  collectedRansom / freeingVictim / finishedFailed / finishedSuccess (this tracks COLLECTION, after the note).
  **EXPERIMENT (deployed):** `MurderWatchdog` calls `MurderController.Instance.TriggerRansomDelivery()` once when
  our overridden kidnap reaches `escaping`/`unsolved`, logging `[ransom]` ransomPhase before/after +
  `currentMurderIsOurs`. If Player.log then shows "Adding find ransom note objective"/"Creating ransom objective"
  + a note at the victim's home → the authentic vanilla note+objective works for ~a few lines. If it no-ops (a
  guard on `MurderController+0x40/0x48` / `GetCurrentMurder`), the logs say so → pivot to a **mod-authored ransom
  note** placed at the victim's home via `ClueInjector` (the mod's proven note-injection), which delivers the
  same visible lead reliably. `[kidnap-live]` now also samples `post`/`escaping`/`unsolved` + logs `ransomPhase`.
  **RESULT (playtest, in-game pass-time): the ransom OBJECTIVES DO get created** — the game itself then ran
  `TriggerRansomDelivery()` many times and logged `Objective: Successfully added objective Optional: For ransom
  to be collected, call: NNN-NNNN`. So the ransom chain CAN be kicked off. **NEW BLOCKER = the KILL DEADLINE:**
  the objectives appeared and immediately auto-completed and "save the victim's life" was crossed out — the
  kidnapper killed the victim instantly. Root: **`Murder.killTime`** (the game time the kidnapper kills) is set
  by the bypassed vanilla setup, so ours stays ~0 → "now >= killTime" is instantly true, and the kill-check
  (decoded @MurderController.txt ISIL ~2960-3044) fires as soon as the killer is **co-located** with the victim —
  which our killer-to-den pin guarantees. **FIX DEPLOYED (not yet playtested):** `MurderWatchdog` pushes
  `murder.killTime = SessionData.gameTime + KidnapKillGraceHours` (default 72h = 3 in-game days) once per victim,
  as early as it sees our kidnap, so the player has time to investigate + rescue. `[kidnap-live]` now logs
  `killTime`/`now`; `[kidnap-kill]` logs the push. If it sticks, the note+objective+grace together = a solvable
  vanilla-like kidnap. If the game re-sets killTime small after ours, add a guarded re-assert (watch the logs).
  - **killTime fix VERIFIED (`pushed killTime 0.0 -> 114.0`)** — killTime was indeed 0 (unset); now the victim
    isn't insta-killed.
  - **BUT the direct `TriggerRansomDelivery` call was WRONG** — it created the DOWNSTREAM objectives (set_victim_free
    / place briefcase / call number) out of order and skipped the note. Vanilla order (confirmed from a back-to-back
    vanilla run): `Examine the ransom note found at <home>` FIRST → player reads it → THEN the downstream objectives.
  - **REAL FIX (deployed, not yet playtested):** the ransom note is a **preset lead item tagged `JobTag.U`**;
    vanilla spawns it + registers it in **`Murder.activeMurderItems[U]`** (a `Dictionary<JobTag,Interactable>`
    exposed in interop), and `TriggerKidnappingCase` adds the examine-note objective only when that entry exists.
    Our rushed/swapped case never spawns it. `MurderWatchdog.SpawnRansomNote` now finds the tag-U lead in
    `murder.preset.leads` and spawns it via `MurderController.SpawnItem(...)` with the lead's own params, then
    registers it in `activeMurderItems[U]` — replicating vanilla's `SpawnItemsCheck`. The **direct
    TriggerRansomDelivery call was REMOVED**; the game's own chain (examine-note → TriggerRansomDelivery →
    downstream) is left to run. TEST: does `Examine the ransom note found at <home>` now appear FIRST, and do the
    downstream objectives appear only AFTER reading it? Watch `[ransom] spawned ransom note (tag U…)`.
  - **RANSOM NOTE + EXAMINE-NOTE OBJECTIVE: VERIFIED WORKING** — spawning the tag-U note lead gives the correct
    vanilla order ("Examine the ransom note found at <home>" first, then the downstream objectives after reading).
  - **KILL DEADLINE — the game OVERWRITES our killTime.** Playtest: we set killTime 0→114.4 and it HELD through
    `post`, but the moment the case hit `unsolved` the game reset it to ~50.5 (≈ now+6h) and the kidnapper killed
    the victim ~6h in — long before the (later) displayed save-by. So a one-time push is not enough. **FIX
    (deployed, not yet playtested):** pin killTime — pick a fixed target (now + `KidnapKillGraceHours`, default 72h)
    ONCE per victim and RE-ASSERT it every tick (only raise to target, never lower), so the game's short reset
    can't stick. Because we hold it from the start, the ransom note's stated kill time should line up with the
    actual kill. `[kidnap-kill]` logs the set + a one-time "game shortened … re-asserted" line; `[kidnap-live]`
    logs `killTime`/`now` so we can confirm it stays pinned. Grace is tunable (could bind to config, or match
    vanilla's real deadline). WATCH: if the victim still dies early via the RANSOM-fail path (`finishedFailed` /
    `TriggerRansomFail`) rather than killTime, that timer needs the same treatment.
  - **KILL still fired despite the killTime pin (playtest) — killTime is NOT the kill gate.** Log CONFIRMED the pin
    worked (`game shortened killTime to 49.5; re-asserted to 114.0`, held at 114), yet the kidnapper killed the
    victim right after the case went live (now≈44 ≪ 114). So the kill is gated by OTHER expired timers/flags (a
    ransom-resolution counter @MurderController+0xE8 + a victim flag @victim+0x1F), not killTime. Chasing each is
    whack-a-mole. **NEW FIX (deployed, not yet playtested) — block the kill directly:** the kill sequence sets
    `Murder.kidnapKillPhase=true` then `SetMurderState(travellingTo)`→`(executing)` to send the killer to deliver
    the blow. A **PREFIX on `Murder.SetMurderState`** (`MurderWatchdog.ShouldBlockKidnapKill`) now returns false
    (skips the transition) for our kidnap when `kidnapKillPhase` is set AND `now < killTimeTarget` — so the kill
    can't start until our deadline; the victim stays held + rescuable. After the deadline we stop blocking (real,
    missable deadline). `[kidnap-kill] BLOCKING …` logs it once; sampler now logs `killPhase=`. ⚠️ This adds a new
    Harmony **prefix** to `SetMurderState` (param `newState`, matching the existing postfix) — **verify the plugin
    still loads** (LogOutput.log has NO "Failed to patch … Parameter not found"; F-keys work). Deadline still 72h,
    tunable / could be "the following day".
  - **KILL-BLOCK worked (victim survived) BUT suppressed the ransom objectives (playtest).** The block held the
    victim alive, but the game had flipped `kidnapKillPhase=True` the instant the case went live and STAYED there
    (standoff — Player.log spammed "It's time to kill"), and that kill-mode SUPPRESSES the ransom-demand setup:
    only "Collect Hand In" was added, no "Examine the ransom note" (though the note item WAS spawned). So kill-mode
    and the ransom flow are mutually exclusive. **COMBINED FIX (deployed, not yet playtested):** (1) block the
    kill's travellingTo/executing re-entry via `KidnapReachedHold` (set once the case reaches post/escaping/
    unsolved) instead of via `kidnapKillPhase`, so (2) we can also CLEAR `murder.kidnapKillPhase=false` every tick
    before the deadline — letting `TriggerKidnappingCase` build the ransom objectives (examine-note → downstream)
    while the kill stays blocked. Racy (the game re-sets kidnapKillPhase each tick; we clear it each tick), so it
    may or may not un-suppress the objective. **If the examine-note objective STILL doesn't appear, the vanilla
    kill/ransom timing is likely not cleanly reproducible for a swapped victim — decision point: (A) make the
    victim un-killable / always-rescuable (keep ransom leads, drop the deadline), (B) accept early death with
    working ransom, or (C) deeper RE of the resolve gate (`victim+0x1F` / MC ransom timers).** `killPhase=` now in
    the `[kidnap-live]` sampler.
  - **ORDER BUG FOUND + FIXED (playtest): the "Examine the ransom note" objective only appeared at the KILL, not at
    case-live.** Player.log order: abduction "knocked out and restrained" → `post` → `escaping` → "Triggered new
    kidnapping case" → `TriggerKidnappingCase()`×many (this is where the game checks for the note + would add the
    examine-note objective). Our `SpawnRansomNote` fired at escaping/unsolved — a hair too LATE, so
    TriggerKidnappingCase's check missed the note and only added the objective at its NEXT run (the kill's post).
    Meanwhile the kill-block + kidnapKillPhase-clear correctly kept the victim alive during the hold. **FIX
    (deployed, not yet playtested):** spawn the ransom note at the earlier `post` (right after the abduction),
    before TriggerKidnappingCase runs at `escaping` — so the examine-note objective is created EARLY (during the
    hold), like vanilla, while the kill stays blocked until the deadline. If this lands: examine-note appears at
    case-live + victim held/rescuable + kill only at the 72h deadline = the full intended flow.
  - **RANSOM NOTE + EARLY OBJECTIVE + VICTIM SURVIVAL: ALL WORKING (playtest).** Note spawns, "Examine the ransom
    note at <home>" appears up front, victim held (survived 48h in test). REMAINING: the note said "you have 6
    hours" but our kill-block held for 72h (KidnapKillGraceHours) — a mismatch the user flagged. **FIX (deployed,
    not yet playtested):** stop overriding killTime / imposing our own 72h deadline; instead release the kill-block
    at the game's OWN `Murder.killTime` (the value the ransom note counts down to). So the note's stated time and
    the actual kill are the SAME value by construction (~6h, and they track even if it varies per case). The
    kidnapKillPhase-clear + kill-block now both gate on `killTime` (block until `now >= killTime`; keep blocking
    while killTime is unset ~0 so no kill before the deadline is established). `KidnapKillGraceHours`/`_killTimeTarget`
    are now unused (left in place). Net kidnap flow if this lands: abduct → hold → examine-note lead at case-live →
    ~6h window to investigate/rescue → kidnapper returns + kills at the note's stated time if unsolved.
  - **LOW-PRIORITY (user-noted):** mod kidnap runs noticeably FASTER than vanilla (we teleport killer+victim to the
    den, skipping the travel/lure the game normally does). Cosmetic pacing; revisit later if desired.
- **#3 Post-rescue: victim still "in handcuffs" after release; killer re-teleports + kills on fast-forward.**
  Lower priority (user thinks fast-forward is involved). Revisit after the ransom lead. Likely needs the restraint
  state cleared on release + an egress nudge (victim can't path out of the walk-unreachable den).

## (historical) The seat problem — how it was diagnosed
Working (CONFIRMED in-game): the natural override motivates a kidnap (no F2 needed), a den gets assigned, and
the MEET happens (killer + victim both arrive at the restaurant). The case then got stuck in `waitForLocation`:
the game repeatedly ran a "GoTo den routine" for the victim but the victim never reached the den, so it never
seated. Resolved by the seat-assist above.

## DECODED THIS SESSION (2026-09-22) — the EXACT seat condition (why it loops)
Read the ISIL of `MurderController.Update()` and `Murder.IsValidLocation` (dump at
`…\73b964de-…\scratchpad\cpp2il\isil\IsilDump\Assembly-CSharp\`). The `waitForLocation` **primary seat** is
(MurderController.txt ISIL ~L39587–39615 → seat block ~L40746–40776):
```
if (murder.state == waitForLocation /*3*/ && <session/singleton ok>)
    if ( murder.IsValidLocation( victim.currentGameLocation /*Human field @0x168*/ ) )   // NOTE: victim's CURRENT loc, NOT the den directly
    {   murder.SetMurderLocation( victim.currentGameLocation );
        murder.SetMurderState( travellingTo /*4*/ ); }          // <-- THE SEAT
    // else: not yet at a valid loc -> "Waiting too long" -> GoTo <VictimSite|work|den|street|home> routine
```
And `Murder.IsValidLocation(newLoc)` (Murder nested ISIL @L6476): rejects `newLoc==null` and the street/outside
singleton; branches on `mo` allow-flags (`mo` @Murder+0xF8; caseType @preset+0x20: sniper==1 ⇒ `newLoc==sniperVictimSite@0x170`);
for a **kidnap MO (allowDen only)** it returns true **iff `newLoc == murderer.den`**.
⇒ **The seat fires only when the VICTIM's own `currentGameLocation` equals `murderer.den` — i.e. the victim is literally standing in the den.**
Our den already PASSES `IsValidLocation` (den probe = 1 valid), so IsValidLocation is NOT rejecting our den. The
loop can therefore mean ONLY ONE thing: **the victim's `currentGameLocation` never actually becomes the den.**
This CONFIRMS the user's lead (teleport not landing) as the mechanism and names the exact field to watch.
- **`Human.FindSafeTeleport(gameLoc,...)` just FINDS + RETURNS a node** (Human.txt ISIL ~L73533: logs
  "Found safe teleport for <gameLoc> teleporting to {pos}" then `return node`). The **caller** does the actual
  teleport. So the "thousands of Found safe teleport" lines are thousands of *calls* (one per stuck tick), and
  the open question is now precise: **does that returned node sit INSIDE the den, and does the victim ever
  register as being in the den?** (`NewNode.gameLocation`/`.room`/`.position` are public — usable to check.)

### DIAGNOSTIC BUILD DEPLOYED (this session, purely additive — no behaviour change, builds clean 0-warn)
- `[SODMotives][den] teleport-viability: FST-node.loc=<..> INSIDE_DEN=<bool> node.pos=<..>` — logged at
  den-assignment (F2 or natural), from `MurderSelector.EnsureKidnapDen` → `MurderWatchdog.DescribeDenTeleport`.
  Answers immediately whether the den's safe-teleport spot is inside the den.
- `[SODMotives][kidnap-live] OURS/VANILLA VICTIM_AT_DEN=<bool> victim@<loc> ; KILLER_AT_DEN=<bool> killer@<loc> ;
  den=<..> ; dist(victim->den.anchor)=<..> ; FST-node.loc=<..> INSIDE_DEN=<bool> …` — a throttled (every 0.1
  game-hr, capped 80) live sampler in `MurderWatchdog.Tick`, fires for ANY kidnap incl. VANILLA while in
  `waitForLocation`. Tells us if the victim EVER reaches the den (ours vs vanilla), if the killer does, and
  whether the safe-teleport spot is inside the den. **READ IN THE BepInEx LogOutput.log (`[SODMotives]`).**
  → If `VICTIM_AT_DEN` is never true for ours but true (then seats) for vanilla, and/or `INSIDE_DEN=false` for
  ours, the teleport-landing hypothesis is confirmed and the fix is a den whose FST lands inside it / that the
  victim can actually be seated in. If `VICTIM_AT_DEN` flickers true but never seats, it's a different bug
  (victim leaves / wrong field) — investigate from there.

## ✅ ROOT CAUSE CONFIRMED IN-GAME (2026-09-22) + FIX DEPLOYED (not yet playtested)
Ran the diagnostic build (fresh sandbox, all murders off, F2). Findings, side by side:
- **OUR F2 kidnap** (Rina Takagi#218 → **Elliot Xue#29**, den=`Basement 03 Rose Building`): den probe = **1 valid** ✓,
  `INSIDE_DEN=True` ✓ (safe-teleport spot IS inside the den, node Y=−5.4). Meet happened (both at Jade King
  Lounge). Then the victim **drifts to `Fitzgerald Boulevard` (a street) and stalls at exactly 89.0 m from the
  den**, never closer; killer goes home. **`VICTIM_AT_DEN` never true.** Player.log: 42+ ×
  `Waiting for too long! Creating GoTo den routine for victim Elliot Xue` → never seats.
- **VANILLA kidnap same session** (Oliver Morozov#67 → **Shen Ren#245**): **1×** `GoTo den routine`, victim walks
  in — reaches `Novák House Basement 1st floor landing` (**1.8 m**) — then the sampler stops because it **SEATED**
  (`Set murder state: waitForLocation → travellingTo → executing`).
- **THE MECHANISM (also from ISIL):** `GoTo den routine` = `FindSafeTeleport(den)` (just FINDS+logs a node — the
  "Found safe teleport … teleporting to" line is NOT an actual teleport) **+ `NewAIController.CreateNewGoal` = a
  WALK goal.** The game relies on the victim WALKING into the den. Vanilla's den is walk-reachable; **ours is
  NOT** (locked basement of a lived-in building → victim's pathing stalls at the street). `FindSafeTeleport`
  finds an interior node only because we pass `allowTrespass=true`, which ignores the door the victim's real
  pathing respects. ⇒ The old "victim teleported thousands of times" belief was a MISREAD of the finder's log;
  the victim is never actually moved — it's a failed WALK.
- **FIX = SEAT-ASSIST (deployed, builds clean 0-warn, NOT yet playtested):** `MurderWatchdog.TrySeatAssist` — when
  one of OUR kidnaps sits in `waitForLocation` with a reachable den and the victim is NOT yet inside it, teleport
  the victim into the den via **`Actor.Teleport(FindSafeTeleport(den), null)`** + `UpdateGameLocation()`, so
  `IsValidLocation(victim.currentGameLocation)` passes and the case advances. Bounded (`SeatAssistMaxTries=60`
  per victim, `KidnapSeatAssist` bool). Also: the `executing` force-KILL now **skips kidnaps** (a kidnap resolves
  to restraint, not death); the `[kidnap-live]` sampler now also fires in `travellingTo`/`executing` (state shown
  in the line) and `DescribeDenTeleport` now also logs `WALK_REACHABLE`/`WALK_INSIDE_DEN` (allowTrespass=false).
- **NEXT (this is the open question the next playtest answers):** does seat-assist → seat → `travellingTo` →
  `executing` → "knocked out and restrained" → `post` COMPLETE, or does it seat then stall because the KILLER
  also can't reach the (walk-unreachable) den, or the victim wanders off before the killer arrives? Watch
  `[kidnap-live]` (does `KILLER_AT_DEN` become true in travellingTo?), `[flow] SetMurderLocation`, and Player.log
  `Set murder state:`. If the killer can't reach the den → extend the assist to the killer / keep the victim
  pinned until restrained. If it completes → decide whether to also PREFER walk-reachable dens for naturalness.

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

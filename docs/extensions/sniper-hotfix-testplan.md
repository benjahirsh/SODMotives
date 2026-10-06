# Sniper hotfix - test plan (branch `sniper-hotfix`, commit 03d34e7)

Fixes two user-reported bugs on shipped motivated sniper cases:
- **Bug 1:** `During invoking native->managed trampoline` NRE at `TryGetSniperVantagePoint`; any sniper case stuck.
  Cause: the mod's Harmony patch on that solver forced an Il2CppInterop trampoline onto a hot game method that
  NRE'd on some players' builds. **Fix: the patch is gone**; snipers defer the nest + shot to the game.
- **Bug 2:** a sniper whose victim/murderer went null/destroyed ("Victim (Null)") jammed the whole murder
  pipeline for days and bricked the save. **Fix: `TryRecoverBrokenMurder` cancels any broken current murder**
  (any case type) so the scheduler resumes, healing bricked saves on load.

The DLL is already built + deployed to `BepInEx/plugins/SODMotives`. If you rebuild, do it with the game CLOSED.

## Config to set before testing
In `BepInEx/config/com.benhirsh.sodmotives.cfg` (BindApply keeps an existing value, so verify, don't assume):
- `[Motive Mix] MotivatedSniperShare = 1`  (every sniper motivated - the shipped default)
- `[Troubleshooting] MotiveCaseWatchdog = true`  (renamed from UnstickStalledMurders; gates the whole motive-case watchdog incl. the recovery - must be on)
- `[Debug] EnableDebugKeys = true`  (turns on the `[sniper*]` / `[recover]` logging and the F-keys)
- Sandbox: the **"Sniper cases"** murder type must be enabled.

Read the log yourself at `BepInEx/LogOutput.log` (and `Player.log`). No need to relay lines to me - point me at the file.

## Test 1 - Bug 1 gone (snipers no longer NRE / stick)
1. Start a sandbox with Sniper cases on. Press **F3** (ForceSniperCase) to create a motivated sniper now, or just
   play until one occurs.
2. Confirm the log shows `[SODMotives] override: motivating a SNIPER case ...` then
   `[SODMotives][sniper] seeded sniperVictimSite = <street> (game default ...)`.
3. **There must be NO `During invoking native->managed trampoline` / NRE at `TryGetSniperVantagePoint`** anywhere.
   (That error cannot occur now - the patch that created the trampoline is removed.)
4. The case should progress and the shot should land like a vanilla sniper (waitForLocation -> travellingTo ->
   executing -> post). F9 shows `SNIPE MO` / `SNIPE SITE` and `SNIPE NEST: deferred to the game's own site/...`.

## Test 2 - the 3h re-seed + 24h cancel (stuck but not broken)
1. Force/await an ExCop sniper whose victim has a hard routine (or just watch a slow one).
2. If no shot lands, expect a re-seed roughly every **3 in-game hours**:
   `[SODMotives][sniper] default pool (N ranked ...); fail#K -> picked rank R = <street>` with R climbing toward 0
   (the guaranteed-best rooftop) as K rises. Re-seeds pause while the killer is `travellingTo` (so a working
   approach is not interrupted).
3. If it still never fires, at **24 in-game hours** expect:
   `[SODMotives][sniper] ... never fired from a seeded site within 24h ... cancelling to vanilla` and the case ends.
4. **Sanity check:** a normally-solvable sniper should FIRE before the first 3h re-seed (i.e. 3h is not cutting
   good cases short). If good cases are getting re-pathed, tell me and I'll raise `SniperReseedStallHours`.
   (Use the FastCadence / TimeBoost debug keys to shorten the wait to the 3h / 24h windows.)

## Test 3 - Bug 2 recovery (REDESIGNED - the one that needs your eyes)
The recovery was rewritten after the first F6 test: it no longer calls the game's CancelCurrentMurder (that
AI-ticked the destroyed actor and NRE'd, which corrupted the case and caused the never-ending reward loop). It now
tears the broken murder down DIRECTLY (remove it from the murder list, null the controller's current-murder
fields, re-enable the scheduler) and never ticks the dead actor, so no NRE and nothing left to over-pay.
1. With a motivated sniper active and the pair shown in F9, destroy a party out from under the case. Two dev keys
   (gated behind `EnableDebugKeys`, both default-unbound - bind free keys like **F6** and **F5** under
   `[Debug Keys]`): `BreakCurrentMurderer` (the Nasty-Dog repro) and `BreakCurrentVictim` (the destroyed-victim
   variant). Run BOTH, in separate attempts.
2. Watch `BepInEx/LogOutput.log` for, in order:
   - `[SODMotives][recover] discarding broken murder id=<n> (murdererDead=.. victimDead=..) by direct teardown ...`
   - `[SODMotives][recover] teardown done; GetCurrentMurder()==null (cleared). Watching for a fresh case.`
   - `[SODMotives][recover] scheduler resumed -- a fresh case was picked.`
3. **Success =** that `scheduler resumed` line appears and a new murder runs; and **no NRE** (`During invoking
   native->managed trampoline` / `NullReferenceException`) anywhere after the break. Hit **End** (TimeBoost) to
   fast-forward the wait.
4. **CRITICAL reward-loop check:** after recovery, play on and when a fresh case comes up, **hand it in / resolve it**
   and confirm the payout happens **once** (no never-ending money + social credit). That loop was the symptom; it
   must be gone.
5. **If instead** you see `no fresh case after repeated enables` with no new murders, or any NRE after the break,
   tell me - the teardown or the scheduler re-enable needs more work.
6. **Migration / bricked-save heal:** if you still have (or can make) a save that is ALREADY stuck/over-paying from
   the earlier failed test, **load it with this build**: the recovery runs on load - watch for the same `[recover]`
   lines, confirm murders resume, and **save + reload once more** to confirm it comes back clean and the old case is
   no longer resolvable/over-paying.

## If all three pass
Merge `sniper-hotfix` into `v2`, bump the version + changelog, and ship. (Say the word and I'll do the merge +
packaging.)

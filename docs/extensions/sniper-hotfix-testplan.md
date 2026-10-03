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
- `[Troubleshooting] UnstickStalledMurders = true`  (gates the whole watchdog incl. the recovery - must be on)
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

## Test 3 - Bug 2 recovery (THE one that needs your eyes)
This is the item I couldn't fully verify statically: does `CancelCurrentMurder` on a fake-null case actually let
the scheduler roll a NEW murder? You test it by driving a case into the broken state and watching for recovery.
1. With a motivated sniper active and the victim selected in F9, make the victim OR murderer get destroyed out from
   under the case - e.g. kill the would-be murderer with another mod (the exact Nasty-Dog repro), or kill the
   victim.
2. Watch the log for: `[SODMotives][recover] the current murder has a null/destroyed victim or murderer ...
   cancelling to vanilla so the scheduler can resume (attempt 1).`
3. **Success = a NEW murder gets scheduled within ~a game-day** (murders resume; the pipeline is un-jammed). F9's
   sniper line will also show `BROKEN: game-side victim=NULL ...` while the case is in the broken state.
4. **If instead you see** `the broken murder is STILL current after repeated cancels` (logged after 3 attempts) and
   no new murders ever appear, then `CancelCurrentMurder` alone isn't enough on your build - tell me and I'll add a
   stronger scheduler reset (directly clearing the controller's current-murder pointers).
5. **Bricked-save heal:** if you still have (or can recreate) a save that's already stuck, just LOAD it with this
   build and watch for the same `[SODMotives][recover] ...` line and murders resuming - no action needed in-game.

## If all three pass
Merge `sniper-hotfix` into `v2`, bump the version + changelog, and ship. (Say the word and I'll do the merge +
packaging.)

# Motivated snipers — HANDOVER / RESUME (2026-09-24)

Focused resume point for the motivated-sniper feature. Full play-by-play (playtests 1-7 + vanilla decode) is in
`docs/extensions/sniper-recon.md` (bottom, dated sections). This file = the current state + what to do next.

## Status
- Branch **`sniper-wip`** (LOCAL, NOT pushed). Clean tree. Builds 0-warn, auto-deploys to the game's
  `BepInEx/plugins/SODMotives` when the game is CLOSED (`dotnet build -c Release`; restart game to load).
- Shipped already (branch `v2`, not this branch): 1.0.2 + motivated KIDNAPPINGS 1.1.0/1.1.1.
- Motivated snipers are the CURRENT feature. Not shipped, not merged to v2.

## What the code does now (current design — "defer to the game")
In `Patch_ExecuteNewMurder_Override` (Plugin.cs), for a SNIPER case:
1. Swap in the motivated pair (`TryPickVictimCentric`, no killer filter — any motivated pair).
2. Choose the MO by COHABITATION: if `killer.home == victim.home` (cohabiting), force the STREET MO
   **ExCopSniper** via the `ref motive` param (a public assassination — VoyeurSniper would be nonsensical when the
   killer lives with the victim). Non-cohabiting: leave the game's MO (usually VoyeurSniper).
3. `victimSite = null` — DO NOT pin. Let the game pick the site + vantage + shot itself.

Safety nets (MurderWatchdog): **SNIPER PATIENCE** (`ShouldBlockSniperCancel` via a Harmony prefix on
`MurderController.Murder.CancelCurrentMurder`) keeps an ExCopSniper alive past the game's give-up up to
`[Troubleshooting] SniperPatienceHours` (default 12 game-h), then force-cancels -> vanilla; VoyeurSniper cases use
the existing `waitForLocation` stall-cancel. So an un-snipeable victim falls back to vanilla, never hangs.

Config: `[Motive Mix] MotivatedSniperShare` (default 0; set 1 to motivate natural sandbox snipers). `[Debug]
EnableDebugKeys` gates the diagnostics + F-keys. F9 overlay shows the game's own SNIPE SITE / MO / sniperKillShotNode.

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

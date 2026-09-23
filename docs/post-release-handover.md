# Post-release handover (extensions + feedback)

**Branch:** `v2` · **Written:** 2026-09-21 · Companion to the auto-loaded memories `sod-prerelease-progress.md`
and `sod-motives-mod.md`. This is the resume point after the 1.0 release. The next session is for
**post-release extensions and incoming community feedback** (nothing concrete queued yet).

## Status: SHIPPED

- **Thunderstore (live):** https://thunderstore.io/c/shadows-of-doubt/p/opi/Better_Leads_and_Motives/
  (team namespace `opi`, display name "Better Leads and Motives").
- **GitHub (public, MIT):** https://github.com/benjahirsh/SODMotives (default branch `v2`, latest `53a4410`,
  tag `v1.0.1`).
- **Steam Guide (live):** https://steamcommunity.com/sharedfiles/filedetails/?id=3804929108
  ("[MOD] Better Leads and Motives: a vanilla+ experience").

## ⚠️ Read before doing anything

1. **NO AI ATTRIBUTION on this repo.** The git history was rewritten to strip all `Co-Authored-By: Claude`
   trailers before going public. **Do NOT add that trailer (or any 🤖 line) to commits here.** (This
   overrides the default attribution reminder for this project.)
2. **Dev env is RESTORED and building.** The manual BepInEx is back at the game root; `dotnet build -c Release`
   works and auto-deploys to `<game>/BepInEx/plugins/SODMotives` when the game is CLOSED (restart the game to
   load a new DLL). An r2modman profile also exists (in `%APPDATA%\r2modmanPlus-local\ShadowsofDoubt\`) for
   player-fidelity testing; launching through r2modman re-injects its own doorstop, so both modes coexist.
3. **Working tree:** commit the handover (this file) and you're clean.

## Version state — confirm before shipping any update

- **v1.0.0 is live on Thunderstore.** `v1.0.1` (the config-overlay-opens-on-backquote fix) is committed +
  tagged locally, but **it was not confirmed uploaded** — check the Thunderstore page's latest version. If it
  still says 1.0.0, ship 1.0.1 (rebuild the zip + upload; see Packaging).
- **Thunderstore has no in-place edit.** Every update = bump `manifest.json` `version_number`, rebuild the zip,
  and re-upload under the same team (`opi`) + name (it lists as a new version, not a new package). The
  Changelog tab reads `CHANGELOG.md` from the uploaded zip.
- The old, misnamed `SODMotives` Thunderstore package should be **deprecated** if it hasn't been.

## Packaging recipe (rebuild the upload zip)

The zip is assembled by hand (no build step packages it). Contents, with **forward-slash** entry paths:

```
manifest.json
icon.png
README.md
CHANGELOG.md
LICENSE
BepInEx/plugins/SODMotives/SODMotives.dll        <- from bin/Release after `dotnet build -c Release`
BepInEx/config/com.sinai.BepInExConfigManager.cfg <- from packaging/config/ (sets overlay toggle to backquote)
```

Name the zip `Better_Leads_and_Motives-<version>.zip`. (Windows PowerShell's `Compx-Archive` writes
backslash entry paths that r2modman mishandles — build the zip with `System.IO.Compression.ZipArchive` and
`CreateEntry("BepInEx/plugins/...")` so paths use forward slashes. There's a working PowerShell snippet in the
session history; re-derive it if needed.)

## Feedback + bug-report channels

Reports will come via **Thunderstore comments**, **GitHub issues**, and **Steam guide comments**. For bugs,
ask the reporter for the **F9 case-solution overlay** contents (a screenshot) — it shows case type, state,
killer/victim, motive, and injected clues, which is the fastest triage. `[Debug] ShowSuspectPoolAndKnowers`
adds the full pool + knower lists for deeper reports.

## Extension backlog (candidates for the next session)

- **Natural-language name references in gossip.** Gossip currently relays **full names** (`citizenName`, via
  `Interrogation.Name` / `SocialEvent.cs:343`) because spoken bubbles can't be clickable and SoD has name
  collisions, so a full name keeps the lead findable. Idea: soften to **first name + a role/context tag**
  ("their coworker, Dave") for immersion while staying findable, optionally behind a config toggle
  (full / first+descriptor / first-only). Anchors are named in `Interrogation.ComposeLines` (`JoinNames`).
- **Red-herring clues from the victim's OTHER (non-target) events** so the clue pile doesn't telegraph the
  motive (like gossip already does). Recon: `docs/extensions/red-herring-clues.md`.
- **Theft motive** — a new motive family.
- **Address-book / call-history lead** — recon done in `docs/a6-callhistory-recon.md` (call log persists
  natively; address-book half is larger/uncertain).
- **Layoffs clue granularity** — currently one "Redundancy List" naming all laid-off; consider per-NPC
  termination notices (like promotion's per-rival threats) or a hybrid.
- **Boss/promotee victim rate slider (Promotion cases).** Promotion cases can make the victim a boss/promotee;
  there are relatively FEW bosses in a city, so killing them off depletes the pool over time. Add a config
  slider to bias promotion-case victim selection AWAY from bosses/promotees (kill fewer of them) — e.g. a
  weight/probability that a promotion case targets a rival instead of the boss/promotee, or a cap on
  boss-victim frequency. Selection is in `MurderSelector.TryPickVictimCentric` + `WorkplaceSim` (Promotion
  edges r→P and r→P's boss D; victim currently = P or D). (Requested by user 2026-09-22.)
- **Kidnap ransom "thanks for the money" call fires even when the victim was RESCUED (not paid).** Confirmed
  OURS, not vanilla (vanilla gives no such call on a rescue). Our `MurderWatchdog.SpawnRansomNote` kicks off the
  game's own ransom chain (spawn note → TriggerKidnappingCase → downstream); in our case that chain reaches the
  COLLECTED state (`MurderController.KidnapperCollectedRansom` / `SetRansomPhase(collectedRansom/finishedSuccess)`)
  even without the player paying. Fix: trace where/why our `Murder.ransomPhase` reaches `collectedRansom` for a
  rescued victim and gate the collection (or force `ransomPhase` to a terminal non-collected state on rescue).
  Cosmetic — the case still solves. Also (minor, same area): when the rescued victim is asked about the ransom
  note, their name renders BLANK (vanilla names them in 3rd person) — our swapped victim doesn't resolve into the
  note's name token. (Found by user 2026-09-23.)
- **"Ask your partner to come to the door" tweak.** (Requested by user 2026-09-23; scope to be detailed — a way
  to get a specific NPC, e.g. a suspect's or victim's partner, to come to the door rather than whoever the game
  sends.)
- **Bias the default murder/kidnap mix AWAY from rare/structural NPCs (bosses + landlords).** Rare NPCs (company
  owners/directors = bosses, property landlords) are costly to lose from the world: as a MURDER victim they're
  deleted; as a caught KILLER they're arrested/removed. Improve the default selection so fewer of them are
  murdered or become the arrested killer. Conversely a KIDNAP victim can be SAVED, so *kidnapping* such an NPC is
  preferable to murdering them (the world keeps them if the player rescues them) — i.e. skew kidnaps TOWARD
  boss/landlord victims and murders AWAY from them. Generalises the "Boss/promotee victim rate slider" item below
  to landlords, to killers-arrested, and to the murder-vs-kidnap split. Selection lives in
  `MurderSelector.TryPickVictimCentric` (motive-family weights map to roles: Workplace→boss, Property→landlord;
  branch on case type since kidnap vs murder is per-case). (Requested by user 2026-09-23.)
- More ideas: `docs/extensions/README.md`, `docs/v2-design.md`.

## Optional loose ends

- **README cross-links:** add the Thunderstore + Steam-guide links to the GitHub README (they don't
  cross-link yet). Ships to Thunderstore on the next version bump.
- **Compatibility triage list:** offered but not built — bucket the most-downloaded SoD mods into
  "overlaps with this mod's 3 systems (murder selection / evidence / interrogation) — test" vs "orthogonal —
  safe." Useful once feedback about conflicts starts arriving.

## Toolchain

`dotnet build -c Release`. Signatures: `ilspycmd` vs `<game>\BepInEx\interop\Assembly-CSharp.dll`. Method
bodies: Cpp2IL `2022.1.0-pre-release.21`. Config overlay: BepInExConfigManager (open with `` ` ``).

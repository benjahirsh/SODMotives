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

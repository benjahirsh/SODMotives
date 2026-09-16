# Pre-release plan (v2 → first release)

**Branch:** `v2` · **Created:** 2026-09-16 · Companion to the auto-loaded memory `sod-motives-mod.md`
and `docs/extensions/` (post-release backlog). This is the tracked checklist to get from "feature-
complete + verified in-game" to "shippable." Trust but verify any symbol/line before editing.

## How to use / ordering
Work top-down through the phases. **Phases A–B are safe to do while still playtesting.** **Phase D is
end-stage: it removes the F3–F12 test loop and flips the out-of-box experience, so do it LAST**, after
the final playtest (Phase C). Line numbers are as of this writing — re-check before editing.

Legend: `[ ]` todo · size **S/M/L** · ⚠️ = disturbs the test loop (defer to Phase D).

---

## Phase A — Content & robustness (safe during testing; highest player-facing value)

- [ ] **A1 — Gossip writing pass** (M) *(user-requested 2026-09-14; bigger than a tweak)*
  Testimony reads templated and **overuses em-dashes ("AI giveaway")**. Naturalise across ALL motive
  types: `SocialEvent.TestimonyAbout` (`SocialEvent.cs`), `Interrogation.AffairLine` +
  `Interrogation.ComposeLines` (`Interrogation.cs`), and the workplace/property/feud/debt phrasings.
  - Cut em-dashes; add lexical variety picked **deterministically per (npc,subject) seed** (re-asking
    stays stable — mirror the existing `Pick(seed, …)` pattern).
  - Make multi-line delivery conversational (a follow-on "…and I heard they also fell out with Y"),
    not a second identically-shaped sentence.
  - **Optional, do together:** collapse many feud gossip bubbles into ONE list (like affairs/debt) in
    `Interrogation.ComposeLines` ("…fallen out with X, Y and Z"). Debt was already collapsed; feud
    wasn't.

- [ ] **A2 — Non-killer-knower guarantee** (M) *(robustness; open task-chip since v2.3.1)*
  A socially-isolated lone Debt/Feud/RentArrears victim can leave the **killer as the only person who
  both knows the victim's name AND the motive event** → no honest independent lead. Guarantee an
  independent name-knower at event materialization (or selection). Anchor: the knower plumbing in
  `Gossip.Distribute` / `EventStore` + the F9 knower logic in `DebugTools.AddVictimKnowers` shows how
  "knows name + knows event" is computed.

- [ ] **A3 — In-game menu config** (L) *(new work package; needs recon first)*
  Today config is BepInEx `.cfg` only (`Config.Bind` in `Plugin.cs`). The project references **no**
  `SOD.Common` or config-menu lib yet.
  - **Recon:** compare (a) `SOD.Common` (community lib — check whether it exposes an in-game settings
    menu, not just save/time hooks), (b) the generic BepInEx **ConfigurationManager** overlay (quick
    baseline, dev-flavoured, separate user install), (c) a custom Unity IMGUI menu hung off the game's
    own options. Decide dependency vs roll-our-own.
  - **Build:** surface the existing `ConfigEntry`s through the chosen path. Ideally lands before Phase D
    so every knob is menu-exposed.

- [ ] **A4 — Decide: workplace/property participant-knower exemption** (S, design call)
  Currently a uniform "a participant isn't a knower of their own event" skip. For multi-party events
  (layoffs/eviction) `TestimonyAbout` names the SUBJECT's role, not the npc, so an involved
  coworker/tenant gossiping doesn't self-incriminate — a type-scoped exemption (skip-if-party only for
  Affair/Feud/Debt/RentArrears) is defensible. ~one-line change in `Interrogation.ComposeLines` if
  wanted. Decide, then apply or close.

---

## Phase B — Code hygiene (safe anytime, low risk)

- [ ] **B1 — Delete legacy selector knobs** (S) — the 4 bound-but-unused fields + their `Config.Bind`s:
  `TopPoolSize`, `RedHerringBonusPer`, `WeightExponent`, `SameTypePenalty`
  (`MurderSelector.cs:29-32`, `Plugin.cs:45-52`).
- [ ] **B2 — Dead DDS paths** (S) — resolve `ProbationNoticeTreeId` (defined, unused,
  `ClueInjector.cs`) and the documented-broken `UseMultiPageList` path (`ClueInjector.cs:18` +
  `TryMultiPageList`): keep the custom single-list, delete the dead code. Optionally trim the
  never-firing `InjectEmail` `ProgressVmailThread`/hand-populate fallbacks (per `v2.6-email-recon.md`).
- [ ] **B3 — W5 completeness** (S) —
  - Bind `Interrogation.Enable` to config (currently hardcoded `true`, `Interrogation.cs:24`) under a
    new `[Interrogation]` section.
  - Repoint the last affair-only nearest-knower log (`Plugin.cs:478`, `AffairByVictim`) →
    `EventByVictim` + `evt.NearestKnowers(...)` so workplace/property kills log knowers too. Log-only.
- [ ] **B4 — Cosmetic labels** (S) — MOOT if the debug overlay is gated out (Phase D). If any survives:
  eviction `PropertySim.PlaceName` F9/log label can name an address not on the notice (debug-string
  only; clue+gossip+selection are correct); F9 "basement 03" vs "unknown address" — use
  `NewGameLocation.name`.

---

## Phase C — Final playtest at production balance

- [ ] **C1 — Confirm/tune balance knobs, then playtest each motive type.** Set candidate production
  values and run each of Affair / Promotion / Layoffs / Eviction / RentArrears / Feud / Debt (F6 to
  force while F-keys still exist), checking clue pile + gossip + solvability. Current values to confirm:
  - `[Selection]` MinSuspects 3, KillerPoolSize 10, WorkplaceCaseShare 0.5, NameKnownThreshold 0.35
  - `[Property]` PropertyCaseShare 0.25, EvictionShare 0.6, EnableProperty true (production)
  - `[Feud]` FeudCaseShare 0.15, MaxFeuds 40, MaxDebts 40  → (affair = the ~0.10 remainder)
  - `[Workplace]` PromotionShare 0.4, MaxSuspects 5
  - `[Clues]` EmailClueShare 0.5, WorkplaceClueShare 0.5, FingerprintChance 0.7, MaxCluesPerCase 8

---

## Phase D — Release gate (END-STAGE — do LAST; this removes the test loop) ⚠️

- [ ] **D1 — Flip testing defaults** (S) ⚠️
  - `DebugTools.ForceMotiveType = Money` → `None` (`Plugin.cs:103`); ideally make it config-driven
    (a `[Debug]` string, default `None`) so F6 layers on top.
  - `ObviousTestNames` default `true` → `false` (`Plugin.cs:90`).
  - `VanillaCaseEvery` default `0` → `3` (`Plugin.cs:55`).
  - Reset the live `.cfg` `MaxCluesPerCase` 4 → 8 (code default is already 8) — or delete the `.cfg`
    so it regenerates at defaults.
- [ ] **D2 — Gate/remove dev tooling, but KEEP ONE diagnostics key** (M) ⚠️ — put the F3–F12 hotkeys +
  the always-on `OnGUI` HUD (`DebugTools.cs` / `DebugHotkey`) behind a `[Debug] EnableDebugKeys` flag
  (default false), or compile them out — **except retain one always-available diagnostics key** so a
  user who hits a problem can relay **what event/motive type the case is + which clues were injected**
  (and where). `DebugTools.BuildSolution` (F9) already assembles CASE TYPE + `INJECTED CLUES`, so reuse
  it (or a trimmed variant) as the retained key.
  - **Sub-decision:** strip the KILLER / SUSPECT POOL / knower lines from the retained readout so it is
    NOT a solution-spoiler — event type + injected clues (+ locations) + murder state are enough for a
    bug report. Keep a small on-screen hint naming the key.
  - Un-repurpose F4 as part of the gating.
- [ ] **D3 — Strip diagnostic logging** (S) ⚠️ — remove the pure-diagnostic
  `Patch_Evidence_AddFactLinkExe` patch (`Plugin.cs:357-382`; the real removal is in the
  `AutoCreateFacts` postfix, untouched), the `clue[diag]`/`email[diag]`/`email[ref]` lines, and trim the
  verbose per-suspect pool + `CityScan` dumps to a sane release level.
- [ ] **D4 — Final build + smoke test** (S) — confirm a fresh `.cfg` regenerates with all sections +
  correct defaults; build clean; one clean sandbox → murder → solve with debug off; save/reload once.

---

## Out of scope for this release (pointers only)
- **Post-release extensions:** red-herring clues, theft motive, address-book/call-history lead,
  feud-bubble collapse (if not folded into A1) — see `docs/extensions/`.
- **Future (design doc):** petty-crime job board (3.0), ambient pre-murder gossip, grow-workplace-
  events-over-time, simulate who-knows / NPC lying / foggy testimony — see `docs/v2-design.md`.

---

## Status log
- 2026-09-16 — plan created. Nothing in Phases A–D started yet.

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

- [x] **A1 — Gossip writing pass** ✅ **DONE 2026-09-16** (builds clean; NOT yet playtested)
  - **Finding:** the player-facing gossip variants have **no em-dashes** — every em-dash was in comments
    or log strings, so the "AI giveaway" concern was already clean. Lexical variety (seeded `Pick`)
    already present too.
  - **Done:** added `DedupeOpeners` — after the first bubble, a repeated "Word is …" opener becomes a
    conversational follow-on ("I also heard …" / "And …" / "On top of that, …"), so a multi-bubble
    answer no longer reads as several identically-shaped sentences (the documented ask). Combined with A5
    (collapse), repetition is much reduced.
  - **Not done (not needed):** a line-by-line rewrite of the individual variant strings — they already
    read naturally. Revisit only if playtesting shows a specific phrasing grating.

- [~] **A2 — Non-killer-knower guarantee** — **DEFERRED 2026-09-16 (build-if-needed).** Lowering
  `NameKnownThreshold` 0.35 → 0.2 (see below) makes independent name-knowers the norm, so the "killer is
  the only lead" case shrinks to a rare, isolated-victim edge case. **Detection:** on a Feud/Debt case,
  F9's **VICTIM KNOWERS** list is exactly "non-participant NPCs who can name the victim + know a motive
  event" — if it's ever EMPTY, that's a leadless case. Only then build the fix (branch 2 below). The
  playtested feud case already produced a real independent knower who named the killer, so this is not
  expected to be common.
  *(robustness; open task-chip since v2.3.1; familiarity symmetry considered and DECLINED)*
  **Original scope:** a socially-isolated lone Debt/Feud/RentArrears victim can leave the **killer as
  the only person who both knows the victim's name AND the motive event** → no honest independent lead.
  Guarantee an independent name-knower at event materialization (or selection). Anchor: the knower
  plumbing in `Gossip.Distribute` / `EventStore` + the F9 knower logic in `DebugTools.AddVictimKnowers`.

  **Broadened — the gossip layer is gated by name-FAMILIARITY, and a suspect's MOTIVE edge does not
  imply it.** Two gates sit in front of every gossip line:
  1. **Vanilla identification** — the NPC must be able to identify the subject's photo ("do you know
     this person?" must succeed) for our hook to even arm (`Interrogation.ArmForPick` arms only on
     `success==true`).
  2. **Our `KnowsName(npc, subject) ≥ 0.35`** (directed acquaintance `known`).
  A suspect's motive comes from the EVENT edge (laidoff→boss, tenant→landlord, …), which exists
  regardless of familiarity — so a valid suspect (or the killer) can have a strong motive yet fail both
  gates. **A4 sits AFTER both gates, so it cannot help there.**

  **Concrete findings (A4 playtest 2026-09-16):**
  - **Killer silence is a tell.** In a layoffs case, every laid-off suspect relayed the layoffs EXCEPT
    the killer, because `KnowsName(killer→victim) < 0.35` (peripheral killer). The killer being the ONE
    silent suspect inverts the locked rule *"the killer reads no guiltier than a herring."* So A2 must
    ensure the killer/victim are embedded enough that the killer isn't the odd one out — familiarity
    **symmetry**, not merely "some independent knower exists."
  - **Eviction suspects can't ID the victim.** In an eviction case, NO tenant suspect could identify
    the landlord (the killer "never even saw them"): tenants often have too-weak an edge to their
    landlord (a `landlord` connection exists but with low `known`), so they fail **gate 1**, gossip
    never arms, and A4 is a **no-op for eviction**. (F8 wouldn't help — it forces gate 1 but gate 2
    still rejects.) The case stays solvable via the redevelopment-plan clue + neighbor gossip (as
    designed), but suspect-side interrogation is thin. A4 mainly benefits layoffs/promotion (coworkers
    know their boss + each other), not eviction.

  **Decisions (2026-09-16):**
  - ✅ **DECIDED — do NOT modify acquaintance `known` weights.** No familiarity-boosting of
    killer↔victim or suspect↔victim. Accept **clue-led eviction** (the redevelopment-plan clue +
    neighbor gossip carry the case) and accept the **killer-silence tell** + thin eviction suspect-side
    interrogation as within tolerance. A4 stands as-is (benefits layoffs/promotion; a no-op for
    eviction, which is fine).
  - ⏳ **Still in scope — the ORIGINAL guarantee, implemented WITHOUT touching `known`:** for a lone
    Debt/Feud/RentArrears victim who would otherwise leave the killer as the only name-knower of the
    motive, ensure an INDEPENDENT knower by taking an EXISTING name-knower of the victim (someone
    already at `known ≥ 0.35`) and marking them as knowing the event (`EventStore.MarkKnown`), and/or by
    PREFERRING victims that already have such a knower at selection. No weight edits, no fabricated
    familiarity. If a victim has no existing independent name-knower at all, fall back to leaving the
    case rather than inventing one.
  - Optional: a temporary `ComposeLines` diagnostic (log *why* a line was suppressed — gate-1 vs gate-2
    vs no-event) to confirm behavior per-interview; strip in Phase D.
  - **`NameKnownThreshold` 0.35 → 0.2 (2026-09-16, live cfg + code default, built, committed).** Widens
    gossip/knower coverage (more interactions). **Vanilla-recognition recon (Cpp2IL):** the "do you know
    this person?" recognition path (`PhotoSelectButtonController.OnLeftClick`) gates purely on
    **acquaintance-edge EXISTENCE** (`Human.FindAcquaintanceExists`) with **NO `known` threshold** (the
    type has zero `Acquaintance.known` references); the dialog `success` flag is only the willingness/
    bribe roll, computed before the subject is even picked. Our `KnowsName` = `FindAcquaintanceExists &&
    known ≥ threshold`, so it is ALWAYS a subset of vanilla recognition → **lowering the threshold can
    never contradict a vanilla "I don't know them"; it's purely a taste dial** ("how faint an
    acquaintance may gossip"). Safe to go lower (≈0.10 ≈ vanilla) for even more chatter. Tune in Phase C.

- [ ] **A3 — In-game menu config** (L) *(new work package; needs recon first)*
  Today config is BepInEx `.cfg` only (`Config.Bind` in `Plugin.cs`). The project references **no**
  `SOD.Common` or config-menu lib yet.
  - **Recon:** compare (a) `SOD.Common` (community lib — check whether it exposes an in-game settings
    menu, not just save/time hooks), (b) the generic BepInEx **ConfigurationManager** overlay (quick
    baseline, dev-flavoured, separate user install), (c) a custom Unity IMGUI menu hung off the game's
    own options. Decide dependency vs roll-our-own.
  - **Build:** surface the existing `ConfigEntry`s through the chosen path. Ideally lands before Phase D
    so every knob is menu-exposed.

- [x] **A4 — Workplace/property participant-knower exemption** (S, design call) ✅ **DONE 2026-09-16**
  **Decision:** exempt MULTI-PARTY events (Promotion/Layoffs/Eviction) from the "a participant isn't a
  knower of their own event" skip; KEEP the skip for one-to-one events (Affair/Feud/Debt/RentArrears),
  whose testimony names the other party (a party would narrate themselves / confess their own motive).
  **Applied:** `Interrogation.ComposeLines` skip is now gated on a new `IsOneToOne(e.type)` helper
  (`Interrogation.cs`). Builds clean; **PLAYTEST VERIFIED 2026-09-16** — layoffs positive (suspect→victim,
  suspect→suspect, uninvolved control) + feud negative (parties stay silent) both pass. Eviction is a
  no-op (suspects can't ID the landlord — see A2, accepted).

- [x] **A5 — Feud gossip-bubble collapse** ✅ **DONE 2026-09-16** (builds clean; NOT yet playtested)
  A subject's feuds now collapse into ONE bubble ("Word is they'd fallen out with X, Y and Z.") via a new
  `feudOthers` list + `FeudLine` helper in `Interrogation.ComposeLines`, mirroring the affair/debt
  collapse — instead of one identical "falling-out" line per feud. `TestimonyAbout`'s Feud case is no
  longer routed from ComposeLines (noted in `SocialEvent.cs`); RentArrears still uses `TestimonyAbout`.

- [~] **A6 — Address-book / call-history "who to interview" lead** — **RECON DONE 2026-09-16**, full
  writeup in [`a6-callhistory-recon.md`](a6-callhistory-recon.md). Findings:
  - **Inspectable?** ✅ call log renders via the phone (`CallLogsContentController.building` →
    `NewBuilding.callLog : List<TelephoneController.PhoneCall>`); a call is `EvidenceTelephoneCall`.
    Address book = `Human.addressBook : Evidence`.
  - **Injectable?** call log = **likely clean** (construct `PhoneCall`, `building.callLog.Add`; vanilla
    precedent `TriggerCoverUpTelephoneCall`). Address book (Evidence) = **harder/opaque**.
  - **Persists?** ✅ **natively** — `BuildingStateSav.callLog` + value-type `PhoneCall`, so **no sidecar/
    reload engine** (the email feature's biggest cost is absent here).
  - **VERDICT:** call-log lead is **MEDIUM** (not "small") — native persistence shrinks it, but it
    carries vmail-class runtime risk (renderer finickiness + an unconfirmed player-access flow, pinnable
    only in-game). Address-book half is **larger/uncertain** → post-release.
  - **RECOMMENDATION:** keep A6 **post-release by default**; if wanted in v1, scope **only** the call-log
    half as a test-hook-first spike (retire the render + access unknowns before wiring). Not a safe
    drop-in. → **decision needed:** pre-release call-log spike, or return A6 to `docs/extensions/`?

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
- [~] **B4 — F9/log address labels** — *NOTE: F9 is now a RELEASE-retained diagnostics panel (D2), so
  these are correctness, not throwaway-debug.*
  - [x] **Eviction detail FIXED 2026-09-16** — each tenant suspect now names their OWN unit via new
    `SocialEvent.SafeAddr`, not the shared first-resident `PropertySim.PlaceName`
    (`SocialEvent.CollectEvictionEdges`). Builds clean. **Verify on a FRESH eviction case** — the pool
    `detail` is baked at selection and stored verbatim in the save (PB/MB lines), so already-selected
    cases keep the old text even across reload.
  - [ ] **RentArrears** — same `placeName` pattern in its detail ("their tenant at X …"); same fix
    (`SafeAddr(a)` = the victim tenant's unit) if wanted.
  - [ ] F9 "basement 03" vs "unknown address" quirk — use `NewGameLocation.name`.

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
- 2026-09-16 — plan created.
- 2026-09-16 — **A4 DONE** (builds clean, not yet playtested): multi-party events exempted from the
  participant-knower skip in `Interrogation.ComposeLines` via a new `IsOneToOne` helper.
- 2026-09-16 — **B4 eviction address FIXED** (builds clean, verify on a fresh case): per-tenant unit in
  the eviction motive detail (`SocialEvent.CollectEvictionEdges` + `SafeAddr`).
- 2026-09-16 — **A5 (feud-bubble collapse) + A6 (address-book/call-history lead)** moved up from
  `docs/extensions/` into Phase A to scope their complexity.
- 2026-09-16 — **A4 playtest (partial):** layoffs — involved non-killer suspects now relay gossip
  (A4 works); killer was the only silent suspect (`KnowsName` gate). Eviction — B4 F9 address fix
  CONFIRMED in-game; tenant suspects can't ID the landlord, so gossip never arms (A4 no-op for
  eviction). Both findings rolled into **A2** (two-gate structure, killer-silence tell).
- 2026-09-16 — **A4 layoffs FULLY VERIFIED:** suspect→victim ✅, suspect→non-killer-suspect ✅ (Annie
  relayed a co-suspect's layoff; others gated by `KnowsName`, expected), uninvolved-knower control ✅.
- 2026-09-16 — **A4 Feud negative PASSED (party-side) → A4 VERIFIED.** All feud suspects could name the
  victim (both gates pass) yet gave NO feud gossip → the `IsOneToOne` skip suppresses a party's
  third-person self-narration exactly as intended (an un-skipped feud would have each suspect narrate
  "bad blood between them and <themselves>", since `TestimonyAbout(victim)` names the other party = the
  interviewee). **Contrast also CONFIRMED:** an uninvolved feud knower relayed the feud and NAMED the
  killer — so the suspect silence is definitively the skip, not broken/missing gossip. A4 airtight.
- 2026-09-16 — **A1 + A5 DONE** (builds clean, NOT yet playtested): A5 collapses a subject's feuds into
  one bubble (`FeudLine` in `Interrogation.ComposeLines`); A1 adds `DedupeOpeners` (repeated "Word is …"
  → conversational follow-on). Finding: gossip strings had NO em-dashes (only comments did), so the
  writing pass was lighter than expected. A1 wording later revised to the user's exact copy (widened
  framings) + committed (`ffc21e1`).
- 2026-09-16 — **`NameKnownThreshold` 0.35 → 0.2** (live cfg + code default + committed). Cpp2IL recon:
  vanilla "do you know this person" recognizes by acquaintance-edge EXISTENCE, no `known` threshold — so
  our threshold is a subset of vanilla, no contradiction risk, purely a taste dial. See A2 entry.
- 2026-09-16 — **A2 DEFERRED (build-if-needed):** 0.2 makes independent knowers the norm; only build the
  `MarkKnown` rescue if a Feud/Debt case ever shows an empty F9 VICTIM KNOWERS list.
- 2026-09-16 — **NEXT: A3 (in-game menu config)** — recon-first; see the A3 entry + the /clear resume
  prompt handed to the user.
- 2026-09-16 — **A2 decision:** accept clue-led eviction; do NOT modify acquaintance `known` weights
  (killer-silence tell + thin eviction suspect interrogation accepted). Original independent-knower
  guarantee stays in scope but must be met without weight edits (`MarkKnown` an existing name-knower /
  prefer such victims at selection).
- 2026-09-16 — **A6 recon DONE** (`a6-callhistory-recon.md`): call log = per-building
  `NewBuilding.callLog`, persists natively (`BuildingStateSav`), injectable via `PhoneCall` + list add
  (vanilla precedent `TriggerCoverUpTelephoneCall`). Verdict: MEDIUM spike (vmail-class runtime risk),
  address-book half larger. Recommend post-release unless a call-log-only spike is wanted; **decision
  pending.**

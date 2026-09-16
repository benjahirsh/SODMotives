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
  - **Pairs with A5** (feud gossip-bubble collapse) — same file (`Interrogation.ComposeLines`); best
    done in the same pass as the naturalisation.

- [ ] **A2 — Non-killer-knower guarantee** (M) *(robustness; open task-chip since v2.3.1; A4-playtest
  findings + decision recorded 2026-09-16 — familiarity symmetry considered and DECLINED)*
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
  (`Interrogation.cs`). Builds clean; NOT yet playtested (confirm involved coworkers/tenants now relay
  workplace/property gossip, and one-to-one self-narration is still suppressed).

- [ ] **A5 — Feud gossip-bubble collapse** (S) *(moved up from extensions 2026-09-16; pairs with A1)*
  Collapse the multiple feud lines about one subject into a SINGLE bubble, the way affairs/debt already
  do — e.g. "Word is they'd fallen out with X, Y and Z." Today each feud event the subject is in yields
  its own bubble ("rounds off strangely" with many feud suspects). **Mechanism already exists:** mirror
  the `lovers` / `owedTo` / `owedBy` list-collapse in `Interrogation.ComposeLines` + add a
  `FeudLine(subject, others, seed)` like `AffairLine`. Low complexity; best done alongside A1.

- [ ] **A6 — Address-book / call-history "who to interview" lead** (L, RECON-FIRST)
  *(moved up from extensions 2026-09-16 to scope its complexity)*
  Surface the victim's phone contacts + call log as a lead to WHO to interview — mirroring how the clue
  pile + gossip already point at suspects. **Recon before committing** (this may drop back to
  post-release if the API doesn't cooperate):
  - Is an NPC's address book / call history exposed as INSPECTABLE evidence the player can read?
    (`AddressBookController`, `CallLogsContentController` via `TelephoneController.PhoneCall`;
    `Toolbox.GetMailbox()` = the physical mailbox.)
  - Can we INJECT entries (ensure the victim's contacts/log include the suspects + knowers), or is it
    read-only sim state?
  - Does it persist / round-trip on reload?
  Decide after recon: keep as a pre-release feature, or return it to `docs/extensions/`.

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
  eviction). Both findings rolled into **A2** (two-gate structure, killer-silence tell). Still to test:
  suspect→non-killer-suspect, and a non-involved knower control on layoffs.
- 2026-09-16 — **A2 decision:** accept clue-led eviction; do NOT modify acquaintance `known` weights
  (killer-silence tell + thin eviction suspect interrogation accepted). Original independent-knower
  guarantee stays in scope but must be met without weight edits (`MarkKnown` an existing name-knower /
  prefer such victims at selection).

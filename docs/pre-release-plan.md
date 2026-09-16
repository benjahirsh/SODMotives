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

- [~] **A3 — In-game menu config** (L) — **DECIDED + CODE DONE 2026-09-16** (builds clean + deployed;
  NOT yet verified in-game — overlay not installed). Full recon: [`a3-config-menu-recon.md`](a3-config-menu-recon.md).
  - **Recon result:** (a) `SOD.Common` = `.cfg`-binding sugar (`PluginController`/`IConfigBindings`), **no
    in-game UI** → rejected. (b) external **ConfigurationManager** overlay = the SOD market norm (ship
    `ConfigEntry`s + rely on BepInExConfigManager/ConfigurationManager; e.g. GameBalanceOptions). (c) custom
    IMGUI + (a 4th option) native new-game-menu injection = scoped and **declined** (largest, no modding
    API, most patch-fragile; the new-game screen is a fixed prefab of named `MainMenuController` widget
    fields + hardcoded `ModifiersController` bools).
  - **DECISION: Tier 1 — external overlay.** Player opens the overlay (incl. at the main menu, before New
    Game) to edit knobs. Chosen because most knobs are actually LIVE (only the 4 `[Feud]` seeding caps are
    new-game-time), so an overlay usable both in-game and at the main menu strictly dominates a new-game-only
    native panel.
  - **CODE DONE (`Plugin.cs` + `MurderSelector.cs`):** `BindApply` + `Config.SettingChanged` re-read
    refactor — every knob is now re-applied into its static field on change (overlay / .cfg reload), so
    edits actually take effect (next case; `[Feud]` caps next new game). New headline knob **`[Selection]
    MotiveCaseShare`** (0..1, default **1.0 = all-motive = current MAX**; lower mixes in vanilla via
    `MurderSelector.ShouldForceVanilla`, composing with `VanillaCaseEvery`). Local
    `ConfigurationManagerAttributes` class → 0..1 sliders, MotiveCaseShare ordered to top, legacy knobs
    hidden (still in `.cfg`).
  - **OVERLAY INSTALLED (2026-09-16):** BepInExConfigManager 1.3.1 (TeamSpyraxi, SoD-native) →
    `plugins/BepInExConfigManager/` (overlay + UniverseLib) + `patchers/BepInExConfigManager/` (patcher).
    Toggle **rebound F5 → BackQuote** (the game's F5 = quicksave) by pre-writing
    `config/com.sinai.BepInExConfigManager.cfg` — `[Settings]` / `Main Menu Toggle = BackQuote` (schema from
    decompiling the overlay: section const `CTG="Settings"`, key `KeyCode` default F5=286). **This overlay
    honours only `IsAdvanced`** (ignores `Order`/`Browsable`) — legacy knobs use `IsAdvanced=true`. Our debug
    **F5 (TriggerMurder) unbound** in `DebugTools` (method kept for later re-bind).
  - **KEYS CONFIGURABLE (2026-09-16):** all debug hotkeys are now a `[Debug Keys]` `KeyCode` section
    (`DebugTools.Key*`, live-rebound from the overlay's key-binders — BepInEx converts enums natively, so
    KeyCode binds regardless of load order). The MENU toggle key is the overlay's OWN `[Settings] Main Menu
    Toggle` (rebindable natively under the "BepInExConfigManager" category); deliberately NOT duplicated in
    our config to avoid two controls fighting over one setting.
  - **PENDING:** verify in-game — open with `` ` ``, edit `MotiveCaseShare`, confirm live-apply (next case)
    + New-Game reseed for `[Feud]`, and that a rebound debug key takes effect; then declare the overlay an
    optional/recommended dependency for release (as GameBalanceOptions does). If the pre-written keybind
    doesn't take, fall back = rebind in the overlay's own Settings ▸ Main Menu Toggle.

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

- [x] **B1 — Delete legacy selector knobs** ✅ **DONE 2026-09-17** — removed the 4 bound-but-unused fields
  (`TopPoolSize`/`RedHerringBonusPer`/`WeightExponent`/`SameTypePenalty`) + their binds + the now-unused
  `Hidden()` overlay helper. Builds clean.
- [x] **B2 — Dead DDS paths** ✅ **DONE 2026-09-17** — deleted the unused `ProbationNoticeTreeId` and the
  whole documented-broken multipage path: `UseMultiPageList` field + its guard + `TryMultiPageList` +
  `ResolveMultiPageItemPresets` + `ResolveMultiPagePreset` + `SafeEvPresetName` + the `_listItem*` fields
  (`ResolveConnectFactPreset`/`AddCitizenConnection` kept — shared by live paths). Custom single-list note
  is now the only layoffs path. Builds clean. **Deferred (optional):** trimming the never-firing
  `InjectEmail` vmail fallbacks — email is verified working, so low value / higher risk.
- [x] **B3 — W5 completeness** ✅ **DONE 2026-09-17** —
  - Bound `Interrogation.Enable` to a new `[Interrogation] EnableInterrogation` knob (live-editable).
  - Repointed the nearest-knower log from affair-only `AffairByVictim` → `EventByVictim` +
    `evt.NearestKnowers(...)`, so workplace/property/feud kills log knowers too. Log-only.
- [~] **B4 — F9/log address labels** — *NOTE: F9 is now a RELEASE-retained diagnostics panel (D2), so
  these are correctness, not throwaway-debug.*
  - [x] **Eviction detail FIXED 2026-09-16** — each tenant suspect now names their OWN unit via new
    `SocialEvent.SafeAddr`, not the shared first-resident `PropertySim.PlaceName`
    (`SocialEvent.CollectEvictionEdges`). Builds clean. **Verify on a FRESH eviction case** — the pool
    `detail` is baked at selection and stored verbatim in the save (PB/MB lines), so already-selected
    cases keep the old text even across reload.
  - [x] **RentArrears FIXED 2026-09-17** — the detail now names the tenant-victim's OWN unit via
    `SafeAddr(a)` (`SocialEvent.CollectRentArrearsEdges`), not the shared `placeName`. Verify on a FRESH
    rent-arrears case (the pool `detail` is baked at selection, so already-selected cases keep old text).
  - [ ] F9 "basement 03" vs "unknown address" quirk — use `NewGameLocation.name`. *(still open — cosmetic
    F9 label; deferred, low value.)*

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
- 2026-09-16 — **A3 recon DONE + DECIDED + CODE DONE** (`a3-config-menu-recon.md`; builds clean, deployed,
  NOT yet verified in-game). Market survey: ~90% of SOD mods ship `ConfigEntry`s + rely on the
  BepInExConfigManager/ConfigurationManager overlay (F5/F1); a few build bespoke IMGUI menus (Khundian's
  Debug Menu); nobody injects config into the native new-game menu. **Chose Tier 1 (external overlay).**
  Implemented the `BindApply`/`SettingChanged` re-read refactor (all knobs live), the `MotiveCaseShare`
  0..1 mix knob (1.0 = all-motive MAX = current), and a local `ConfigurationManagerAttributes` (sliders /
  ordering / hide-legacy). PENDING: install an overlay (recommend SoD-native BepInExConfigManager) + in-game
  verify; then declare it an optional dependency.
- 2026-09-16 — **A3 overlay installed + configurable debug keys + COMMITTED (`92a396b` on v2).** Installed
  BepInExConfigManager 1.3.1 (user-side, not in-repo), rebound its toggle F5 → BackQuote (F5 = quicksave).
  Added a `[Debug Keys]` `KeyCode` section (all debug hotkeys rebindable live via the overlay's key-binders);
  unbound debug F5 (was TriggerMurder). Menu CONFIRMED working in-game ("works awesome"). Remaining: verify
  MotiveCaseShare mixing over several murders + a rebound debug key; then declare the overlay an optional dep.
- 2026-09-17 — **Phase B (code hygiene) DONE** (builds clean, deployed; not yet committed). B1: removed the 4
  legacy selector knobs + `Hidden()` helper. B2: deleted `ProbationNoticeTreeId` + the whole broken
  multipage path (`UseMultiPageList`/`TryMultiPageList`/`ResolveMultiPageItemPresets`/`ResolveMultiPagePreset`/
  `SafeEvPresetName`/`_listItem*`); kept shared `ResolveConnectFactPreset`/`AddCitizenConnection`. B3: bound
  `Interrogation.Enable` (`[Interrogation]`) + repointed the knower log to `EventByVictim` (any motive type).
  B4: RentArrears detail now uses `SafeAddr(a)`. Deferred: the optional B2 email-fallback trim + the cosmetic
  F9 "basement 03" address quirk.

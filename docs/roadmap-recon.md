# Roadmap recon — W5 remainder, promotion-clue polish, C7b tree-cloning

**Date:** 2026-09-07 · **Branch:** v2 · Companion to the persistence recon
(`docs/save-reload-recon.md`). Line numbers are as of HEAD `70973b2` + this session's
uncommitted W4 edits; re-check before editing. These are **plans**, not applied code.

---

## 1. W5 — nearly closed

The F9/F12 workplace/type-agnostic repoint (reading `MurderSelector.EventByVictim`) is **done
this session**. Nearly every player-facing tunable is already bound in `MotivesPlugin.Load`
(`Plugin.cs:28-73`). What's left is small:

**Already bound:** `MurderSelector.{EnableOverride, MinSuspects, KillerPoolSize,
WorkplaceCaseShare, StripSignatures, VanillaCaseEvery, + legacy TopPoolSize/RedHerringBonusPer/
WeightExponent/SameTypePenalty}`; `WorkplaceSim.{Enable, MaxSuspects, PromotionShare}`;
`ClueInjector.{Enable, MaxClues, ObviousNames, FingerprintChance, WorkplaceClueShare}`.

**To close W5 (ordered):**

1. **Bind `Interrogation.Enable`** (`Interrogation.cs:24`, currently an unbound `true`). One
   genuinely player-facing feature toggle still not in config. Add under a new `[Interrogation]`
   section: `EnableInterrogation` (default `true`, "NPCs volunteer real gossip about affairs and
   workplace rivalries in the vanilla 'Do you know this person?' flow"). Additive, zero risk.
2. **Repoint the last affair-only debug log** at `Plugin.cs:394` (selection-time nearest-knower
   log) from `AffairByVictim` → `EventByVictim` + `evt.NearestKnowers(...)`, so workplace/
   promotion/layoff kills log their knowers too. This is the only debug/log site the F9/F12
   migration missed. Log-only, low risk.
3. **Make `DebugTools.ForceMotiveType` config-driven + default `None`** (currently hardcoded to
   `MotiveType.Professional` at `Plugin.cs:73`). Bind a string in a `[Debug]` section
   (`None`/`Infidelity`/`Professional`, parse to enum, fallback `None`). This is **both** a W5
   knob **and** a C7 release-blocker (release target is `None`, per `v2-design.md:336`). F6
   in-game cycling still works on top of it. *Note:* flips the out-of-the-box test experience from
   "every case forced workplace" to "any motive" — gate to the release milestone so it doesn't
   disrupt this session's F5/F6 loop.
4. **Decide `ClueInjector.UseMultiPageList`** (`ClueInjector.cs:18`, unbound `false`). Its
   true-path is documented broken (dumps profile tiles, no readable clue). **Recommendation: leave
   hardcoded false, do not expose** — it's a footgun.
5. **(Release-cleanup tail, optional now)** flip testing defaults at the release milestone:
   `Clues/ObviousTestNames` true→false, `Flavour/VanillaCaseEvery` 0→3/4; delete the four
   legacy binds the victim-centric selector no longer reads.
6. **Verify** the generated `com.benhirsh.sodmotives.cfg` gains the new sections with correct
   defaults and values flow bind→static.

`Gossip.cs` has no numeric knobs (hardcoded `ConnectionType` audiences — not tuning material).
`DebugTools.{Show,Ghost,AlwaysAnswer}` are runtime cheat toggles (F7/F8/F9), no bind needed.

---

## 2. Promotion-clue polish — bring it to layoffs quality

**Current state** (`ClueInjector.InjectPromotion`, `ClueInjector.cs:314-335`): markedly
lower-fidelity than layoffs. Threat notes reuse the **stock** `Dodgy_NoteRat` tree for *every*
rival (identical canned text, names no one, no clickable links, no explicit board fact, rival
print only at `FingerprintChance`); the letter reuses the **stock** `Employment Contract` tree
(generic, promotee not named in a custom body, no link, no explicit board fact). It does **not**
use the `BuildRedundancyDocTree` custom-doc engine. Interrogation already handles Promotion
(`SocialEvent.TestimonyAbout`), so this upgrade is self-contained in `ClueInjector`.

**Plan (ordered):**

1. **Extract a reusable engine** from `BuildRedundancyDocTree` (`ClueInjector.cs:374-501`):
   - `RegisterCustomDocTree(string body, out string treeId)` — the registration+layout core
     (Strings `dds.blocks` write; block/message/tree into the Toolbox dicts; Probation_Notice-
     cloned layout). **Add the optional `fixedTreeId` param here** — it's also what the
     save/reload fix needs (see persistence doc).
   - `CitizenLink(Human h, ref int linked)` — the `evidenceEntry → AddOrGetLink → <link=id>Name</link>`
     pattern. Rewrite `BuildRedundancyDocTree` to use both so layoffs output is byte-identical.
2. **`BuildPromotionLetterTree(promotee, boss)`** — custom body naming the promotee via
   `CitizenLink`, signed "Issued by: " + `CitizenLink(boss)`.
3. **Rewrite the letter placement** (`InjectPromotion:327-333`) modeled on
   `InjectLayoffsCustomText`: `PlaceClue(victim, writer=boss, receiver=promotee, treeId)`,
   `SetWriter/SetDDSOverride`, boss `AddNewDynamicFingerprint`, clean title
   `AddOrSetCustomName(name,"Promotion Letter")` + blank tied keys, one `AddCitizenConnection`
   doc→promotee. **Sent/unsent:** `victim==boss` (writer) → unsent original at boss's desk →
   strip the promotee's auto-stamped prints; `victim==promotee` → delivered → keep them. *(Get
   this branch right — the recipient-print strip must run only in the unsent branch.)*
4. **Custom threat notes per rival** via the engine (`BuildThreatNoteTree`): a short menacing,
   **unsigned** note (a threat's tie is the print, not a signature) that references the promotion
   as context; **unconditional** `AddNewDynamicFingerprint(rival)` (per V2.1 "each carrying that
   rival's own prints" — NOT gated by `FingerprintChance`); each note gets a **unique tree id**;
   optional explicit `CreateFact` rival→victim (more reliable than the game's auto writer-fact).
   Keep the body flavour, not a giveaway.
5. **Close C7b for promotion + delete dead constants** (`PromotionLetterTreeId/Name`,
   `ThreatTreeId/Name` at 109-110/134-135) — routing every promotion notice through unique-GUID
   trees + explicit facts eliminates any tree-collision under either collision-rule reading.
6. **(Optional)** a promotion analog of F4/`SpawnTestCustomNote` to render-test without a live
   promotion murder.

**Caveat:** inherits the **same save/reload orphaning risk** as layoffs — fix once for both via
the persistence design.

---

## 3. C7b tree-cloning — a representation *choice*, not a live bug

**Important reframing from the recon:** the **shipped** `InjectLayoffs` already avoids
board-link collision by minting a **fresh-GUID tree per note** inside `BuildRedundancyDocTree`. So
C7b as literally specified (clone `Probation_Notice` into N ids) is an **alternative
representation**, not a bug-stop. Decide deliberately.

**The machinery already exists:** `CloneDDSTree(src,new)` (`ClueInjector.cs:48-99`) deep-copies a
loaded `DDSTreeSave` while **reusing** the source's message/messageRef/startingMessage refs — so a
clone renders pixel-identical with **zero** new block/message/Strings entries; only the tree id is
unique. `ProbationNoticeTreeId` (`:115`) is **defined but unused**, prepared for exactly this;
`_cloneSeq` (`:133`) is the monotonic counter (already used at 611/759 to clone
`Ev_PrintedEmployeeDB`).

**If you choose per-rival probation clones** (native prose, native 1:1 receiver board-links, zero
custom Strings/links):
- Per notice: `tid = CloneDDSTree(ProbationNoticeTreeId, "SODMotives_Term_" + (++_cloneSeq))`,
  `PlaceClue(victim, boss, rival, tid)`, `Finish(..., unsentDoc:true)` (strips the recipient's
  auto-print → only boss printed). Unique tree per notice → the `(writer=boss, tree)` pair is
  distinct → receiver links stay 1:1.
- **Answer to "does it need custom keys?"** No — the clone reuses `Probation_Notice`'s existing
  blocks/messages/Strings; `saidBy`/`saidTo` participant slots fill from the note's writer/reciever
  at render, so there's no in-body `<link>` markup and **no `AddOrGetLink`**. Only the tree id is
  per-notice-unique.

**Risks / decisions:**
- `CloneDDSTree` **falls back to the source id** if `Probation_Notice` isn't loaded in
  `allDDSTrees` at inject time (58-59) → every notice shares the real tree → collision returns.
  Confirm StreamingAssets DDS is loaded before the first layoffs inject (inferred; affair/promotion
  trees already resolve at inject time — supporting evidence).
- **Clue economy:** N per-rival notices consume N of `MaxClues=8` and N rooms; the one-clue-per-room
  rule (`_placedRooms`) will silently drop later rivals when home+workplace rooms run out. The
  current single-list model spends 1 slot/1 room. May need a per-event cap. Needs the W6 playtest.
- Shared `DDSMessageSettings` ref across many notes — if the renderer mutates `settings.links` per
  note they could interfere (unverifiable from declarations; but the same ref-sharing already ships
  for affair trees + the `Ev_PrintedEmployeeDB` clone with no reported issue).
- **Both** representations share the save/reload orphaning risk (the clone tree id is equally
  runtime-only).

**Decision to make:** ship the current custom single-list (`BuildRedundancyDocTree`, readable
clickable name-list, 1 slot) **or** per-rival probation clones (authentic prose, native links, N
slots). Either way, wire `ProbationNoticeTreeId` so it stops being dead code, and update the C7b
note in `v2-design.md:338-345` to record the resolution.

---

*Full agent output (with every cited line number) in the workflow transcript
`…/subagents/workflows/wf_5bee826c-fab/journal.jsonl`.*

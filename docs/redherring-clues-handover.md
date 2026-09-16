# Handover — red-herring clues from the victim's OTHER events

**Branch:** `v2` · **Date:** 2026-09-16 · Companion to the auto-loaded memory `sod-motives-mod.md` and
`docs/v2.6-email-recon.md`. Trust this, but **verify any symbol before relying on it** (regenerate interop
decompiles with `ilspycmd` against `<game>\BepInEx\interop\Assembly-CSharp.dll`; for method BODIES use Cpp2IL —
see Toolchain below).

This is the design brief + resume kit for the **next** work item. The V2.6 email work is DONE, verified in-game,
and committed (see "Where we are").

---

## Where we are (all committed on `v2`, working tree clean)
- `0864cf6` feat — physical-OR-email clue fork + vmail reload persistence.
- `b5cf745` docs — V2.6 wiring writeup (appended to `docs/v2.6-email-recon.md`).
- `8ae49f5` feat — promotion rival-threat variant wording.

**Email feature COMPLETE + verified in-game:** promotion (both channels: anon rival threats + boss→promotee
email), layoffs redundancy email, eviction redevelopment-plan email (clickable addresses), affair email, and F3
test-email save/reload persistence. RentArrears/Feud/Debt are always physical by design.

---

## THE TASK — inject red-herring clues from the victim's OTHER events

**User's goal (their words, 2026-09-16):** "have the injected clues in the victim's home in such a way that it
isn't obvious which motive we are dealing with … inject clues from other events the victim is involved in, in
the same way we do this for gossip."

**What already happens (don't rebuild it):** `ClueInjector.InjectForCase` already injects one clue-set **per
event in the victim's SUSPECT POOL** — i.e. every event that gives *someone a motive to kill the victim*
(`MurderSelector.PoolByVictim[victim]`, deduped to distinct `SocialEvent`s). So a victim who is the target of
several motives already gets a mixed clue pile (e.g. an affair letter + a debt threat). The killer's own event
is processed first.

**The gap this task fills:** events the victim is merely **involved in but is NOT the target of** — no suspect
edge points at them, so those events are absent from the pool and drop no clue today. Examples:
- the victim was a **lover** in an affair whose *target* is someone else (the betrayed partner);
- the victim was a **passed-over rival** in a promotion (they're a suspect against the promotee, not a target);
- the victim was a **co-tenant** in an eviction (target = the landlord);
- the victim **owed** someone money / had a feud where *they* were the aggressor.

Injecting a clue from one of these makes the clue pile suggest a *different* motive, so the pile no longer
telegraphs the real one. This mirrors gossip, which already surfaces **all** events involving a subject via
`EventStore` + `SocialEvent.InvolvesHuman`.

**Stay inside the V2.2 rule (LOCKED, see memory):** REAL-people-only, **no fabricated dead-ends**. A red-herring
clue must come from a **genuine** event with **real** people; misdirection is that a real person had a real
connection to the victim but an alibi/forensics clears them — never a made-up lead. This task is compliant by
construction (the events already exist in `EventStore`).

---

## Design questions to settle WITH THE USER before coding
1. **Which of the victim's other events qualify?** Candidate rule: `EventStore.All` where
   `e.InvolvesHuman(victim.humanID)` AND `e` is not already one of the pool events injected this case. Include
   all types, or exclude some (e.g. only affair/workplace/property make legible physical clues)?
2. **How many, and budget:** cap (e.g. 1–2 red herrings/case) + a config knob (`RedHerringClueShare` or
   `MaxRedHerringClues`). Inject them **after** the real pool clues so they can't starve the real motive, and
   respect `MaxCluesPerCase` (currently 4 in the live .cfg; code default 8).
3. **Whose forensics?** The clue should carry the **other party's** prints/handwriting (the real participant of
   that other event), NOT the killer's — so it points at a real, alibi-clearable person. Confirm.
4. **Board connections?** Drawing a doc→citizen/location connection actively points the player at the red-herring
   person. That's honest (real relationship, cleared by forensics) but stronger misdirection. Decide whether red
   herrings draw connections or stay ambient (body links only).
5. **Which clue FORM per role?** The victim's role in the other event differs from the pool case:
   - victim = a **lover** → an affair love-letter (from the victim to the other lover, or vice-versa);
   - victim = a **debtor/feud aggressor** → the "settle your debt"/feud threat note the victim *received* (the
     existing `InjectMoneyThreat`/`InjectFeud` already anchor to the debtor's/victim's home — reusable);
   - victim = a **rival/co-tenant** → a promotion-threat / eviction-adjacent clue.
   Reuse the existing per-type injectors where possible, but they were written for "victim is the target" — map
   the roles carefully (e.g. for an affair the victim isn't the pool target, so pick the right two Humans).
6. **Physical vs email?** Reuse the same `EmailClueShare` fork, or keep red herrings physical-only for simplicity?
7. **Persistence:** any red-herring clue that uses the custom-doc engine (`RegisterCustomDocTree` /
   `BuildEmailBody`) or an anon threat must be recorded via `Persistence.RecordNote`/`RecordEmail` with the right
   `kind` so it rebuilds on reload — same as the existing clues. Verify the reused injectors already do this.

---

## Where it hooks (entry points)
- **`ClueInjector.InjectForCase(murder)`** (`ClueInjector.cs`) — the clue orchestrator. Add a red-herring pass
  after the existing per-pool-event `foreach`. Gather the victim's involved-but-not-target events, cap them,
  route each to the appropriate injector (with the victim in its real role), still bounded by `placed < MaxClues`.
- **`EventStore.All`** + **`SocialEvent.InvolvesHuman(int)`** (`SocialEvent.cs`) — enumerate the victim's events;
  exclude the pool events already handled (dedupe by the `SocialEvent` refs already collected in `InjectForCase`).
- Existing per-type injectors in `ClueInjector.cs`: `InjectAffair`, `InjectMoneyThreat` (rent/debt),
  `InjectFeud`, `InjectPromotion`, `InjectLayoffs`, `InjectEviction`, and the shared `InjectAnonThreatNote` +
  `InjectEmailClue`. The custom-doc/email builders (`BuildEmailBody`, `Register­CustomDocTree`, the anon-threat
  builders) all already persist + rebuild via `Persistence` and `ClueInjector.RebuildCustomNote`/`RebuildEmailTree`.
- The clue location primitive is `PlaceClue`/`PlaceClueNote` (home OR work per `WorkplaceClueShare`, one clue per
  room via `_placedRooms`).

## Key facts (don't re-learn)
- The **suspect pool** = `MurderSelector.PoolByVictim[victim.humanID]` (`List<SuspectEdge>`); each edge has
  `.evt` (the backing `SocialEvent`), `.suspect`, `.type`. `InjectForCase` derives its distinct events from this.
- `SocialEvent` fields: `a`/`b`/`group`, `type` (`SocialEventType`), `knownBy` (humanIDs), `InvolvesHuman(id)`.
- Emails render from the **thread's stored `messages[i]`** (Cpp2IL-confirmed), which is why reload needs the
  tree re-register + thread resync. Any new email clue inherits this for free via `InjectEmailClue` + the
  `Persistence` `EM` line + `RebuildEmailTree`.
- Anon threat notes (`InjectAnonThreatNote`) set the writer's handwriting+print, hide the "From" via the
  `Evidence.AutoCreateFacts` postfix (tracked in `AnonWriterNoteIds`), and draw an explicit "To" to the recipient.

## Toolchain / iterate
- Build+deploy: `dotnet build -c Release` (deploys to the game's `BepInEx/plugins/SODMotives` only when the game
  is CLOSED; a running game locks the DLL → deploy warnings, restart to load). Log: `<game>\BepInEx\LogOutput.log`,
  filter `[SODMotives]`.
- **Cpp2IL for method bodies** (interop DLL = signatures only): Cpp2IL **2022.0.7 is TOO OLD** (game is IL2CPP
  metadata **v31**, that build supports ≤29). Use **`2022.1.0-pre-release.21`**:
  `Cpp2IL.exe --game-path "<game>" --output-as dll_il_recovery --use-processor attributeinjector,callanalyzer
  --output-to cpp2il_out` (comma-list, NOT repeated `--use-processor`), then `ilspycmd cpp2il_out/Assembly-CSharp.dll`.
  Method bodies come back as `throw null` stubs, but the `[Calls]`/`[CalledBy]` attributes give the call graph.
- Debug keys: **F6** now cycles a SPECIFIC event type (OFF/Affair/Promotion/Layoffs/Eviction/RentArrears/Feud/
  Debt) via `DebugTools.ForceEventType`; **F5** trigger murder; **F9** case-solution overlay (shows pool + injected
  clues + knowers); **F3** test email; **F4** test threat note; **F10/F11/F12** teleports; **F8** always-answer;
  **F7** ghost. HUD top-right shows the active force.

## Config currently primed for TESTING (revert list lives in W5 cleanup — see memory)
`ForceMotiveType`=Money (Plugin.cs startup default) / `ForceEventType`=null; `ObviousTestNames`=true;
`VanillaCaseEvery`=0; `MaxCluesPerCase`=4 (user set for 3 threats + 1 email); `EmailClueShare`=0.5.
`EnableProperty`=true + `EvictionShare`=0.6 are now at PRODUCTION values.

## Deferred (NOT this task — for later contexts; all in memory)
- **#3 smaller polish:** address-book/call-history as a lead to *who to interview* (needs recon — unknown API);
  guarantee a non-killer knower for socially-isolated victims; collapse many feud gossip bubbles into one (copy
  the existing debt-collapse pattern).
- **#4 W5 release cleanup:** revert the test config above; gate the F-keys; strip `AddFactLinkExe`/`clue[diag]`
  debug logging; the eviction **`placeName` F9/log cosmetic** (the eviction motive-detail uses
  `PropertySim.PlaceName` = the first building resident's unit, possibly a non-suspect/landlord, as a loose
  building label, so it can name an address not on the notice — debug-string only; clue+gossip+selection correct).
- **Theft motive** (recon done): rides the custom-doc engine as a stolen-goods document, no new object type.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

# SOD Motives — V2 (2.0.0) Design Spec

Status: **design locked** (this document); **implementation in progress on `v2`.**
`main` / tag `v1.0.0` remain the stable V1 build.
**→ Current status, the pending in-game test, and the atomic next-chunk plan are at the
bottom: [Status & resume kit](#status--resume-kit). Read that first when resuming.**

## One-sentence vision

> Make things actually **happen** between NPCs, leave **traceable trails**, and let
> NPCs **tell the player** about them — so a murder's motive can be reconstructed
> from real, discoverable social events instead of an invisible number.

V1 learned the hard way that the vanilla `like` value — which drove V1's motives —
**is not player-traceable** (it's a static, worldgen-derived number with no events
or evidence behind it). V2 replaces that hidden driver with **simulated social
events** that are, by construction, observable: witnessed by NPCs, gossiped about,
and left as physical/newspaper evidence.

## Guiding principles (carried from V1's lessons)

1. **Fun & novelty over realism.** A movie-like case beats a statistically-accurate one.
2. **Nothing drives the game that the player can't trace.** If a system isn't
   observable, simplify or cut it. No hidden probability deciding outcomes.
3. **Don't touch vanilla murder *timing*.** We only change *who*, never *when*
   (same `ExecuteNewMurder` hook as V1).
4. **Two-layer solving.** Events = the *motive/suspicion* layer (many true leads,
   most of them red herrings). Physical evidence (prints/CCTV/alibi) = the *guilt*
   layer (the single discriminator). A witnessed argument makes a suspect, never a killer.
5. **Build generic, ship one vertical.** Affairs first, end-to-end, on a framework
   that Workplace and Feuds slot into later.

## Locked decisions

| Area | Decision |
|---|---|
| World model | **Hybrid** — a few event types mutate the social graph (affairs form, firings), most are non-mutating happenings that still leave trails. |
| Murder origin | **Director picks killer/victim**, but only from a real, traceable event-backed motive. Vanilla timing untouched. |
| Vanilla fallback | If no motivated case is available → vanilla. **Plus a guaranteed vanilla case at least every N cases** (`VanillaCaseEvery`, default 3–4) so the classic serial-killer hunt never disappears. Deterministic counter, not a dice roll. |
| Knowledge spread | **Bounded gossip**, 1–2 hops, spreads **on real conversations** between NPCs who know each other. |
| Opinions | **Opinions ARE known events.** "A thinks B hates C" = A witnessed/was-told a B–C feud event. No hidden opinion value. |
| Affair knowledge (2.0.0) | **Assume known** — when an affair is used as a murder motive, the betrayed partner simply knows. The affair still leaves a full trail the *player* uncovers. (Simulating "who knows" is a later enhancement.) |
| Testimony | **Reliable** — NPCs accurately report what they witnessed/were told. Challenge is gathering & cross-referencing, not doubting witnesses. |
| Interrogation | **Real new questions + testimony**, via an **extensible question registry** (easy to add more later). Starter set below. |
| Foreshadowing | **Foreshadowed but not preventable.** Tension is discoverable before the murder; the murder still happens on schedule. (Prevention/vigilantism = a separate future mod.) |
| Event set | **Affairs (2.0.0)**, then **Workplace (2.1)**, **Feuds (2.2)**. |
| Event generation | **Observable with some randomness** — which pairs get an affair/feud can vary, but every resulting event is fully witnessable/traceable. |
| Cold start | **Seed existing + grow new** — treat worldgen affairs as already-in-progress with backdated discoverable history, and grow new affairs as the sim runs. |
| Determinism | Randomness allowed in *setup* (which pairs), never in *hidden outcomes*. |
| Petty-crime job board | Deferred to **3.0.0** (natural once events exist). |

## Architecture

### 1. Event framework (generic — built once)

```
SocialEvent {
    id
    type            // Affair | Workplace | Feud | ...
    participants    // A, B, (optional C)
    time            // when it happened
    location        // where (NewGameLocation / node)
    witnesses       // Humans who observed it (from presence at location/time)
    evidence[]      // trail artifacts produced (see §5)
    knownBy         // set of Humans who know (witnessed OR gossiped)
    motiveWeight    // how strong a murder motive this creates, and for whom
}
```

- An **EventType module** knows how to: generate/seed instances, produce evidence,
  compute the motive it creates and for whom, and phrase testimony about itself.
  Adding Workplace/Feuds = adding a module; the sim/gossip/director/interview are shared.

### 2. Event simulation (light, continuous, city-wide)

- Runs on a periodic tick (in-game time hook via SOD.Common).
- Each period, generate a small number of events among **existing** relationships
  (coworkers, neighbours, couples) — observable, low volume, relevant.
- **Affairs mutate the graph** (a new `paramour` link forms); most events don't.
- **Witnessing:** when an event fires at a location/time, NPCs physically present
  become witnesses (reuse the game's sighting system) and it enters their memory.
- Bounded so cost stays low (cap events/period; only real relationships considered).

### 3. Knowledge & gossip

- `knownBy` starts with participants + witnesses.
- **Gossip on conversation:** when two NPCs who know each other talk (the game's
  existing conversation moments), one shares a recent notable event they know →
  added to the listener's `knownBy`, capped at 1–2 hops from the original witness.
- The player can trace *how* someone knew (who saw it, who told whom).

### 4. The murder director (V2)

At `ExecuteNewMurder` (vanilla timing untouched):

```
if (forcedVanillaThisCase())          // counter: guarantee 1 vanilla per N
    return; // let vanilla run
candidates = allPairs(A -> B) where A holds a real event-backed motive toward B
            and B is a valid victim
prefer B with >= 2 motivated suspects  // guaranteed red-herring shortlist
weightedPick(candidates, variety)      // reuse V1 selector knobs
if (none) return;                      // natural vanilla fallback
override killer=A, victim=B            // reuse V1 ExecuteNewMurder swap
```

- The killer's motive is a concrete event chain (e.g. *A's partner B is having an
  affair with C*), so a real trail + testimony already exist.
- Red herrings = other people with real event-motives toward B (jealous exes,
  work rivals, feuding neighbours). Physical evidence discriminates.

### 5. Evidence / trail per event (the player's path)

Reuses V1 tech (note injection) + existing systems + the new testimony channel:

- **Sightings** → NPC memory & testimony ("I saw B and C at the diner Tuesday night").
- **Physical notes/letters** → V1 `ClueInjector` (document-type DDS), now tied to a
  *real* event (an actual love note from an actual rendezvous). Author prints/handwriting.
- **Gossip** → obtained via interrogation, traceable to its source.
- **Newspaper** (`NewspaperController`) → optional public foreshadowing for big events.

### 6. Interrogation (extensible)

Question registry (starter set; add more freely later):

- **"What do you know about X?"** — relationships + recent events they know about X.
- **"Have you seen X with anyone?"** — associations/sightings (uncovers affairs).
- **"Who'd want to harm X?"** — names locally-known people with friction toward X
  (builds the shortlist, including false leads).
- **"What do you know about X and Y?"** — targeted relationship query (enables
  "A thinks B hates C").

Answers are generated from the NPC's `knownBy` events. Testimony is reliable.

## Vanilla systems we build on (verified)

- **Sighting memory:** `Human.lastSightings : Dictionary<Human, Sighting>`,
  `UpdateLastSighting(...)`, `RevealSighting(..., SpeechController)`. `Sighting`
  records time/node/dest/moving/running/phone/talking-to. (Currently *last* sighting
  per person — V2 may extend toward a short timeline.)
- **Timeline events:** `TimelineEvent` + `TimelineEvent.EventType` (observational
  vocabulary: sightings, sounds, arrivals, timeOfDeath, questioned…). Fields incl.
  `happenedAt`, `timeAccuracy`, `discoveredByQuestioned`, location.
- **Interrogation:** `DialogController`, `SpeechController`.
- **Newspaper:** `NewspaperController`, `NewspaperArticle.Category`.
- **Relationships:** `Human.partner` / `Human.paramour` (affair state), `Human.acquaintances`.
- **Murder hook (unchanged from V1):** `MurderController.ExecuteNewMurder(...)` prefix.
- **DDS content tools (community):** DDSLoader (add/override DDS trees/messages/blocks),
  DDS Script Extensions (dynamic Lua text), PlayerDialogAdditions (player dialogue),
  SOD.Common (save/time hooks, helpers). Likely dependencies for the interview layer.

## Implementation investigation plan (technical unknowns to resolve BEFORE coding)

1. **Interview questions — the big one.** How to add new player→NPC questions and
   event-derived answers. Investigate `DialogController`/`SpeechController` + whether
   PlayerDialogAdditions/DDSLoader are needed, or if we can inject dialogue directly.
2. **Gossip trigger.** Find the conversation moment to hook (when two NPCs talk) to
   run the 1–2-hop share.
3. **Witnessing.** Confirm how to enumerate NPCs present at a location/time and write
   into their sighting/timeline memory so vanilla interrogation surfaces it.
4. **Time tick & persistence.** SOD.Common time hooks for the sim; save/load of
   `SocialEvent`/knowledge (SOD.Common save hooks) so it survives reloads.
5. **Dependency decision.** DDSLoader / PlayerDialogAdditions as hard deps vs. rolling
   our own. (Lean: depend on them — standard in the SoD scene — to avoid reinventing DDS.)

## Config knobs (planned)

`EventsPerPeriod`, `NewAffairRate`, `RendezvousRate`, `GossipHops`,
`VanillaCaseEvery` (default 3–4), plus V1's selector knobs (`WeightExponent`,
`TopPoolSize`, `RedHerringBonusPer`) reused for director variety.

## Deferred

- **2.1 / 2.2:** Workplace and Feud event modules.
- **Later 2.x:** simulate "who knows" (secret affairs, discovery as a discoverable
  step); NPC lying/withholding; unreliable/foggy testimony.
- **3.0.0:** petty crimes (thefts, etc.) on the job board.
- **Separate mod:** vigilante/prevention (act before the murder), inheritance/family
  feuds, framing & false accusations, accidental (Knives-Out) murders.

## V2.1 — Workplace rivalries + mixed-motive suspect pool (LOCKED 2026-09-04)

**The pivot.** V2.0 picks a *motivated pair* (killer→victim) and injects only that
motive's clue. V2.1 flips to **victim-centric**: choose a victim who has a **rich pool
of real, event-backed suspects** (different people, different motives), pick the **real
killer at random from that pool**, and seed **one clue-set per suspect's motive** at the
scene. Motive + gossip narrow the player to a shortlist; the **vanilla physical layer
(weapon prints / CCTV / alibi) — untouched — convicts the one.** Motive clues
*supplement* forensics, never replace them — which **organically reduces** (never
hard-prevents) lone-fingerprint cases — and the real killer looks **no guiltier than the
red herrings** at the motive layer. "Workplace rivalry" is therefore not a case *type* — it's a motive
*source* that feeds suspects into the shared pool, exactly like affairs.

**Selection algorithm** (replaces the affair-first / V1-fallback cascade at
`ExecuteNewMurder`; same gates: `procGenLoopActive`, `caseType==murder`,
`VanillaCaseEvery`):
1. Build citywide `(suspect → victim)` motive edges from **events only** (affairs +
   workplace). The V1 `like`-based `Motive.Score` / `TryPick` is **retired from
   selection** (it's the untraceable signal V2 exists to escape; keep only as a
   diagnostic if useful).
2. **Prefer** victims with **≥3 distinct real suspects** (`Selection/MinSuspects`, default 3).
3. Pick the victim **uniformly at random** among those. **No vanilla fallback for the
   floor** — the seeded graph is static, so gating to vanilla whenever the floor isn't met
   would risk *always* going vanilla; instead **degrade gracefully** to the richest
   available victim (≥2, then ≥1). Vanilla happens only via the explicit deterministic
   `VanillaCaseEvery` cadence (default 0 = off in testing), or in the degenerate case where
   the city has *zero* motivated victims (shouldn't occur after seeding).
4. Rank that victim's suspects by motive strength, keep the **top 10**
   (`Selection/KillerPoolSize`), pick the killer **uniformly at random** among them.
5. Set `currentMurderer/currentVictim`; **record the full pool** (every suspect + the
   event behind them) for clue injection. Vanilla then builds the guilt layer around the killer.

**No dummy red herrings** — richness comes from real events only; density is the lever (see seeding).

**Event types** (each = a module: seed · suspect-edges · testimony · clue-spec · audience):
- **Affair** (unchanged shape): participants = the two lovers; suspect-edges = the
  existing love-triangle pairings. Clue = **one** shared love letter (lovers' prints).
- **Promotion**: **promotee P** (a random report; *never* a suspect in their own promotion),
  **boss/decider D** (`Company.director`), **rivals R** = other reports (the passed-over),
  capped `Workplace/MaxSuspects`. Victim = P **or** D; suspects = R either way (edges
  `r∈R → P` and `r∈R → D`). Clues (W3): **one promotion letter** (names P; sent-vs-unsent
  *inferred from where it's found*) **+ one threat note per passed-over rival** (each rival's
  own prints → several forensically-named suspects, one killer).
- **Layoffs** (the reframed "firing" — no one is actually fired; the game simulates **no**
  job changes): victim = the **boss/director**, suspects = the **employees on the redundancy
  list** (`group`). Edges: each `l∈group → boss`. Clues (W3): **multiple unsent termination
  notices** (boss's prints; each *names one laid-off employee = a killer-to-be*). Structurally
  guarantees ≥3 real suspects from any boss with enough reports.

**Clue placement — motive-agnostic (KEY principle):** every clue lands at the victim's
**home OR workplace at random**, independent of motive type
(`home.PlaceObject` / `job.employer.placeOfBusiness.PlaceObject` — same primitive, both
confirmed exposed). An affair letter can surface at the office; a termination notice at
home. Injection is **pool-driven** (reads the recorded suspects), not re-derived from the
home's residents. Total clues per case capped for scene sanity (`Clues/MaxCluesPerCase`).

**Gossip & interrogation (same channel as affairs):** workplace audience = coworkers
(`workTeam`/`workOther`/`workNotBoss`/`familiarWork`) **+ partners** (people vent about
work at home) — a new case in `Gossip.Audience` / `IncludesPartners`.
`Interrogation.ComposeAbout` dispatches per event type: coworkers **name the suspects**
("X was passed over when Y got promoted — took it hard"; "half the office is on the layoff
list") so testimony expands an event into named suspects.

**Generation — ON-DEMAND, not seeded (revised 2026-09-04).** Promotions/layoffs aren't
vanilla events and pre-seeding fake ones city-wide created a density/tuning mess (the "1
suspect" degrade). Instead: at murder time `WorkplaceSim.CandidateEvents()` builds **one
ephemeral case per eligible company** from the LIVE roster — boss = `Company.director`,
suspects sampled from the alive `companyRoster` reports (`Occupation.employee`), needing
≥`MinSuspects` reports. The selector **merges these candidates into the victim-centric pool**
alongside affairs (mixed pools preserved), and **only the CHOSEN victim's case is
materialized** (`Gossip.Distribute` + `EventStore.Add`) so interrogation works — nothing is
persisted for cases that never happen. Suspects come straight from the roster, so multiplicity
is guaranteed and there is **no seeding-density lottery**. Dead actors are filtered
(`IsValidActor` now checks `isDead`). *Ambient* (pre-murder) workplace gossip is deferred to
optional **W2b** — cheap to add later (gossip-only events, no clues/floor) since affairs
already carry the ambient red-herring layer.

**New config:** `Workplace/{EnableWorkplace, MaxSuspects, PromotionShare}`,
`Selection/{MinSuspects=3, KillerPoolSize=10}`. (`DebugTools.ForceMotiveType`, F6, defaults
to `Professional` for testing — revert to `None` for release.)

**Deferred within this line:** grow-new workplace events over time (seed-only for 2.1);
**sabotage / credit-theft / humiliation** as a *separate, non-workplace "personal grudge"
motive family* (its own later vertical); converting money/landlord & feud to event types
(then they join the same pool).

## Status & resume kit

_Kept current each session so any fresh context can pick up cold. To resume: read this
section + `git log --oneline -8` + the `sod-motives-mod` memory. Build/deploy:
`dotnet build -c Release` (auto-copies to plugins; **game must be fully restarted** to
load the new DLL). Log: `BepInEx/LogOutput.log`, filter `[SODMotives]`._

**Landed so far (on `v2`):** affair events seeded from `paramour` links; director picks
an affair-motivated killer at `ExecuteNewMurder` (vanilla timing untouched); motive-note
clue injection (distinct JobTag per clue); interrogation piggybacks the native "Do you
know this person?" picker (arm-then-fire; GUID-unique speech ids); gossip seeded to
neighbours/coworkers/friends via a type-keyed audience; location-aware knower logging +
F9/F12 aids; **F8 = ALWAYS-ANSWER cheat** (NPCs never refuse the picker, no bribe).

**In-game so far:** clue OBJECTS spawn distinct (overlap fixed ✅). **Interrogation speech CONFIRMED
WORKING ✅** — after a long saga (stale text, then blank), the root cause was that NO Speak/DDS/Strings
path renders runtime text: `Strings.Get` rejects runtime-added entries even when they sit in
`stringTable`/`stringTableENG` (proven by `[spdiag]` direct read-back). SOLUTION (dd2fd54): don't
Speak — on pick, arm `{npc.humanID → gossip}`, and a postfix on `SpeechBubbleController.Setup` REPLACES
that NPC's vanilla answer bubble's `actualString`+`words` with our line, riding the proven render.
Log shows `hijacked bubble for <NPC> -> "..."`. Diagnostics stripped (this commit). This is a clean
SAVE POINT — interrogation loop end-to-end works (F8 no-bribe, F9/F12 find knowers, ask → NPC says gossip).

**Gossip rule (FINALIZED):** "reveal affairs the SUBJECT participates in" — generic (any citizen,
not murder-gated); a non-participant (e.g. a betrayed spouse) correctly returns nothing. Ask about
each of the 3 love-triangle members: the 2 affair participants reveal gossip; the betrayed partner
logs `(nothing to add)`. Betrayed-partner-victim murders stay solvable via the graph hop (ask about
the victim's cheating partner).

**Debug/testing keys:** F7 ghost · F8 always-answer (no bribe) · F9 case solution + nearest knowers ·
F10 teleport to scene · F11 to victim work · F12 to nearest affair-knower's home.

**Atomic next chunks** (each = one clear-able session ending in a commit):
- **C1 ✅** clue-overlap + interro-timing + location logging  _(07b9fea)_.
- **C1b ✅** interro stale-speech + F8 cheat  _(84becd1)_ → then blank-bubble saga → **RESOLVED via
  bubble hijack** _(dd2fd54)_; diagnostics stripped. Interrogation loop CONFIRMED in-game ✅.
- **C2 ✅ CLUE SYSTEM REWRITTEN** — model is now "real affair letters of the victim's apartment
  residents": for each resident with a `paramour`, one love letter between the two ACTUAL
  participants (`SetWriter`+`SetReciever`, never self-addressed), ≤2, no fake decoys. Placement uses
  `victim.home.PlaceObject(notePreset, owner, sender, reciever, out furn, forcedOwnership,
  placeClosestTo:home.anchorNode, ddsOverride:treeId, ...)` — the primitive `SpawnItem` wraps —
  which returns the REAL note (not a StorageBox) IN the home (`SpawnItem` gave containers + routed
  work/retail presets to workplaces; dropped all JobTag/activeMurderItems/SpawnItem machinery).
  Note preset filtered for a clean home document note (no retail/game-location/work/sub-spawn/folder).
  Gated to affair murders only (`MurderSelector.AffairByVictim`) so vanilla cases get nothing.
  Commits 11af7a0→010ad3b. **Confirmed in-game:** affair murder → one clean love letter at the
  victim's home (`atHome=True`); non-affair murders inject nothing.
- **C3** gossip-density tuning — adjust `Gossip.Audience` if finding a knower feels off.
- **C4** pair interrogation "what's going on between X and Y?" (two-person pick / second
  armed context in `Interrogation.cs`).
- **C5 → expanded into the V2.1 workplace + mixed-motive-pool line (spec above):**
  - **W1** Event model + mixed-motive pool selector: generalize `SocialEvent` (roles +
    resenter group), add `SocialEventType.Promotion`/`ChoppingBlock`, per-event
    suspect-edges, citywide victim→suspects index; refactor `MurderSelector` to
    victim-centric (≥3 victim / top-10 uniform killer / event-only pool / record full
    pool / vanilla fallback); retire V1 `TryPick` from the override. **Regression gate:**
    affairs still fire end-to-end through the NEW pool (no workplace events seeded yet).
  - **W2** `WorkplaceSim` seeding (company enumeration, generous promotion + chopping
    subset, resenter selection, gossip distribution). Workplace suspects enter the pool →
    workplace-motivated cases appear; F9 shows them.
  - **W3** Clue injection generalized + **pool-driven**: per-event clue specs, per-suspect
    threat notes, official records, home/work random placement, author prints, cap.
  - **W4** Interrogation generalized: `ComposeAbout` per event type; coworkers name the
    resenters; chopping-block phrasing.
  - **W5** F9/debug repoint at the REAL event pool; workplace nearest-knowers / teleport
    aids; expose the new tuning knobs.
  - **W6** Variety/balance playtest (event density, promotion:chopping ratio, ≥3 reachability).
- **C6** Feud event type (2.2).
- **C7** release cleanup — `ObviousTestNames=false`, `VanillaCaseEvery=3–4`,
  **`DebugTools.ForceMotiveType=None`** (Plugin.Load testing default is `Professional`),
  gate/remove debug hotkeys (F6 force-motive, F7 ghost, F9–F12), final variety playtest.

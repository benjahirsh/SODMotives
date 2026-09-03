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
- **C2 ✅** affair motive-note authored by an affair PARTICIPANT, not the betrayed killer
  _(11af7a0)_ — fixes the "love letter from the jealous betrayed spouse" nonsense. **Needs in-game
  spot-check** (log shows `suspect X writtenBy Y` when they differ), but low risk.
- **C3** gossip-density tuning — adjust `Gossip.Audience` if finding a knower feels off.
- **C4** pair interrogation "what's going on between X and Y?" (two-person pick / second
  armed context in `Interrogation.cs`).
- **C5** Workplace/promotion event type (2.1) via the `Gossip` type-keyed seam.
- **C6** Feud event type (2.2).
- **C7** release cleanup for 2.0.0 — `ObviousTestNames=false`, `VanillaCaseEvery=3–4`,
  gate/remove debug hotkeys (F7 ghost, F9–F12), final variety playtest.

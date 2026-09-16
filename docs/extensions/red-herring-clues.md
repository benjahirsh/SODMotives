# Extension — fabricated cross-motive "red herring" clues

**Branch it would land on:** `v2` · **Design pass:** 2026-09-16 · **Status:** design only, NO code,
DEFERRED. Companion to the auto-loaded memory `sod-motives-mod.md`. Supersedes the old
`redherring-clues-handover.md` resume kit (deleted — it carried a reversed design; see "Corrected
principle" below). Trust but verify any symbol before relying on it.

**Build / toolchain / debug keys:** unchanged from the rest of the mod — see the memory
(`sod-motives-mod.md`) and `docs/v2.6-email-recon.md` (Cpp2IL `2022.1.0-pre-release.21` for method
bodies; `ilspycmd` against the interop DLL for signatures; F-key test hooks).

---

## Why deferred
Big change, and it may not be necessary. The victim-centric selector already prefers MIXED pools
(`MurderSelector.MinSuspects` >= 3, and it buckets victims by motive family so a case often already
spans several motive types), so most cases don't badly telegraph the motive. **Revisit only if
playtesting shows single-sphere cases feeling repetitive/obvious.**

## Goal (user's words, 2026-09-16)
"Have the injected clues in the victim's home in such a way that it isn't obvious which motive we are
dealing with … inject clues from other events the victim is involved in, in the same way we do this
for gossip."

---

## Corrected design principle (THE important bit — figured out 2026-09-16)
A red herring must give the player **another reason someone would want the VICTIM dead** — i.e. the
victim is the **TARGET** of the fabricated motive, so it spawns a fresh set of suspects pointing *at*
them.

> Example: real case = the victim was killed by their **creditor** (debt). To muddy it, fabricate that
> the victim had just been **promoted** at work → jealous passed-over coworkers now ALSO had a reason
> to kill them. Two competing motives at the scene, one real killer.

**The first design pass got this BACKWARDS** and must not be repeated: it proposed events where the
victim was the *aggrieved party* (on a layoff list, being evicted, a passed-over coworker). That frames
the victim as someone who might have killed *someone else* — which does NOT explain why *they* are dead.
Those roles are OUT.

- **IN — victim = TARGET (creates a real alternative motive to kill the victim):**
  promotee, promoting boss, boss doing layoffs, evicting landlord, indebted tenant, owed
  landlord/creditor.
  - (Owed-landlord/creditor needs the money motive treated as BIDIRECTIONAL. `Debt` already is
    (`CollectDebtEdges` emits both directions); `RentArrears` is currently one-way (landlord→tenant),
    easy to extend if that role is wanted.)
- **OUT — victim = suspect/bystander:** passed-over coworker, evicted tenant, laid-off employee.

---

## Key findings from the design pass (don't re-derive)

1. **These events are NOT in `EventStore` at injection time, so they can't be read from it.**
   - Seeded events (`EventStore.Add` in `AffairSim`/`FeudSim`) are ONLY affair/feud/debt — all
     bidirectional / both-target, so a seeded event involving the victim is *always already in their
     pool*. Never a red-herring gap.
   - Workplace/property events (`WorkplaceSim.CandidateEvents` / `PropertySim.CandidateEvents`) are
     minted on demand DURING victim selection and only stored (`MurderSelector.cs` step 7,
     `EventStore.Add`) for the CHOSEN case. They're murder scaffolding, not durable world facts.
   - Gossip has the same limitation (it reads `EventStore.KnownBy`), so "mirror gossip" via
     `EventStore.All` would surface almost nothing.

2. **So the feature must MINT a fabricated target-event** from the victim's live affiliations (real
   employer + coworkers for a promotion; real tenants for an eviction; real landlord for rent/debt) —
   the SAME move the director already makes to build the real case, applied a second time from a
   different motive sphere.

3. **The existing per-type injectors already fit.** `InjectPromotion` / `InjectEviction` / etc. in
   `ClueInjector.cs` were written for "victim IS the target" (a real case always is), so reusing them
   for red herrings is clean — they already carry the *suspects'* prints (not the killer's), draw board
   connections, handle the physical/email fork, and persist via `Persistence.RecordNote` /
   `RecordEmail`. Pass a minted `SocialEvent` with the victim in the target slot (`ev.a`/`ev.b`) and the
   fabricated suspects in `ev.group`; the injectors fall back to `ev.group` when there is no
   `PoolByVictim` entry for the event.

4. **Honesty (V2.2 rule):** compliant by the same logic as the main cases — real people, a fabricated
   grievance, cleared by alibi/forensics. Get the user's explicit nod before building anyway (they
   wrote the rule; minting a *second* fabricated motive is a mild stretch of "genuine event").

---

## Open decisions (settle WITH THE USER before coding)

1. **Which fabricated types?** Promotion is universal (almost any employed victim has coworkers →
   jealous rivals — this is the user's own example). Eviction / layoffs / rent-debt are situational
   (victim must be a landlord / company director / renter). Start promotion-only, or support whatever
   the victim's real life allows?

2. **Clue-only, or clue + gossip?** "The same way we do this for gossip" implies also minting the event
   into `EventStore` + `Gossip.Distribute` so interviews corroborate it ("word is they'd just been
   promoted"). More immersive but touches the pool + persistence more. **Must be done POST killer
   selection** so the fabricated suspects can never become the real killer.

3. **How heavy, how often?** A full fabricated promotion via `InjectPromotion` can drop 3 rival threats
   + an email — could out-shout the real motive and collides with `MaxCluesPerCase` (live cfg = 4). Full
   intensity (equal footing = strongest misdirection) vs deliberately lighter (1 clue = a hint)? Every
   eligible case, only when the real pool is single-sphere, or on a config share? Prefer a red-herring
   sphere DIFFERENT from the killer's motive so it actually diversifies the pile.

---

## Where it would hook
- **`ClueInjector.InjectForCase`** — a red-herring pass AFTER the existing per-pool-event `foreach`,
  still bounded by `placed < MaxClues`. Mint the fabricated `SocialEvent` (victim in the target slot,
  fabricated suspects in `ev.group`), then route to the matching per-type injector.
- **For clue + gossip:** also `Gossip.Distribute(ev)` + `EventStore.Add(ev)` + add to
  `MurderSelector.PoolByVictim[victim]` — POST selection, so it can't perturb the killer pick.
- **Persistence:** the reused injectors already record notes/emails; a pool/EventStore addition
  round-trips via the existing pass-2 serialization. Injection is once-per-case gated (`_injected`), so
  no random re-minting on reload.
- **Suggested config knob:** `[Clues] RedHerringShare` (and/or `MaxRedHerringClues`).

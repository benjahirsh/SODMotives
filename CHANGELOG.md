# Changelog

## 1.0.0 — first release

Replaces ordinary procedurally-generated murders with ones driven by **real NPC relationships and
events**, so the case has an actual motive you can reconstruct from a discoverable trail instead of
chasing a random stranger. Special cases (kidnap / sniper) and story murders are left fully vanilla.

**How a case is built**
- The mod picks a **victim with several real, event-backed enemies**, then a **killer at random from that
  pool** — every suspect is a genuine red herring and the killer looks no guiltier than the rest. Vanilla
  physical forensics (weapon prints / CCTV / alibi) still convicts the one; the motive layer only
  supplements it.
- By default **~80%** of generated murders are motive cases; the rest are left as classic vanilla
  serial-killer hunts (with their signatures) for variety. Tunable via `MotiveCaseShare`.

**Motive families** (all traceable, drawn from simulated social events rather than the static `like` value)
- **Affair** — infidelity / love-triangle.
- **Workplace** — promotions and layoffs (passed-over rivals / laid-off staff).
- **Property** — evictions and rent arrears (landlord vs aggrieved tenants).
- **Feud** — personal bad blood; **Debt** — unpaid debts (both bidirectional).

**The trail**
- **Physical clues** — motive-appropriate notes rendered from document DDS trees, placed among the
  victim's things (home *or* workplace), with the author's handwriting and a per-type fingerprint policy,
  and clickable, board-pinnable citizen/address links in custom-written bodies.
- **Emails** — eligible clues can arrive as a vmail instead; the promotion letter always emails boss →
  promotee. Survives save/reload via a sidecar.
- **Gossip / interrogation** — asking an NPC "do you know this person?" can append naturalised gossip
  about the subject's motive events, so you can build the suspect pool by asking around.

Serial-killer dressing (calling card / moniker / graffiti) is stripped from motivated cases.

**Configuration**
- All knobs live-editable in the **BepInExConfigManager** overlay (open with `` ` ``); the mod also runs
  without it, reading the `.cfg` directly.
- Developer/testing tooling ships **disabled**; a single always-available **F9** case-diagnostics overlay
  (case type + murder state + injected clues, no spoilers) remains for bug reports. Enable the full test
  loop via `[Debug] EnableDebugKeys`.

# A6 recon — address-book / call-history "who to interview" lead

**Branch:** `v2` · **Date:** 2026-09-16 · Recon-only (no code). Companion to `docs/pre-release-plan.md`
(item **A6**) and the auto-loaded memory. Decompiles this session in scratchpad `a6recon/` (interop =
accurate signatures, NO method bodies — regenerate with `ilspycmd "<game>/BepInEx/interop/Assembly-CSharp.dll" -t <Type>`).
For method BODIES use Cpp2IL `2022.1.0-pre-release.21` (see `v2.6-email-recon.md`).

## Goal
Surface the victim's phone **call history** (and/or **address book**) as an in-world lead pointing the
player at WHO to interview — mirroring how the clue pile + gossip already point at suspects. Inject
calls/contacts between the victim and the real suspects/knowers; the player checks the victim's phone
and gets names to chase. Honesty (V2.2): real people, a plausible fabricated call, alibi-clearable — a
real connection, not a dead-end.

---

## Data model (all verified in the interop signatures)
- **`NewBuilding.callLog`** : `Il2CppSystem.Collections.Generic.List<TelephoneController.PhoneCall>` —
  **call history is stored PER BUILDING.** Rendered by `CallLogsContentController` (has a
  `NewBuilding building` field) + `CallLogsEntryController.Setup(PhoneCall, NewBuilding)` per row.
- **`TelephoneController.PhoneCall`** (`: Il2CppSystem.Object`, plain data): fields
  `from`(int, number), `to`(int, number), `time`(float), `caller`(int humanID), `receiver`(int humanID),
  `intendedReceiver`(int humanID), `source`(`CallSource`), `state`/`previousSate`(`CallState`).
  Ctor: `PhoneCall(Telephone newFrom, Telephone newTo, float newTime, Human newCaller,
  Human newIntendedReceiver, CallSource newCallSource, float newMaxRingTime = 0.1f,
  bool newSpecificRecevier = false)` — pass two `Telephone`s + caller/receiver `Human`s.
- **`EvidenceTelephoneCall : EvidenceTime`** — a call is itself an Evidence subtype (so a logged call is
  inspectable/board-pinnable, like other time-stamped evidence).
- **`Human.addressBook`** : `Evidence` — each citizen's address book is an **Evidence object** (the
  OTHER half of the lead; see "Address-book half" below).
- **`Telephone`** : `number`(int), `numberString`(string) — a placed phone interactable.
- **`Toolbox.GetClosestTelephone(Actor toActor, float maxDistance = 18f, bool prioritiseSameLocation =
  true, bool payPhonesOnly = false, bool mustHaveValidAccess = true)` : `Telephone`** — how to obtain a
  `Telephone` near a citizen (for the ctor's from/to).
- **Persistence:** **`BuildingStateSav.callLog`** : `List<TelephoneController.PhoneCall>` (a save class
  nested in `StateSaveData`) — **the building call log ROUND-TRIPS natively**, and `PhoneCall` is all
  int/float/enum fields (no runtime-only DDS tree like vmail), so injected calls persist for free.

**Vanilla precedent (the linchpin to reuse):** the symbol **`TriggerCoverUpTelephoneCall`** exists — the
game already injects a scripted call for cover-up cases. Confirm its owner + body (Cpp2IL) first; it is
almost certainly the exact "inject a call into the log" pattern we want, done the game's own way.
Related symbols to chase: `FindTelephoneByNumber`, `FindTelephonesAtPlayerLocation`,
`SpawnTelephoneEntryWindow`, `SetTelephoneAnswered`, `GenerateTelephoneNumber`, `buildingCallLogMax`.

---

## The three questions
1. **Inspectable?** ✅ Yes. Call log renders via the phone UI (`CallLogsContentController.building` →
   `callLog`), and a call is `EvidenceTelephoneCall`. Address book is `Human.addressBook` (Evidence).
2. **Injectable?**
   - **Call log — likely YES and clean:** construct a `PhoneCall` (public ctor; or set the int/enum
     fields directly) and `victim.home.building.callLog.Add(...)`. No `AddCallLog` helper found → it's a
     raw list add (mirror whatever `TriggerCoverUpTelephoneCall` does). Get the two `Telephone`s via
     `GetClosestTelephone` (or the home's phone).
   - **Address book — HARDER/opaque:** `Human.addressBook` is an `Evidence`; no clear population/injection
     path surfaced in `Human`/`Toolbox`. Manipulating an Evidence's contacts/facts is its own sub-recon.
3. **Persists?** ✅ Yes, natively for the call log (`BuildingStateSav.callLog` + value-type `PhoneCall`).
   **No sidecar/reload-rebuild needed** — the major cost that the email feature carried is absent here.

---

## Injection recipe (hypothesis — call-log half)
1. For the victim, get `victim.home.building` (`NewBuilding`) and its `callLog`.
2. For each suspect/knower to surface: get a `Telephone` for each end (victim + other party) via
   `Toolbox.Instance.GetClosestTelephone(...)` or the home's phone.
3. `new TelephoneController.PhoneCall(otherTel, victimTel, gameTime - N, otherHuman, victimHuman,
   CallSource.<completed/landline>)` (confirm the enum value that reads as a normal completed call), then
   `building.callLog.Add(call)` (respect `buildingCallLogMax`; inject recent timestamps).
4. Record nothing extra for persistence — it saves via `BuildingStateSav`.

**Open design point:** the log is per-BUILDING, so in an apartment block the victim's unit shares the
building log. `PhoneCall.to`/`intendedReceiver` (humanID) disambiguate the target, but confirm the app
scopes/labels rows to the victim rather than dumping the whole tower.

## Access / discovery flow (RUNTIME UNKNOWN — verify in-game)
How does the player reach the victim's call log? Presumably: use the victim's home `Telephone` →
`SpawnTelephoneEntryWindow` → call-logs app for that building. `GetClosestTelephone(...,
mustHaveValidAccess=true)` implies access can be gated. Confirm the player can view it at the scene and
whether it needs the number / valid access — this decides whether the lead is discoverable at all.

---

## Open runtime unknowns → verify with a test hook FIRST (the vmail lesson)
The email feature looked simple from decompiles too, then needed real runtime debugging (triggerPoint,
messageRef, participants, block-split). Expect the same shape here. Build an F-key test hook that injects
one call into a known building, then confirm:
1. Does an injected `PhoneCall` **render** in the call-logs app (right `state`/`source`/valid numbers)?
2. Can the player **access** that building's log in-game, and where from?
3. Does it **round-trip** on save/reload (expected native yes via `BuildingStateSav`)?
4. Does it register as `EvidenceTelephoneCall` (board-pinnable / linkable to the two citizens)?
5. `buildingCallLogMax` trimming behaviour.

## Address-book half (the harder, deferred part)
`Human.addressBook` is an `Evidence`. `AddressBookController` sorts `Human`/`Acquaintance` and filters
`Interactable.Passed` — that reads like the PLAYER's own contacts view, not a per-NPC inspectable book;
needs body-level recon (Cpp2IL on `AddressBookController.CheckEnabled` + `FindAddressBook` /
`addressBookDiscovery`) to confirm whether an NPC's address book is player-inspectable and how it's
populated/injectable. Recommend deferring this half regardless.

---

## Sizing verdict (for the pre-release question)
- **Call-log lead:** **MEDIUM**, not "small." Native persistence removes the biggest cost (no
  sidecar/reload engine that the email work needed), and construction is a simple list-add with a vanilla
  precedent (`TriggerCoverUpTelephoneCall`). BUT it carries **vmail-class runtime risk**: renderer
  finickiness + an unconfirmed player-access flow that can only be pinned in-game. So it's a focused
  spike (test-hook-first), not a safe drop-in.
- **Address-book lead:** **LARGER / uncertain** (Evidence manipulation, opaque population) → post-release.

**Recommendation:** keep A6 **post-release by default**. If the call-log lead is wanted in v1, scope
**only** the call-log half as a deliberate spike, starting with an F-key test hook to retire the two
runtime unknowns (render + access) before wiring it to real cases — exactly the sequence that worked for
vmail. Do NOT commit to it as "small" sight-unseen.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

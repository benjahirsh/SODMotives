# C1b — Simplified playtest plan

Goal: run each of the 7 motive types once at production balance and confirm **clue pile + gossip +
solvability**. Companion to `pre-release-plan.md` (Phase C). Production blend is already the code default
(Affair/Feud/Debt 0.35 · Workplace 0.46 · Property 0.18; Promotion 70/30; Eviction 30/70).

## One-time setup
1. Launch a **fresh sandbox** — this regenerates the clean `.cfg` at production defaults. (Optional: turn on
   the "start with an apartment" gameplay setting so home clue-drops are reachable.)
2. Open the mod overlay with **`` ` ``** and confirm `[Motive Mix]` loaded the values above.
3. Turn on the test aids:
   - **F7** — ghost mode (fly through walls, invincible)
   - **F8** — always-answer (NPCs never refuse "do you know this person?", so gossip always arms)
   - Clues are auto-named **"MODCLUE …"** (ObviousTestNames on) so they stand out on the board.

## The loop — repeat for each type
Order: **Affair → Promotion → Layoffs → Eviction → RentArrears → Feud → Debt**

1. **Force the type** — tap **F6** until the top-right HUD shows your target type.
2. **Get a murder** — wait for the next scheduled murder. *(No trigger hotkey right now — ask me to add one
   for a fast loop.)*
3. **Read the solution** — **F9** overlay: confirm **CASE TYPE** matches, note **KILLER**, the **SUSPECT
   POOL**, and the **INJECTED CLUES** + where each landed.
4. **Find the clue(s)** — **F10** (crime scene) / **F11** (victim's workplace) to get there; look for the
   "MODCLUE …" items. For an emailed clue, open the recipient's inbox.
5. **Test gossip** — **F12** to the nearest knower (or walk to a suspect), interrogate → "do you know this
   person?" → pick the victim, then a suspect. Confirm the mod gossip bubble appears and reads naturally.
6. **Judge it** — the 3 checks below.

## The 3 checks (per case)
- **Clue pile** — at least one motive clue exists, reads correctly (right names / handwriting / prints), and
  does **not** name the killer more loudly than the red herrings.
- **Gossip** — a knower relays the motive in natural language and points you at the **pool**, not straight
  at the killer.
- **Solvable** — pool has **≥3 real red herrings**; the **physical forensics** (weapon prints / CCTV /
  alibi) is what pins the actual killer; the killer looks no guiltier than the herrings.

## What each type should drop
| Type | Expected clue | Notes |
|---|---|---|
| **Affair** | Latent love letter (no visible From/To) | handwriting + print + gossip carry it |
| **Promotion** | ALWAYS both channels: anon handwritten threat note(s) to victim (rival's prints) **+** boss→promotee letter **email** | email lands in both inboxes |
| **Layoffs** | Redundancy list naming the laid-off (boss's prints) | may arrive as email instead |
| **Eviction** | Redevelopment/eviction notice, clickable tenant addresses | may arrive as email; **rare ~3% → F6-force it** |
| **RentArrears** | Physical arrears threat note | bidirectional — landlord OR tenant can be victim |
| **Feud** | Physical; several feud suspects | gossip collapses feuds into one line |
| **Debt** | Physical; creditor vs debtor | bidirectional |

## Notes
- **F6 forces the NEXT murder only.** If the log says *"no event-backed suspect pool,"* the city had no
  valid pool of that type this roll — get another murder or move on.
- **Eviction (~3%) and layoffs (~8%)** are the rarest — always F6-force those.
- If a case's feel/balance is off, tweak the `[Motive Mix]` sliders live in the overlay (**`` ` ``**);
  changes apply to the next case.
- **Bug to report?** F9 shows CASE TYPE + INJECTED CLUES (+ locations) — capture those.

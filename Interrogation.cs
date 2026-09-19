using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace SODMotives
{
    // V2 interrogation, piggybacking the NATIVE "Do you know this person?" flow.
    //
    // Two-hook "arm then fire" model (the old single-hook version replayed a stale pick
    // and fired even on a reject-without-bribe):
    //   1) The player clicks "Do you know this person?" -> DialogController.DoYouKnowThisPerson
    //      (+Bribe1/2/3) runs at DECISION time with a success flag but NO picked citizen.
    //      We ARM here, and ONLY when accepted (success==true), recording which NPC is
    //      talking. We never speak here — there is no pick yet.
    //   2) The photo picker opens; the player clicks a face -> PhotoSelectButtonController
    //      .OnLeftClick carries the chosen citizen. If armed, we speak what THIS npc knows
    //      about the FRESHLY-picked person, then disarm (one-shot).
    // Guarantees: fresh pick, accepted-only, and appended AFTER the vanilla answer.
    internal static class Interrogation
    {
        internal static bool Enable = true;

        // Pending "knowName was accepted" context, consumed by the next photo pick.
        private static bool _armed;
        private static Citizen _npc;

        // Gossip delivery — a NEW trailing bubble, leaving the vanilla answer bubbles untouched.
        // The DDS/Strings/Speak path can't render runtime text (Strings.Get rejects our entry),
        // so we can't just Speak the gossip. Instead, two-phase on SpeechBubbleController.Setup:
        //   Phase 1 — when the armed NPC's vanilla answer bubble spawns, we know the answer is
        //     fully queued, so we enqueue a NEW bubble on that NPC's speechController REUSING the
        //     vanilla bubble's (valid) dictRef/entryRef (guaranteed to render), landing it at the
        //     END of the queue. We record OUR QueueElement's native pointer. Vanilla left alone.
        //   Phase 2 — when OUR queued bubble spawns (matched by that pointer), we replace its text
        //     with the gossip and restart the reveal. Result: real answer, then our gossip bubble.
        private static int _pendingHumanId = -1;                        // armed NPC awaiting its vanilla answer (phase 1)
        private static System.Collections.Generic.List<string> _pendingLines; // one gossip line per event TYPE -> one bubble each
        // Our queued gossip bubbles, keyed by their QueueElement pointer -> the text to inject (phase 2).
        private static readonly System.Collections.Generic.Dictionary<System.IntPtr, string> _pendingByPtr
            = new System.Collections.Generic.Dictionary<System.IntPtr, string>();

        // Postfix target for DoYouKnowThisPerson(+Bribe1/2/3): ARM ONLY, never speak.
        internal static void ArmForPick(Citizen npc, Interactable speakingTo, bool success)
        {
            if (!Enable) return;
            try { DebugTools.NoteTalkingTo(npc); } catch { }   // F8 HUD: update "who you're talking to" on any photo-show
            try
            {
                if (success && npc != null)
                {
                    _armed = true; _npc = npc;
                    MotivesPlugin.Log.LogInfo($"[SODMotives][interro] armed — {Name(npc)} accepted 'do you know this person?'");
                }
                else
                {
                    _armed = false; _npc = null;   // reject -> next pick can't replay us
                }
            }
            catch { _armed = false; _npc = null; }
        }

        // Postfix target for PhotoSelectButtonController.OnLeftClick: fire once on the fresh pick.
        internal static void OnPicked(Human subject)
        {
            if (!Enable || !_armed) return;
            Citizen npc = _npc;
            _armed = false; _npc = null;   // one-shot

            try
            {
                if (npc == null || subject == null) return;
                // No lazy re-seed here: OnStartGame seeds new games and the sidecar import (or its
                // affair fallback) handles loads, so the store is already populated by interrogation
                // time. If it's genuinely empty (e.g. a paramour-free city), ComposeLines simply yields
                // no gossip — we must NOT call SeedForNewGame, which would clear the restored note
                // Records + per-case maps (and re-derive nothing new).

                var lines = ComposeLines(npc, subject);
                if (lines.Count == 0)
                {
                    MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {Name(npc)} asked about {Name(subject)} -> (nothing to add)");
                    return; // this NPC knows nothing about the subject; leave the vanilla answer
                }

                // Arm phase 1: the NPC's vanilla answer bubble triggers us to enqueue ONE trailing
                // gossip bubble per line. Clear any stale phase-2 state from a previous pick.
                _pendingHumanId = npc.humanID;
                _pendingLines = lines;
                _pendingByPtr.Clear();
                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {Name(npc)} asked about {Name(subject)} -> {lines.Count} gossip line(s) (armed trailing bubbles)");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] OnPicked error: {e.Message}"); }
        }

        // Two-phase delivery on every SpeechBubbleController.Setup (see the field comment):
        // phase 2 first (inject our gossip into our own queued bubble), then phase 1 (on the
        // armed NPC's vanilla answer, enqueue that trailing gossip bubble). The vanilla answer
        // bubbles are never modified — the gossip is its own bubble at the end of the sequence.
        internal static void OnBubbleSetup(SpeechBubbleController bubble, SpeechController sc)
        {
            if (bubble == null || sc == null) return;
            // Testing aid (F8 HUD): note the NPC who's speaking so DebugTools can show who you're
            // talking to. The game has no forceable "tell me your name" dialog, so this readout is
            // how you always identify the current interlocutor. Ignores the player's own lines.
            try { var a0 = sc.actor; var h0 = a0 != null ? a0.TryCast<Human>() : null; if (h0 != null) DebugTools.NoteTalkingTo(h0); } catch { }
            try
            {
                // Phase 2: one of our enqueued gossip bubbles just spawned -> set its text.
                if (_pendingByPtr.Count > 0)
                {
                    System.IntPtr elemPtr = System.IntPtr.Zero;
                    try { var s = bubble.speech; if (s != null) elemPtr = s.Pointer; } catch { }
                    if (elemPtr != System.IntPtr.Zero && _pendingByPtr.TryGetValue(elemPtr, out var gossip))
                    {
                        _pendingByPtr.Remove(elemPtr);
                        SetBubbleText(bubble, gossip);
                        MotivesPlugin.Log.LogInfo($"[SODMotives][interro] gossip bubble rendered -> \"{gossip}\"");
                        return;
                    }
                }

                // Phase 1: the armed NPC's vanilla answer bubble spawned -> the answer is fully
                // queued, so enqueue ONE trailing bubble PER gossip line (each reusing this bubble's
                // valid dictRef/entryRef so it renders), recording each QueueElement pointer for
                // phase 2. The vanilla answer bubbles are never touched.
                if (_pendingHumanId < 0 || _pendingLines == null) return;
                Human speaker = null;
                try { var a = sc.actor; if (a != null) speaker = a.TryCast<Human>(); } catch { }
                if (speaker == null || speaker.humanID != _pendingHumanId) return;

                var lines = _pendingLines;
                _pendingHumanId = -1; _pendingLines = null;   // one-shot phase 1

                string dict = null, entry = null;
                try { var s = bubble.speech; if (s != null) { dict = s.dictRef; entry = s.entryRef; } } catch { }
                if (string.IsNullOrEmpty(dict)) return;   // can't clone -> bail

                var q = sc.speechQueue;
                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    int before = q != null ? q.Count : -1;
                    sc.Speak(dict, entry, false, false, false, 0f);   // append a trailing bubble, no interrupt
                    if (q != null && q.Count > before && q.Count > 0)
                    {
                        var ours = q[q.Count - 1];
                        var ptr = ours != null ? ours.Pointer : System.IntPtr.Zero;
                        if (ptr != System.IntPtr.Zero) _pendingByPtr[ptr] = line;
                    }
                }
                if (_pendingByPtr.Count > 0)
                    MotivesPlugin.Log.LogInfo($"[SODMotives][interro] queued {_pendingByPtr.Count} trailing gossip bubble(s) for {Name(speaker)}");
                else
                    MotivesPlugin.Log.LogWarning("[SODMotives][interro] could not queue trailing gossip bubbles");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] bubble error: {e.Message}"); }
        }

        // Set a bubble's target text + word list and restart the typewriter reveal from 0.
        private static void SetBubbleText(SpeechBubbleController bubble, string text)
        {
            bubble.actualString = text;
            try
            {
                var parts = text.Split(' ');
                var arr = new Il2CppStringArray(parts.Length);
                for (int i = 0; i < parts.Length; i++) arr[i] = parts[i];
                bubble.words = arr;
            }
            catch { }
            bubble.revealedChars = 0;
            bubble.wordsRevealed = 0;
            bubble.setFinalText = false;
            try { if (bubble.text != null) bubble.text.text = ""; } catch { }
        }

        // Safety backstop on how many gossip bubbles one answer produces (affairs are always a
        // single collapsed bubble; each other event the subject is in adds one). NOT a per-type
        // cap — keep it comfortably above the number of motive event types a subject could
        // realistically be in at once (affair + professional + planned feud/theft/...), so genuine
        // multi-motive subjects fully surface. It only guards against a pathological pile-up.
        private const int MaxGossipBubbles = 6;

        // The gossip lines this NPC can add about the picked person — ONE per event type, each
        // becoming its own trailing bubble. The subject's involvement can span types (e.g. an
        // affair AND a passed-over promotion). Each line refers to the subject as "they" (the
        // player just picked their photo; restating the name is noise). Generic — works for any
        // citizen. A non-participant (a betrayed spouse with no affair of their own, an outsider
        // to the company) yields an empty list; the murder stays solvable via the graph.
        private static List<string> ComposeLines(Human npc, Human subject)
        {
            var lines = new List<string>();
            // Never narrate about the very person being interviewed (you picked their own photo).
            if (Motive.Same(npc, subject)) return lines;

            // Only volunteer gossip about someone this NPC can NAME. Knowing an EVENT (via its gossip
            // audience — e.g. a tenant's friend hearing about a redevelopment) is NOT the same as knowing
            // the subject: such an NPC can't identify the photo (no name), so gossip from them reads as
            // coming from a stranger. Gate on the `knowsName` connection so gossip + photo-ID agree.
            if (!Motive.KnowsName(npc, subject)) return lines;

            // Stable per (npc, subject) so re-asking the same person gives the same lines.
            int seed = npc.humanID * 31 + subject.humanID;

            // Gather the subject's affair partners (that this NPC knows) and non-affair lines
            // separately. Multiple affairs of the same subject COLLAPSE into ONE line naming each
            // lover once — so affairs are a single bubble.
            // Collapse the one-to-one relations into single list bubbles (like affairs) so an answer
            // never reads as several identically-shaped sentences:
            //   lovers     -> "having an affair with X, Y and Z"
            //   owedTo     -> creditors the subject owes  ("they owed X and Y money")
            //   owedBy     -> debtors who owe the subject  ("X and Y owed them money")
            //   feudOthers -> feud counterparties          ("fallen out with X, Y and Z")
            // Only the multi-party workplace / property events stay one bubble per event line.
            var lovers = new List<Human>();
            var owedTo = new List<Human>();   // subject is the debtor; these are their creditors
            var owedBy = new List<Human>();   // subject is the creditor; these are their debtors
            var feudOthers = new List<Human>();   // feud counterparties of the subject (collapsed like affairs/debt)
            var rentChasing = new List<Human>();  // subject is a LANDLORD: the tenants they're chasing for rent (collapse to a COUNT)
            bool rentBehind = false;              // subject is a TENANT behind on their rent
            var workLines = new List<string>();
            foreach (var e in EventStore.KnownBy(npc.humanID))
            {
                // A party to a ONE-TO-ONE event (affair/feud/debt/rent-arrears) is NOT a gossip
                // "knower" of it: its testimony names the OTHER party, so a party asked about the other
                // would narrate THEMSELVES in the third person (how the killer was once heard describing
                // their own feud with the victim) — or worse, volunteer their own motive (a landlord
                // relaying their tenant's arrears). MULTI-PARTY events (promotion/layoffs/eviction) are
                // EXEMPT: their testimony reports the SUBJECT's role, not the speaker's, so an involved
                // coworker/tenant naming a co-participant can't self-incriminate — and office/building
                // drama realistically spreads among the very people in it. (A4, 2026-09-16.)
                if (IsOneToOne(e.type) && e.InvolvesHuman(npc.humanID)) continue;

                if (e.type == SocialEventType.Affair)
                {
                    if (!Involves(e, subject)) continue;   // npc-is-a-party already excluded above
                    Human other = Motive.Same(e.a, subject) ? e.b : e.a;
                    if (other != null && !ContainsHuman(lovers, other)) lovers.Add(other);
                }
                else if (e.type == SocialEventType.Debt)
                {
                    // e.a = debtor, e.b = creditor. Collapse both directions into the two lists.
                    if (e.a != null && Motive.Same(e.a, subject)) { if (e.b != null && !ContainsHuman(owedTo, e.b)) owedTo.Add(e.b); }
                    else if (e.b != null && Motive.Same(e.b, subject)) { if (e.a != null && !ContainsHuman(owedBy, e.a)) owedBy.Add(e.a); }
                }
                else if (e.type == SocialEventType.Feud)
                {
                    // Collapse the subject's feuds into ONE bubble (like affairs/debt) instead of a
                    // separate, identically-shaped "falling-out" line per feud. Name the counterparty
                    // (the party that isn't the subject).
                    if (e.a != null && Motive.Same(e.a, subject)) { if (e.b != null && !ContainsHuman(feudOthers, e.b)) feudOthers.Add(e.b); }
                    else if (e.b != null && Motive.Same(e.b, subject)) { if (e.a != null && !ContainsHuman(feudOthers, e.a)) feudOthers.Add(e.a); }
                }
                else if (e.type == SocialEventType.RentArrears)
                {
                    // a = tenant, b = landlord. Bidirectional arrears means a landlord can be party to several
                    // events, so COLLAPSE like debt/feud: a landlord subject -> a COUNT of tenants chased
                    // ("chasing three tenants for rent"), a tenant subject -> one "behind on rent" line —
                    // never one "chasing a tenant" line per event. (Party-knowers already skipped above.)
                    if (e.b != null && Motive.Same(e.b, subject)) { if (e.a != null && !ContainsHuman(rentChasing, e.a)) rentChasing.Add(e.a); }
                    else if (e.a != null && Motive.Same(e.a, subject)) rentBehind = true;
                }
                else if (e.type == SocialEventType.Promotion && e.a != null && Motive.Same(e.a, npc)
                         && e.b != null && Motive.Same(e.b, subject))
                {
                    // The PROMOTEE (e.a) asked about the BOSS (e.b) who promoted them — the one participant
                    // with firsthand knowledge. Speak first-person (they were IN it, so no "word is" rumour
                    // framing) and hint at the disgruntled rivals WITHOUT naming them, giving the player a
                    // lead into the suspect pool while leaving the "ask around to find who" work intact.
                    // (The promotee is never a suspect in their own promotion, so this can't self-incriminate.)
                    string pl = PromoteeAboutBossLine(seed + workLines.Count);
                    if (!string.IsNullOrEmpty(pl) && !workLines.Contains(pl)) workLines.Add(pl);
                }
                else
                {
                    // Multi-party workplace / property (Promotion / Layoffs / Eviction / RentArrears):
                    // the SUBJECT's own role. Bump the seed per line so multiple events of one type don't
                    // pick the same variant. Null when the subject isn't in this event.
                    string wl = e.TestimonyAbout(subject, seed + workLines.Count);
                    if (!string.IsNullOrEmpty(wl) && !workLines.Contains(wl)) workLines.Add(wl);
                }
            }

            // One affair bubble + up to two debt bubbles (owed-to / owed-by) + one per other event line,
            // capped only by the safety backstop (so future motive types still surface alongside these).
            // Distinct seed offsets so back-to-back bubbles don't land on the identically-shaped variant.
            if (lovers.Count > 0) lines.Add(AffairLine(subject, lovers, seed));
            if (owedTo.Count > 0) lines.Add(DebtOwedLine(owedTo, seed));
            if (owedBy.Count > 0) lines.Add(DebtOwedToThemLine(owedBy, seed + 1));
            if (feudOthers.Count > 0) lines.Add(FeudLine(feudOthers, seed + 2));
            if (rentChasing.Count > 0) lines.Add(RentChaseLine(rentChasing.Count, seed + 3));
            if (rentBehind) lines.Add(RentBehindLine(seed + 4));
            for (int i = 0; i < workLines.Count && lines.Count < MaxGossipBubbles; i++) lines.Add(workLines[i]);

            // Naturalise a multi-bubble answer so it never reads as several identically-shaped sentences:
            // vary a repeated "Word is …" opener into a conversational follow-on, and keep the
            // ", from what I hear." hearsay tail on only the first line that uses it.
            DedupeOpeners(lines);
            DedupeHearsayTail(lines);
            return lines;
        }

        // Affair gossip about the subject (subject = "they"), naming each distinct lover once. The
        // betrayed-partner clause is appended ONLY when the subject actually has a partner (the
        // variants themselves never assume one, so they read correctly for a single subject too).
        private static string AffairLine(Human subject, List<Human> lovers, int seed)
        {
            Human sp = SafePartner(subject);
            string ln = Pick(seed,
                $"I think they've been having an affair with {JoinNames(lovers)}.",
                $"Apparently they have something going on with {JoinNames(lovers)}.",
                $"Rumour has it they've been seeing {JoinNames(lovers)} in secret.");
            if (sp != null) ln += Pick(seed + 5,
                $" Can't imagine {Name(sp)} took that well.",
                $" {Name(sp)} can't have been happy about it.",
                $" Don't think {Name(sp)} knew.");
            return ln;
        }

        // Debt gossip, collapsed like affairs. Subject = "they". Variants read correctly for one name or
        // a "X, Y and Z" list (no singular/plural verb agreement on the list).
        private static string DebtOwedLine(List<Human> creditors, int seed)   // subject OWES these people
            => Pick(seed,
                $"Apparently they owed {JoinNames(creditors)} money.",
                $"Word is they were in debt to {JoinNames(creditors)}.",
                $"I think they owed {JoinNames(creditors)} and hadn't paid it back.",
                $"Rumour has it they'd borrowed off {JoinNames(creditors)} and never repaid it.");

        private static string DebtOwedToThemLine(List<Human> debtors, int seed)   // these people OWE the subject
            => Pick(seed,
                $"Apparently {JoinNames(debtors)} owed them money.",
                $"Word is {JoinNames(debtors)} still owed them money.",
                $"Heard they'd lent {JoinNames(debtors)} money that was never repaid.",
                $"I think {JoinNames(debtors)} owed them and never paid them back.");

        // Feud gossip, collapsed like affairs/debt. Subject = "they". Names each distinct counterparty
        // once; variants read correctly for one name or an "X, Y and Z" list.
        private static string FeudLine(List<Human> others, int seed)
            => Pick(seed,
                $"Apparently they'd fallen out with {JoinNames(others)}.",
                $"Word is they and {JoinNames(others)} had a serious falling out.",
                $"Rumour has it they'd been at odds with {JoinNames(others)}.",
                $"Heard they weren't on speaking terms with {JoinNames(others)}.");

        // Promotee (a non-suspect participant) speaking firsthand about the boss (the victim) who promoted
        // them. First person — they were in it, so no rumour framing — with a soft, non-naming hint at the
        // disgruntled rivals: a lead INTO the suspect pool that still leaves finding who to the player. The
        // boss (subject) stays "they"; a promotion case always has passed-over rivals, so the hint is true.
        private static string PromoteeAboutBossLine(int seed)
            => Pick(seed,
                "They handed me a promotion. I reckon some people weren't too happy about it.",
                "They're the one who promoted me. A few people weren't best pleased, I can tell you.",
                "I got my promotion from them. Not everyone took it well.",
                "They gave me the promotion. I don't think it went down well with everyone.");

        // Rent-arrears gossip, collapsed to a COUNT (naming the tenants is pointless — the game exposes no
        // landlord->tenant trail to follow anyway). Subject = the LANDLORD; `count` = tenants they're chasing.
        private static string RentChaseLine(int count, int seed)
        {
            if (count <= 1)
                return Pick(seed,
                    "Word is one of their tenants had stopped paying rent.",
                    "Apparently they'd been chasing a tenant for unpaid rent.",
                    "Heard they had a tenant who wouldn't pay up.",
                    "I think one of their tenants had fallen behind on the rent.");
            string n = NumWord(count);
            return Pick(seed,
                $"Word is {n} of their tenants had stopped paying rent.",
                $"Apparently they'd been chasing {n} tenants for unpaid rent.",
                $"Heard {n} of their tenants had fallen behind on the rent.",
                $"I think they were chasing {n} tenants over unpaid rent.");
        }

        // Rent-arrears gossip when the subject is the TENANT who fell behind (they have one landlord).
        private static string RentBehindLine(int seed)
            => Pick(seed,
                "Apparently they'd fallen behind on their rent.",
                "Heard they'd been struggling to pay their rent.",
                "I think money had been tight and they'd missed rent.",
                "Word is they were behind on the rent.");

        // Spell small counts for natural gossip ("three tenants"); digits for larger.
        private static string NumWord(int n)
        {
            switch (n)
            {
                case 2: return "two";
                case 3: return "three";
                case 4: return "four";
                case 5: return "five";
                case 6: return "six";
                case 7: return "seven";
                case 8: return "eight";
                case 9: return "nine";
                default: return n.ToString();
            }
        }

        // Deterministic variant pick (stable per seed; never negative-indexes).
        private static string Pick(int seed, params string[] variants)
            => variants[((seed % variants.Length) + variants.Length) % variants.Length];

        // Rumour lead-ins we recognise. If a bubble repeats a lead-in already used earlier in the SAME
        // answer, swap it for a conversational follow-on so a multi-bubble answer never stacks the same
        // framing ("Apparently X. Apparently Y."). The clause after every lead-in starts with
        // they/there's/a name, so each follow-on stays grammatical.
        private static readonly string[] _leadIns =
            { "Word is ", "Apparently ", "Rumour has it ", "I think ", "I heard that ", "I heard ",
              "I gather ", "Someone mentioned ", "Heard " };
        private static readonly string[] _followOns = { "I also heard ", "And ", "On top of that, " };
        private static void DedupeOpeners(List<string> lines)
        {
            var usedLead = new HashSet<string>();
            int f = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var s = lines[i];
                if (string.IsNullOrEmpty(s)) continue;
                string lead = null;
                for (int j = 0; j < _leadIns.Length; j++) if (s.StartsWith(_leadIns[j])) { lead = _leadIns[j]; break; }
                if (lead == null) continue;
                if (usedLead.Add(lead)) continue;   // first appearance of this lead-in — keep it
                lines[i] = _followOns[f % _followOns.Length] + s.Substring(lead.Length);
                f++;
            }
        }

        // Keep a repeated hearsay tail on only the FIRST bubble that uses it; trim it from later ones so
        // the same "…, from what I hear." framing is never echoed twice in one interrogation answer.
        private static void DedupeHearsayTail(List<string> lines)
        {
            const string tail = ", from what I hear.";
            bool used = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var s = lines[i];
                if (string.IsNullOrEmpty(s) || !s.EndsWith(tail)) continue;
                if (used) lines[i] = s.Substring(0, s.Length - tail.Length) + ".";
                else used = true;
            }
        }

        private static bool ContainsHuman(List<Human> list, Human h)
        {
            if (h == null) return false;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].humanID == h.humanID) return true;
            return false;
        }

        // "X" / "X and Y" / "X, Y and Z"
        private static string JoinNames(List<Human> people)
        {
            if (people.Count == 0) return "someone";
            if (people.Count == 1) return Name(people[0]);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < people.Count; i++)
            {
                if (i > 0) sb.Append(i == people.Count - 1 ? " and " : ", ");
                sb.Append(Name(people[i]));
            }
            return sb.ToString();
        }

        // ---- helpers ----
        // One-to-one / two-party events whose testimony names the OTHER party — a party to one must NOT
        // relay it (they'd narrate themselves / confess their own motive). Multi-party events
        // (promotion/layoffs/eviction) name the SUBJECT's role instead, so involved parties may gossip
        // them (see the skip in ComposeLines, A4).
        private static bool IsOneToOne(SocialEventType t)
        {
            switch (t)
            {
                case SocialEventType.Affair:
                case SocialEventType.Feud:
                case SocialEventType.Debt:
                case SocialEventType.RentArrears:
                    return true;
                default:
                    return false;   // Promotion / Layoffs / Eviction — multi-party, exempt
            }
        }

        private static bool Involves(SocialEvent e, Human h)
            => h != null && ((e.a != null && e.a.humanID == h.humanID) || (e.b != null && e.b.humanID == h.humanID));

        private static Human SafePartner(Human h) { if (h == null) return null; try { return h.partner; } catch { return null; } }

        private static string Name(Human h) { if (h == null) return "someone"; try { return h.citizenName; } catch { return "someone"; } }

    }

    // Fire the gossip on the FRESH photo pick — runs AFTER the vanilla answer, and only
    // when a "do you know this person?" was armed (accepted) just before.
    [HarmonyPatch(typeof(PhotoSelectButtonController), nameof(PhotoSelectButtonController.OnLeftClick))]
    internal static class Patch_PhotoPick
    {
        static void Postfix(PhotoSelectButtonController __instance)
        {
            try { Interrogation.OnPicked(__instance.citizen); } catch { }
        }
    }

    // Replace the armed NPC's next answer bubble with our gossip line (rides the vanilla render).
    [HarmonyPatch(typeof(SpeechBubbleController), nameof(SpeechBubbleController.Setup))]
    internal static class Patch_Bubble
    {
        static void Postfix(SpeechBubbleController __instance, SpeechController newSpeechController)
            => Interrogation.OnBubbleSetup(__instance, newSpeechController);
    }

    // TESTING CHEAT (DebugTools.AlwaysAnswer, F8): force the "Do you know this person?" success roll
    // so NPCs always accept — no bribe needed. NOTE: dialog-option clicks invoke the special-case
    // HANDLERS directly (DoYouKnowThisPerson etc.), NOT this ExecuteDialog entry point (verified: the
    // ExecuteDialog prefix never fires on a click), so the real cheat is the handler prefixes below;
    // this stays as harmless belt-and-suspenders for any path that DOES route through ExecuteDialog.
    // (There is no forceable "tell me your name" dialog — that identity is surfaced by the F8 HUD
    // "TALKING TO" readout instead.)
    [HarmonyPatch(typeof(DialogController), nameof(DialogController.ExecuteDialog))]
    internal static class Patch_ForceKnowName
    {
        static void Prefix(EvidenceWitness.DialogOption dialog, ref DialogController.ForceSuccess forceSuccess)
        {
            if (!DebugTools.AlwaysAnswer) return;
            try
            {
                var p = dialog != null ? dialog.preset : null;
                if (p != null && p.specialCase == DialogPreset.SpecialCase.knowName)
                    forceSuccess = DialogController.ForceSuccess.success;
            }
            catch { }
        }
    }

    // Arm on the "Do you know this person?" decision (accept only). No speech here.
    // Prefix = belt-and-suspenders for the F8 cheat (in case the picker-open decision
    // reads `success` in the handler rather than honouring ExecuteDialog's ForceSuccess).
    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPerson))]
    internal static class Patch_DoYouKnow
    {
        static void Prefix(ref bool success) { if (DebugTools.AlwaysAnswer) success = true; }
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.ArmForPick(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe1))]
    internal static class Patch_DoYouKnowB1
    {
        static void Prefix(ref bool success) { if (DebugTools.AlwaysAnswer) success = true; }
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.ArmForPick(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe2))]
    internal static class Patch_DoYouKnowB2
    {
        static void Prefix(ref bool success) { if (DebugTools.AlwaysAnswer) success = true; }
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.ArmForPick(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe3))]
    internal static class Patch_DoYouKnowB3
    {
        static void Prefix(ref bool success) { if (DebugTools.AlwaysAnswer) success = true; }
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.ArmForPick(saysTo, saysToInteractable, success);
    }
}

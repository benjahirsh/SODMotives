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
            // Collapse the one-to-one money/affair relations into single list bubbles (like affairs):
            //   lovers  -> "having an affair with X, Y and Z"
            //   owedTo  -> creditors the subject owes  ("they owed X and Y money")
            //   owedBy  -> debtors who owe the subject  ("X and Y owed them money")
            // Everything else (workplace / property / feud) stays one bubble per event line.
            var lovers = new List<Human>();
            var owedTo = new List<Human>();   // subject is the debtor; these are their creditors
            var owedBy = new List<Human>();   // subject is the creditor; these are their debtors
            var workLines = new List<string>();
            foreach (var e in EventStore.KnownBy(npc.humanID))
            {
                // An NPC is NOT a knower of an event they are themselves a party to: they lived it,
                // they don't relay it as gossip. For a one-to-one event (feud/debt/affair) the
                // testimony names the OTHER party, so a party asked about the other would end up
                // naming THEMSELVES — which is how the killer was heard narrating their own feud with
                // the victim in the third person. (This also means an involved coworker/tenant no
                // longer relays a workplace/property event they're in — only uninvolved observers do.)
                if (e.InvolvesHuman(npc.humanID)) continue;

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
                else
                {
                    // Non-affair, non-debt (workplace / property / feud): the SUBJECT's own role.
                    // Bump the seed per line so multiple events of one type don't pick the same variant.
                    // Null when the subject isn't in this event.
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
            for (int i = 0; i < workLines.Count && lines.Count < MaxGossipBubbles; i++) lines.Add(workLines[i]);

            // A hearsay tail like ", from what I hear." reads oddly repeated across bubbles in one answer.
            // Keep it on the first line that uses it and trim it from the rest (the sentence still reads
            // fine without it), so the same framing is never echoed back-to-back.
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
                $"Word is they've been having an affair with {JoinNames(lovers)}.",
                $"They've been sleeping with {JoinNames(lovers)}, from what I hear.",
                $"Word is there's something going on between them and {JoinNames(lovers)}.");
            if (sp != null) ln += $" Can't imagine {Name(sp)} took that well.";
            return ln;
        }

        // Debt gossip, collapsed like affairs. Subject = "they". Variants read correctly for one name or
        // a "X, Y and Z" list (no singular/plural verb agreement on the list).
        private static string DebtOwedLine(List<Human> creditors, int seed)   // subject OWES these people
            => Pick(seed,
                $"Word is they owed {JoinNames(creditors)} money.",
                $"Heard they were in debt to {JoinNames(creditors)}.",
                $"They owed {JoinNames(creditors)} money and hadn't paid it back, from what I hear.",
                $"Word is they'd borrowed money off {JoinNames(creditors)} and never repaid it.");

        private static string DebtOwedToThemLine(List<Human> debtors, int seed)   // these people OWE the subject
            => Pick(seed,
                $"Word is {JoinNames(debtors)} owed them money.",
                $"Heard {JoinNames(debtors)} still owed them money.",
                $"{JoinNames(debtors)} owed them money and hadn't paid it back, from what I hear.",
                $"Word is they'd lent {JoinNames(debtors)} money that was never repaid.");

        // Deterministic variant pick (stable per seed; never negative-indexes).
        private static string Pick(int seed, params string[] variants)
            => variants[((seed % variants.Length) + variants.Length) % variants.Length];

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

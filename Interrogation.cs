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

        // Gossip armed for the NPC's NEXT answer bubble. The DDS/Strings/Speak path cannot
        // render runtime text (Strings.Get rejects our entry — proven via spdiag), so instead
        // we let the vanilla knowName answer create its bubble and REPLACE that bubble's text
        // with our line (Patch_Bubble on SpeechBubbleController.Setup). Rides the proven render.
        private static int _pendingHumanId = -1;
        private static string _pendingText;

        // Postfix target for DoYouKnowThisPerson(+Bribe1/2/3): ARM ONLY, never speak.
        internal static void ArmForPick(Citizen npc, Interactable speakingTo, bool success)
        {
            if (!Enable) return;
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
                if (EventStore.Count == 0) AffairSim.SeedForNewGame();

                string text = ComposeAbout(npc, subject);
                if (text == null)
                {
                    MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {Name(npc)} asked about {Name(subject)} -> (nothing to add)");
                    return; // this NPC knows no affair involving the subject; leave vanilla answer
                }

                // Arm the bubble hijack: the NPC's next answer bubble becomes our gossip line.
                _pendingHumanId = npc.humanID;
                _pendingText = text;
                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {Name(npc)} asked about {Name(subject)} -> \"{text}\" (armed bubble)");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] OnPicked error: {e.Message}"); }
        }

        // Replace the armed NPC's next speech bubble with our gossip, riding the vanilla render.
        internal static void MaybeHijackBubble(SpeechBubbleController bubble, SpeechController sc)
        {
            if (_pendingHumanId < 0 || bubble == null || sc == null) return;
            try
            {
                Human speaker = null;
                try { var a = sc.actor; if (a != null) speaker = a.TryCast<Human>(); } catch { }
                if (speaker == null || speaker.humanID != _pendingHumanId) return;

                string text = _pendingText;
                _pendingHumanId = -1; _pendingText = null;   // one-shot

                // Swap the bubble's target text + word list and restart the typewriter reveal so
                // it plays OUR line instead of the vanilla answer.
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
                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] hijacked bubble for {Name(speaker)} -> \"{text}\"");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] hijack error: {e.Message}"); }
        }

        // What this NPC can add about the picked person: an affair the SUBJECT is a
        // participant in (never the NPC's own). Generic — works for any citizen, not just
        // murder-involved ones. A non-participant (e.g. a betrayed spouse) correctly yields
        // nothing: there is no affair-gossip about someone who isn't in an affair. Such a
        // victim's murder is still solvable by following the graph to their cheating partner.
        private static string ComposeAbout(Human npc, Human subject)
        {
            SocialEvent affair = null;
            foreach (var e in EventStore.KnownBy(npc.humanID))
            {
                if (e.type != SocialEventType.Affair) continue;
                if (!Involves(e, subject)) continue;
                if (Involves(e, npc)) continue; // won't tattle on their own affair
                affair = e; break;
            }
            if (affair == null) return null;

            Human other = Motive.Same(affair.a, subject) ? affair.b : affair.a;
            Human sp = SafePartner(subject);
            var sb = new System.Text.StringBuilder();
            sb.Append($"Actually — since you ask about {Name(subject)}: between us, word is they'd been carrying on with {Name(other)} behind their partner's back. ");
            if (sp != null) sb.Append($"Can't imagine {Name(sp)} took that well.");
            return sb.ToString().TrimEnd();
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
            => Interrogation.MaybeHijackBubble(__instance, newSpeechController);
    }

    // TESTING CHEAT (DebugTools.AlwaysAnswer, F8): force the "Do you know this person?"
    // success roll so NPCs always accept — no bribe needed. This runs upstream of the
    // picker-vs-bribe branch, using the engine's own ForceSuccess override.
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

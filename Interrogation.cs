using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime;

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
        private static Interactable _speakingTo;
        private static int _answerCounter;

        // Postfix target for DoYouKnowThisPerson(+Bribe1/2/3): ARM ONLY, never speak.
        internal static void ArmForPick(Citizen npc, Interactable speakingTo, bool success)
        {
            if (!Enable) return;
            try
            {
                if (success && npc != null)
                {
                    _armed = true; _npc = npc; _speakingTo = speakingTo;
                    MotivesPlugin.Log.LogInfo($"[SODMotives][interro] armed — {Name(npc)} accepted 'do you know this person?'");
                }
                else
                {
                    // Reject (needs a bribe / refused): clear so the next pick can't replay us.
                    _armed = false; _npc = null; _speakingTo = null;
                }
            }
            catch { _armed = false; _npc = null; _speakingTo = null; }
        }

        // Postfix target for PhotoSelectButtonController.OnLeftClick: fire once on the fresh pick.
        internal static void OnPicked(Human subject)
        {
            if (!Enable || !_armed) return;
            Citizen npc = _npc; Interactable speakingTo = _speakingTo;
            _armed = false; _npc = null; _speakingTo = null;   // one-shot: never double-fire/replay

            try
            {
                if (npc == null || subject == null || npc.speechController == null) return;
                if (EventStore.Count == 0) AffairSim.SeedForNewGame();

                string text = ComposeAbout(npc, subject);
                if (text == null) return; // we know nothing extra -> leave the vanilla answer alone

                Human.InteractionDialogInstance inter = null;
                try { var ie = npc.interactionEvents; if (ie != null && ie.Count > 0) inter = ie[0]; } catch { }

                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {Name(npc)} asked about {Name(subject)} -> \"{text}\"");
                SpeakLine(npc, speakingTo, inter, subject, text);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] OnPicked error: {e.Message}"); }
        }

        // What this NPC can add about the picked person (never their own affair).
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

        private static void SpeakLine(Human npc, Interactable speakingTo, Human.InteractionDialogInstance inter, Human about, string text)
        {
            try
            {
                string blockId = "MOD_Motives_Ans_" + (_answerCounter++);
                var tb = Toolbox.Instance;
                var block = new DDSSaveClasses.DDSBlockSave();
                block.name = text; block.id = blockId;
                tb.allDDSBlocks[blockId] = block;
                Strings.WriteToDictionary("dds.blocks", blockId, "SODMotives", text);

                // interupt=false so we ADD after the vanilla line; speakingTo + inter bind the
                // bubble to the interrogation subtitle so it actually renders.
                npc.speechController.Speak(
                    "dds.blocks", blockId,
                    false,  // useParsing
                    false,  // shout
                    false,  // interupt (append after vanilla)
                    0f,     // delay
                    false, default(Color),
                    about,  // speakingAbout
                    false, false, null,
                    null,   // dialogPreset
                    null,   // dialog
                    speakingTo, inter);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] SpeakLine error: {e.Message}"); }
        }
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

    // Arm on the "Do you know this person?" decision (accept only). No speech here.
    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPerson))]
    internal static class Patch_DoYouKnow
    {
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.ArmForPick(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe1))]
    internal static class Patch_DoYouKnowB1
    {
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.ArmForPick(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe2))]
    internal static class Patch_DoYouKnowB2
    {
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.ArmForPick(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe3))]
    internal static class Patch_DoYouKnowB3
    {
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.ArmForPick(saysTo, saysToInteractable, success);
    }
}

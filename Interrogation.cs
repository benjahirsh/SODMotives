using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace SODMotives
{
    // V2 interrogation, piggybacking the NATIVE "Do you know this person?" flow:
    //   - the player picks any citizen X in the vanilla photo picker
    //   - we capture X, and after the vanilla answer we ADD what this NPC knows about X
    //     (their affairs), spoken through the NPC.
    // No custom dialog option, no menu injection — the game already provides the UI.
    internal static class Interrogation
    {
        internal static bool Enable = true;
        internal static Human LastPicked;   // set by the photo-picker prefix

        private static int _answerCounter;

        // Called from the DoYouKnowThisPerson postfix(es).
        internal static void OnAskedAboutPerson(Citizen npc, Interactable speakingTo, bool success)
        {
            if (!Enable) return;
            try
            {
                if (npc == null || npc.speechController == null) return;
                Human subject = LastPicked;
                if (subject == null) return;
                if (EventStore.Count == 0) AffairSim.SeedForNewGame();

                string text = ComposeAbout(npc, subject);
                if (text == null) return; // we know nothing extra -> leave the vanilla answer alone

                Human.InteractionDialogInstance inter = null;
                try { var ie = npc.interactionEvents; if (ie != null && ie.Count > 0) inter = ie[0]; } catch { }

                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {Name(npc)} asked about {Name(subject)} -> \"{text}\"");
                SpeakLine(npc, speakingTo, inter, subject, text);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] OnAsked error: {e.Message}"); }
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

    // Capture the picked citizen the moment a photo button is clicked.
    [HarmonyPatch(typeof(PhotoSelectButtonController), nameof(PhotoSelectButtonController.OnLeftClick))]
    internal static class Patch_PhotoPick
    {
        static void Prefix(PhotoSelectButtonController __instance)
        {
            try { Interrogation.LastPicked = __instance.citizen; } catch { }
        }
    }

    // After the native "Do you know this person?" answer, add our knowledge about the pick.
    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPerson))]
    internal static class Patch_DoYouKnow
    {
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.OnAskedAboutPerson(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe1))]
    internal static class Patch_DoYouKnowB1
    {
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.OnAskedAboutPerson(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe2))]
    internal static class Patch_DoYouKnowB2
    {
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.OnAskedAboutPerson(saysTo, saysToInteractable, success);
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.DoYouKnowThisPersonBribe3))]
    internal static class Patch_DoYouKnowB3
    {
        static void Postfix(Citizen saysTo, Interactable saysToInteractable, bool success)
            => Interrogation.OnAskedAboutPerson(saysTo, saysToInteractable, success);
    }
}

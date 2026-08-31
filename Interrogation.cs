using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppDialogOption = EvidenceWitness.DialogOption;
using Il2CppOptionList = Il2CppSystem.Collections.Generic.List<EvidenceWitness.DialogOption>;

namespace SODMotives
{
    // V2 interrogation: a targeted question — "What do you know about <the victim>?" —
    // whose answer the mod composes at runtime from the NPC's real event knowledge and
    // speaks through them. SoD has no native "subject person" for a dialog option, so we
    // carry the subject ourselves (currently the case victim; arbitrary people + pairs
    // will use a per-option subject map later).
    internal static class Interrogation
    {
        internal static bool Enable = true;

        private const string MsgId = "MOD_Q_AboutPerson";
        private const string BlockId = MsgId + "_blk";

        private static bool _inited;
        private static DialogPreset _preset;
        private static int _answerCounter;

        internal static void EnsureInit()
        {
            if (_inited) return;
            try
            {
                var tb = Toolbox.Instance;
                if (tb == null || tb.allDDSMessages == null) return; // not ready yet
                _inited = true;

                InjectLabelMessage(MsgId, "(Motives) What do you know about them?"); // placeholder; retargeted per case

                _preset = ScriptableObject.CreateInstance<DialogPreset>();
                _preset.name = MsgId;
                _preset.msgID = MsgId;
                _preset.specialCase = DialogPreset.SpecialCase.none;
                _preset.defaultOption = true;
                _preset.tiedToKey = Evidence.DataKey.name;
                _preset.ranking = 50;
                _preset.cost = 0;
                _preset.baseChance = 1f;
                _preset.useSuccessTest = false;
                _preset.removeAfterSaying = false;
                _preset.dailyReplenish = true;
                _preset.preceedingSyntax = "";
                _preset.followingSyntax = "";
                try { _preset.responses = new Il2CppSystem.Collections.Generic.List<AIActionPreset.AISpeechPreset>(); } catch { }

                MotivesPlugin.Log.LogInfo("[SODMotives][interro] targeted question ready.");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] init error: {e}"); }
        }

        // Build the button-label DDS message (preset.msgID -> message -> block -> Strings text).
        // Explicit field assignment (object initializers don't reliably set Il2Cpp fields).
        private static void InjectLabelMessage(string msgId, string text)
        {
            try
            {
                var tb = Toolbox.Instance;
                string blockId = msgId + "_blk";

                var block = new DDSSaveClasses.DDSBlockSave();
                block.name = text; block.id = blockId;
                tb.allDDSBlocks[blockId] = block;
                Strings.WriteToDictionary("dds.blocks", blockId, "SODMotives", text);

                var cond = new DDSSaveClasses.DDSBlockCondition();
                cond.blockID = blockId; cond.instanceID = msgId + "_inst"; cond.alwaysDisplay = true; cond.group = 0;

                var msg = new DDSSaveClasses.DDSMessageSave();
                msg.name = msgId; msg.id = msgId;
                msg.blocks = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSBlockCondition>();
                msg.blocks.Add(cond);
                tb.allDDSMessages[msgId] = msg;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] label('{msgId}') error: {e}"); }
        }

        private static void SetLabel(string text)
        {
            try { Strings.WriteToDictionary("dds.blocks", BlockId, "SODMotives", text); } catch { }
        }

        internal static bool IsOurPreset(DialogPreset p)
        {
            try { return p != null && p.msgID == MsgId; } catch { return false; }
        }

        // ---- menu ----
        internal static void AddOption(Evidence.DataKey key, Il2CppOptionList list)
        {
            if (!Enable) return;
            EnsureInit();
            if (_preset == null || list == null) return;
            // Add for ONE facet key only, or the menu (which queries many keys) shows duplicates.
            if (key != Evidence.DataKey.name) return;

            Human victim = CurrentVictim();
            if (victim == null) return; // only meaningful during an active case

            try
            {
                SetLabel($"(Motives) What do you know about {SocialEvent.SafeName(victim)}?");
                for (int i = 0; i < list.Count; i++)
                {
                    var o = list[i];
                    if (o != null && o.preset != null && o.preset.msgID == MsgId) return; // already present
                }
                var opt = new Il2CppDialogOption(); opt.preset = _preset; list.Add(opt);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] AddOption error: {e.Message}"); }
        }

        // ---- answer ----
        internal static void Answer(Human npc, Interactable speakingTo, Human.InteractionDialogInstance interaction)
        {
            try
            {
                if (npc == null || npc.speechController == null) { MotivesPlugin.Log.LogInfo("[SODMotives][interro] no npc/speech."); return; }
                if (EventStore.Count == 0) AffairSim.SeedForNewGame();
                string nn = "someone"; try { nn = npc.citizenName; } catch { }

                Human victim = CurrentVictim();
                string text = AnswerAboutVictim(npc, victim);

                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {nn} about {SocialEvent.SafeName(victim)} -> \"{text}\"");
                SpeakLine(npc, speakingTo, interaction, text);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] Answer error: {e}"); }
        }

        private static string AnswerAboutVictim(Human npc, Human victim)
        {
            if (victim == null) return "Not sure who you mean.";
            string vn = SocialEvent.SafeName(victim);

            var rel = AffairsAbout(npc, victim);
            if (rel.Count == 0) return $"{vn}? Can't say I know much about them, sorry.";

            var e = rel[0];
            Human other = Motive.Same(e.a, victim) ? e.b : e.a;
            Human vPartner = SafePartner(victim);
            Human oPartner = SafePartner(other);

            var sb = new System.Text.StringBuilder();
            sb.Append($"{vn}? Between us — word is they'd been carrying on with {SocialEvent.SafeName(other)} behind their partner's back. ");
            if (vPartner != null) sb.Append($"Can't imagine {SocialEvent.SafeName(vPartner)} took that well. ");
            if (oPartner != null) sb.Append($"And {SocialEvent.SafeName(other)}'s partner {SocialEvent.SafeName(oPartner)} wouldn't be pleased either.");
            return sb.ToString().TrimEnd();
        }

        // ---- helpers ----
        private static bool Involves(SocialEvent e, Human h)
            => (e.a != null && e.a.humanID == h.humanID) || (e.b != null && e.b.humanID == h.humanID);

        private static List<SocialEvent> AffairsAbout(Human npc, Human person)
        {
            var res = new List<SocialEvent>();
            if (person == null) return res;
            foreach (var e in EventStore.KnownBy(npc.humanID))
                if (e.type == SocialEventType.Affair && Involves(e, person)) res.Add(e);
            return res;
        }

        private static Human SafePartner(Human h)
        {
            if (h == null) return null;
            try { return h.partner; } catch { return null; }
        }

        private static Human CurrentVictim()
        {
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) return null;
                try { var m = mc.GetCurrentMurder(); if (m != null && m.victim != null) return m.victim; } catch { }
                return mc.currentVictim;
            }
            catch { return null; }
        }

        private static void SpeakLine(Human npc, Interactable speakingTo, Human.InteractionDialogInstance interaction, string text)
        {
            try
            {
                string blockId = "MOD_Motives_Ans_" + (_answerCounter++);
                var tb = Toolbox.Instance;
                var block = new DDSSaveClasses.DDSBlockSave();
                block.name = text; block.id = blockId;
                tb.allDDSBlocks[blockId] = block;
                Strings.WriteToDictionary("dds.blocks", blockId, "SODMotives", text);

                npc.speechController.Speak(
                    "dds.blocks", blockId,
                    false, false, true, 0f, false, default(Color),
                    null, false, false, null,
                    _preset, null,
                    speakingTo, interaction);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] SpeakLine error: {e.Message}"); }
        }
    }

    // Add the question for one facet key (avoids duplicates from multi-key menu builds).
    [HarmonyPatch(typeof(EvidenceWitness), nameof(EvidenceWitness.GetDialogOptions), new Type[] { typeof(Evidence.DataKey) })]
    internal static class Patch_GetDialogOptions_Single
    {
        static void Postfix(Evidence.DataKey key, Il2CppOptionList __result) => Interrogation.AddOption(key, __result);
    }

    // Answer our question ourselves.
    [HarmonyPatch(typeof(DialogController), nameof(DialogController.ExecuteDialog))]
    internal static class Patch_ExecuteDialog
    {
        static bool Prefix(DialogController __instance, Il2CppDialogOption dialog, Interactable saysTo, Human.InteractionDialogInstance interactionInstance)
        {
            try
            {
                if (dialog == null || !Interrogation.IsOurPreset(dialog.preset)) return true; // vanilla
                Human npc = null;
                try { if (__instance != null) npc = __instance.askTarget; } catch { }
                if (npc == null && saysTo != null) { try { var a = saysTo.isActor; if (a != null) npc = a.TryCast<Human>(); } catch { } }
                Interrogation.Answer(npc, saysTo, interactionInstance);
                return false;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] ExecuteDialog prefix error: {e.Message}"); return true; }
        }
    }
}

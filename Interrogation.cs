using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppDialogOption = EvidenceWitness.DialogOption;
using Il2CppOptionList = Il2CppSystem.Collections.Generic.List<EvidenceWitness.DialogOption>;

namespace SODMotives
{
    // V2 interrogation: adds custom player questions whose answers the mod composes at
    // runtime from real event knowledge and speaks through the interviewed NPC.
    // Extensible: add a Question to _questions and it appears + answers automatically.
    internal static class Interrogation
    {
        internal static bool Enable = true;

        private class Question
        {
            public string msgId;                 // also the Strings/preset key
            public string label;                 // button text
            public DialogPreset preset;          // built at init
            public Func<Human, string> answer;   // compose the NPC's reply
            public Func<bool> applicable;        // when to show it (e.g. only during a case)
        }

        private static bool _inited;
        private static readonly List<Question> _questions = new List<Question>();
        private static int _answerCounter;
        private static DialogPreset _anyPreset;   // for the Speak dialogPreset arg
        private static readonly Dictionary<int, int> _gossipCycle = new Dictionary<int, int>();

        internal static void EnsureInit()
        {
            if (_inited) return;
            try
            {
                var tb = Toolbox.Instance;
                if (tb == null || tb.allDDSMessages == null) return; // not ready yet
                _inited = true;

                Add("MOD_Q_Gossip", "(Motives) Noticed anything going on between people lately?", AnswerGossip, () => true);
                Add("MOD_Q_WhoHarm", "(Motives) Who'd have a reason to hurt the victim?", AnswerWhoHarm, () => CurrentVictim() != null);
                Add("MOD_Q_AboutVictim", "(Motives) What can you tell me about the victim?", AnswerAboutVictim, () => CurrentVictim() != null);

                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {_questions.Count} questions ready.");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] init error: {e}"); }
        }

        private static void Add(string msgId, string label, Func<Human, string> answer, Func<bool> applicable)
        {
            InjectLabelMessage(msgId, label);
            var p = ScriptableObject.CreateInstance<DialogPreset>();
            p.name = msgId;
            p.msgID = msgId;
            p.specialCase = DialogPreset.SpecialCase.none;
            p.defaultOption = true;
            p.tiedToKey = Evidence.DataKey.name;
            p.ranking = 50;
            p.cost = 0;
            p.baseChance = 1f;
            p.useSuccessTest = false;
            p.removeAfterSaying = false;
            p.dailyReplenish = true;
            p.preceedingSyntax = "";
            p.followingSyntax = "";
            try { p.responses = new Il2CppSystem.Collections.Generic.List<AIActionPreset.AISpeechPreset>(); } catch { }
            if (_anyPreset == null) _anyPreset = p;
            _questions.Add(new Question { msgId = msgId, label = label, preset = p, answer = answer, applicable = applicable });
        }

        // A question's button label needs a DDS message (preset.msgID -> message -> block ->
        // Strings text). Answers use the simpler direct-Speak path (SpeakLine).
        private static void InjectLabelMessage(string msgId, string text)
        {
            try
            {
                var tb = Toolbox.Instance;
                string blockId = msgId + "_blk";

                // Explicit field assignments — object initializers on Il2CppInterop types
                // don't reliably set fields, which left condition.blockID empty -> blank label.
                var block = new DDSSaveClasses.DDSBlockSave();
                block.name = text;
                block.id = blockId;
                tb.allDDSBlocks[blockId] = block;
                Strings.WriteToDictionary("dds.blocks", blockId, "SODMotives", text);

                var cond = new DDSSaveClasses.DDSBlockCondition();
                cond.blockID = blockId;
                cond.instanceID = msgId + "_inst";
                cond.alwaysDisplay = true;
                cond.group = 0;

                var msg = new DDSSaveClasses.DDSMessageSave();
                msg.name = msgId;
                msg.id = msgId;
                msg.blocks = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSBlockCondition>();
                msg.blocks.Add(cond);
                tb.allDDSMessages[msgId] = msg;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] label('{msgId}') error: {e}"); }
        }

        private static Question Find(DialogPreset p)
        {
            if (p == null) return null;
            string id = null; try { id = p.msgID; } catch { }
            if (id == null) return null;
            for (int i = 0; i < _questions.Count; i++) if (_questions[i].msgId == id) return _questions[i];
            return null;
        }

        internal static bool IsOurPreset(DialogPreset p) => Find(p) != null;

        // ---- answers ----------------------------------------------------------

        private static string AnswerGossip(Human npc)
        {
            var tellable = new List<SocialEvent>();
            foreach (var e in EventStore.KnownBy(npc.humanID))
                if (!Involves(e, npc)) tellable.Add(e);  // never gossip about your own affair
            if (tellable.Count == 0) return "Can't say I've noticed anything out of the ordinary.";
            int c = _gossipCycle.TryGetValue(npc.humanID, out int v) ? v : 0;
            _gossipCycle[npc.humanID] = c + 1;
            return tellable[c % tellable.Count].Testimony(c);
        }

        private static string AnswerWhoHarm(Human npc)
        {
            Human victim = CurrentVictim();
            if (victim == null) return "No one in particular springs to mind.";
            string vn = SocialEvent.SafeName(victim);
            var rel = AffairsAbout(npc, victim);
            if (rel.Count == 0) return $"Couldn't tell you who'd have it in for {vn}.";

            var e = rel[0];
            Human other = Motive.Same(e.a, victim) ? e.b : e.a;
            Human betrayed = SafePartner(victim);       // the spouse victim was cheating on
            Human loverSpouse = SafePartner(other);     // the lover's spouse

            var sb = new System.Text.StringBuilder();
            sb.Append($"If you're asking who'd want {vn} gone — word is {vn} was carrying on with {SocialEvent.SafeName(other)}. ");
            if (betrayed != null) sb.Append($"I'd start with their partner, {SocialEvent.SafeName(betrayed)}. ");
            if (loverSpouse != null) sb.Append($"And {SocialEvent.SafeName(other)}'s partner, {SocialEvent.SafeName(loverSpouse)}, wouldn't be pleased either.");
            return sb.ToString().TrimEnd();
        }

        private static string AnswerAboutVictim(Human npc)
        {
            Human victim = CurrentVictim();
            if (victim == null) return "Not sure who you mean.";
            string vn = SocialEvent.SafeName(victim);
            var rel = AffairsAbout(npc, victim);
            if (rel.Count == 0) return $"Nothing much I can tell you about {vn}, sorry.";
            var e = rel[0];
            Human other = Motive.Same(e.a, victim) ? e.b : e.a;
            return $"{vn}? Between us, they'd been seeing {SocialEvent.SafeName(other)} behind their partner's back.";
        }

        // ---- helpers ----------------------------------------------------------

        private static bool Involves(SocialEvent e, Human h)
            => (e.a != null && e.a.humanID == h.humanID) || (e.b != null && e.b.humanID == h.humanID);

        private static List<SocialEvent> AffairsAbout(Human npc, Human person)
        {
            var res = new List<SocialEvent>();
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

        // ---- menu + speech ----------------------------------------------------

        internal static void AddOptions(Il2CppOptionList list)
        {
            if (!Enable) return;
            EnsureInit();
            if (list == null || _questions.Count == 0) return;
            try
            {
                foreach (var q in _questions)
                {
                    bool ok = true; try { ok = q.applicable == null || q.applicable(); } catch { }
                    if (!ok) continue;
                    bool present = false;
                    for (int i = 0; i < list.Count; i++) { var o = list[i]; if (o != null && o.preset != null && o.preset.msgID == q.msgId) { present = true; break; } }
                    if (present) continue;
                    var opt = new Il2CppDialogOption(); opt.preset = q.preset; list.Add(opt);
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] AddOptions error: {e.Message}"); }
        }

        // Returns true if we handled it (our question).
        internal static bool TryHandle(Human npc, Interactable speakingTo, Human.InteractionDialogInstance interaction, DialogPreset preset)
        {
            var q = Find(preset);
            if (q == null) return false;
            try
            {
                if (npc == null || npc.speechController == null) { MotivesPlugin.Log.LogInfo("[SODMotives][interro] no npc/speech."); return true; }
                if (EventStore.Count == 0) AffairSim.SeedForNewGame(); // safety net
                string name = "someone"; try { name = npc.citizenName; } catch { }
                string text;
                try { text = q.answer(npc) ?? "..."; } catch (Exception ae) { text = "..."; MotivesPlugin.Log.LogWarning($"[SODMotives][interro] answer err: {ae.Message}"); }
                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {name} [{q.msgId}] -> \"{text}\"");
                SpeakLine(npc, speakingTo, interaction, text);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] handle error: {e}"); }
            return true;
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

                // Full overload: speakingTo + interaction bind the line to the interrogation
                // subtitle; interupt clears any stuck element.
                npc.speechController.Speak(
                    "dds.blocks", blockId,
                    false, false, true, 0f, false, default(Color),
                    null, false, false, null,
                    _anyPreset, null,
                    speakingTo, interaction);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] SpeakLine error: {e.Message}"); }
        }
    }

    // Inject our questions into the interview menu (single-DataKey overload; the List
    // overload calls it internally, so patching both would duplicate entries).
    [HarmonyPatch(typeof(EvidenceWitness), nameof(EvidenceWitness.GetDialogOptions), new Type[] { typeof(Evidence.DataKey) })]
    internal static class Patch_GetDialogOptions_Single
    {
        static void Postfix(Il2CppOptionList __result) => Interrogation.AddOptions(__result);
    }

    // Answer our questions ourselves (skip vanilla handling).
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
                Interrogation.TryHandle(npc, saysTo, interactionInstance, dialog.preset);
                return false; // handled
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] ExecuteDialog prefix error: {e.Message}"); return true; }
        }
    }
}

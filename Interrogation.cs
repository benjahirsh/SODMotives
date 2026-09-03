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
                if (text == null)
                {
                    // Visible in the log so a pick that yields nothing is distinguishable from a
                    // pick that never fired (this NPC simply knows no affair touching the subject).
                    MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {Name(npc)} asked about {Name(subject)} -> (nothing to add)");
                    return; // leave the vanilla answer alone
                }

                Human.InteractionDialogInstance inter = null;
                try { var ie = npc.interactionEvents; if (ie != null && ie.Count > 0) inter = ie[0]; } catch { }

                MotivesPlugin.Log.LogInfo($"[SODMotives][interro] {Name(npc)} asked about {Name(subject)} -> \"{text}\"");
                SpeakLine(npc, speakingTo, inter, subject, text);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] OnPicked error: {e.Message}"); }
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

        private static void SpeakLine(Human npc, Interactable speakingTo, Human.InteractionDialogInstance inter, Human about, string text)
        {
            try
            {
                // RELIABLE PATH — matches the first proven interrogation PoC: bake the line into
                // a runtime DDS BLOCK **and MESSAGE**, then Speak the MESSAGE id (overload #2).
                // The direct (dictionary, entryRef) overload resolves via Strings.Get, which does
                // NOT surface our runtime WriteToDictionary this session -> BLANK bubble (it only
                // ever rendered stale values loaded from disk). The message path resolves the
                // in-memory block instead. GUID ids so nothing collides across sessions.
                var tb = Toolbox.Instance;
                string msgId = "MOD_Motives_Msg_" + System.Guid.NewGuid().ToString("N");
                string blockId = msgId + "_blk";

                var block = new DDSSaveClasses.DDSBlockSave();
                block.name = text; block.id = blockId;
                tb.allDDSBlocks[blockId] = block;
                Strings.WriteToDictionary("dds.blocks", blockId, "SODMotives", text);
                // GROUND TRUTH (spdiag): Strings.Get("dds.blocks", key) returned '' after
                // WriteToDictionary — the render reads stringTable[dict][key].displayStr, which
                // WriteToDictionary wasn't populating readably this session. Write it DIRECTLY.
                WriteStringDirect("dds.blocks", blockId, text);

                var cond = new DDSSaveClasses.DDSBlockCondition();
                cond.blockID = blockId;
                cond.instanceID = msgId + "_inst";
                cond.alwaysDisplay = true;
                cond.group = 0;

                var msg = new DDSSaveClasses.DDSMessageSave();
                msg.name = msgId; msg.id = msgId;
                msg.blocks = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSBlockCondition>();
                msg.blocks.Add(cond);
                tb.allDDSMessages[msgId] = msg;

                // overload #2: Speak(ddsMessage, shout, interupt, speakAbout, sideJob, interactionInstance).
                // ALL trailing args MUST be null. A non-null interactionInstance makes
                // SpeechBubbleController.Setup resolve the bubble text from THAT interaction's branch
                // message (which never contains our runtime message) -> EMPTY/blank bubble. null makes
                // Setup fall back to the queue element's own msgId -> resolves via allDDSMessages and
                // renders. speakAbout null too (our line is literal, no DDS tokens). Mirrors the PoC.
                npc.speechController.Speak(msgId, false, false, null, null, null);

                // ---- DIAGNOSTICS (temporary): pin down WHERE the text is lost ----
                try
                {
                    string rb = null; try { rb = Strings.Get("dds.blocks", blockId); } catch { }
                    bool hasMsg = false, hasBlk = false;
                    try { hasMsg = tb.allDDSMessages.ContainsKey(msgId); } catch { }
                    try { hasBlk = tb.allDDSBlocks.ContainsKey(blockId); } catch { }
                    var sc = npc.speechController;
                    int qc = -1; bool active = false, bub = false;
                    try { qc = sc.speechQueue != null ? sc.speechQueue.Count : -1; } catch { }
                    try { active = sc.speechActive; } catch { }
                    try { bub = sc.activeSpeechBubble != null; } catch { }
                    MotivesPlugin.Log.LogInfo($"[SODMotives][spdiag] afterSpeak Strings.Get(dds.blocks,{blockId})='{rb}' (len={(rb == null ? -1 : rb.Length)}) hasMsg={hasMsg} hasBlk={hasBlk} | queue={qc} active={active} bubble={bub}");
                    _watchSc = sc; _watchFrames = 10; _watchText = text;
                }
                catch (Exception d) { MotivesPlugin.Log.LogWarning($"[SODMotives][spdiag] err: {d.Message}"); }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] SpeakLine error: {e.Message}"); }
        }

        // Write a display string straight into Strings.stringTable[dict][key].displayStr — the
        // exact place Strings.Get reads. WriteToDictionary did not surface runtime writes readably
        // this session (confirmed via spdiag), so we populate the read store directly.
        private static void WriteStringDirect(string dict, string key, string text)
        {
            try
            {
                var table = Strings.stringTable;
                if (table == null) return;
                Il2CppSystem.Collections.Generic.Dictionary<string, Strings.DisplayString> inner;
                if (!table.TryGetValue(dict, out inner) || inner == null)
                {
                    inner = new Il2CppSystem.Collections.Generic.Dictionary<string, Strings.DisplayString>();
                    table[dict] = inner;
                }
                var ds = new Strings.DisplayString();
                ds.displayStr = text;
                ds.alternateStr = text;
                inner[key] = ds;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][interro] WriteStringDirect: {e.Message}"); }
        }

        // Deferred read of the speech bubble a few frames after Speak, so we can see whether
        // the game resolved our text into the visible bubble or left it empty. Driven by
        // DebugHotkey.Update -> TickWatch(). (Diagnostics; remove once speech is confirmed.)
        private static SpeechController _watchSc;
        private static int _watchFrames;
        private static string _watchText;

        internal static void TickWatch()
        {
            if (_watchSc == null) return;
            if (--_watchFrames > 0) return;
            var sc = _watchSc; _watchSc = null;
            try
            {
                var b = sc.activeSpeechBubble;
                string actual = null; int wlen = -1; bool fin = false;
                if (b != null)
                {
                    try { actual = b.actualString; } catch { }
                    try { var w = b.words; wlen = w != null ? w.Length : -1; } catch { }
                    try { fin = b.setFinalText; } catch { }
                }
                MotivesPlugin.Log.LogInfo($"[SODMotives][spdiag] +frames bubble={(b != null)} actualString='{actual}' (len={(actual == null ? -1 : actual.Length)}) words={wlen} final={fin} | expected='{_watchText}'");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][spdiag] tick err: {e.Message}"); }
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

using System;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace SODMotives
{
    // "Can I talk to your partner?" — a NEW interrogation option when you talk to someone
    // AT THEIR HOME. On success their partner comes to the door and the person you were
    // talking to steps away (the game closes the door behind them, the partner walks up
    // and opens it). Reimplemented from piepieonline's DialogAdditions (TalkToPartner) but
    // ADDITIVELY: every hook here is a Harmony POSTFIX and never skips vanilla, so we do NOT
    // pull in DialogAdditions' dependencies (SOD.Common / Asset Bundle Loader / DDSLoader) and
    // do not fight its prefix-skips of TestSpecialCaseAvailability / WarnNotewriter.
    //
    // How the four pieces fit the vanilla dialog pipeline (decoded from the IL2CPP build):
    //   APPEAR — the talk menu (InteractionController.RefreshDialogOptions) is built from
    //            EvidenceWitness.GetDialogOptions(DataKey). For a face-to-face chat it fetches
    //            the DataKey.voice list, so a POSTFIX on that overload appends our option.
    //   GATE   — every option is filtered through DialogController.TestSpecialCaseAvailability;
    //            a POSTFIX there shows ours only when the target is home, not homeless, and has
    //            a partner (saysTo is the Citizen, so we test it directly).
    //   LABEL  — DialogButtonController.UpdateButtonText resolves the label from the preset's
    //            DDS msgID; a POSTFIX sets our button text directly, so we need no DDS files.
    //   ACTION — clicking routes ActionController._InvokeDialog -> DialogController.ExecuteDialog
    //            (once, for the clicked option). Our preset has no responses, so ExecuteDialog
    //            speaks nothing (it null-guards the responses list); a POSTFIX does the door swap.
    //
    // The core action is pure vanilla API: partner.ai.AnswerDoor(door, where, Player) makes the
    // partner path to the door and open it (it also shows the vanilla "Somebody is coming to
    // answer the door..." message), and currentGoal.Complete() releases the current answerer.
    internal static class TalkToPartner
    {
        internal static bool Enable = true;

        internal const string PresetName = "SODMotives_TalkToPartner";
        internal const string Label = "Can I talk to your partner?";

        private static DialogPreset _preset;

        // Set after a successful swap; consumed next frame by the InteractionController.Update postfix to
        // close the talk UI safely (closing synchronously inside the click handler risks re-entrancy).
        internal static bool PendingClose;

        private const string AnswerDoorGoal = "AnswerDoor";   // goal AnswerDoor assigns; confirms a summon took
        private const float HandoffMaxWaitSeconds = 10f;      // phase 1: summon anyway if the door never reports closed
        private const float AnsweredTimeoutSeconds = 20f;     // phase 2: close+lock if the player never engages

        // A partner-handoff in progress, tracked PER HOUSEHOLD (a list, not one global slot) so running between
        // flats and spamming the option never cross-blocks. Two phases:
        //   phase 1 (summoned=false): the original NPC was freed; we wait for them to clear/close the door, then
        //                             summon the partner -- so the two never cross at the doorway.
        //   phase 2 (summoned=true):  the partner is at the door. If the player never talks to them we time out
        //                             and close+lock, mirroring vanilla's unanswered-door behaviour.
        private sealed class Handoff
        {
            public Citizen summoner, partner;
            public NewDoor door;
            public NewGameLocation where;
            public float start;
            public bool summoned;
            public float summonedAt;
        }
        private static readonly System.Collections.Generic.List<Handoff> _handoffs = new System.Collections.Generic.List<Handoff>();

        // Who the player is currently talking to (from InteractionController.SetDialog), so a summoned partner
        // being actively talked to isn't timed out or released underneath them.
        private static Citizen _talkingTo;

        internal static bool AnyHandoffs => _handoffs.Count > 0;

        private static int Hid(Human h) { try { return h != null ? h.humanID : -99; } catch { return -99; } }

        // True if a summon is IN FLIGHT (phase 1) for this person's household -- block starting another there.
        // (A phase-2 handoff does NOT block: talking to the arrived partner and asking again is a valid switch.)
        private static bool HouseholdSummoning(Citizen a)
        {
            try
            {
                Citizen b = null; try { b = a != null ? a.partner : null; } catch { }
                int ia = Hid(a), ib = b != null ? Hid(b) : -98;
                for (int i = 0; i < _handoffs.Count; i++)
                {
                    var h = _handoffs[i]; if (h == null || h.summoned) continue;
                    int hs = Hid(h.summoner), hp = Hid(h.partner);
                    if (hs == ia || hs == ib || hp == ia || hp == ib) return true;
                }
            }
            catch { }
            return false;
        }

        private static void RemoveHandoffsInvolving(Citizen c)
        {
            try
            {
                int id = Hid(c);
                for (int i = _handoffs.Count - 1; i >= 0; i--)
                {
                    var h = _handoffs[i];
                    if (h == null || Hid(h.summoner) == id || Hid(h.partner) == id) _handoffs.RemoveAt(i);
                }
            }
            catch { }
        }

        private static void AddHandoff(Citizen summoner, Citizen partner, NewDoor door, NewGameLocation where)
        {
            try { _handoffs.Add(new Handoff { summoner = summoner, partner = partner, door = door, where = where, start = UnityEngine.Time.unscaledTime, summoned = false }); }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] AddHandoff failed: {e.Message}"); }
        }

        // Driven each frame by the InteractionController.Update postfix. Advances every handoff independently.
        internal static void Tick()
        {
            if (_handoffs.Count == 0) return;
            float now = UnityEngine.Time.unscaledTime;
            for (int i = _handoffs.Count - 1; i >= 0; i--)
            {
                Handoff h = _handoffs[i];
                if (h == null) { _handoffs.RemoveAt(i); continue; }
                try
                {
                    if (!h.summoned)
                    {
                        // Phase 1: wait for the original to clear/close the door, then summon the partner.
                        bool doorClosed = false; try { doorClosed = h.door != null && h.door.isClosed; } catch { }
                        bool timeout = now - h.start >= HandoffMaxWaitSeconds;
                        if (!doorClosed && !timeout) continue;

                        bool partnerHome = false; try { partnerHome = h.partner != null && h.partner.isHome; } catch { }
                        if (h.partner == null || h.door == null || !partnerHome)
                        {
                            MotivesPlugin.Log.LogInfo($"[SODMotives][partner] handoff dropped: {Name(h.partner)} unavailable before summon.");
                            _handoffs.RemoveAt(i); continue;
                        }
                        // Wake the partner if asleep so AnswerDoor takes (it no-ops on a sleeper); a knock wakes
                        // sleepers in vanilla too. forceImmediate = up right away and able to answer.
                        bool wasAsleep = false;
                        try { wasAsleep = h.partner.isAsleep; if (wasAsleep) h.partner.WakeUp(true); } catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] WakeUp failed: {e.Message}"); }

                        h.partner.ai.AnswerDoor(h.door, h.where, Player.Instance);
                        string pg = GoalName(h.partner);
                        bool took = string.Equals(pg, AnswerDoorGoal, StringComparison.OrdinalIgnoreCase);
                        MotivesPlugin.Log.LogInfo($"[SODMotives][partner] handoff: {Name(h.summoner)} cleared the door (doorClosed={doorClosed} timeout={timeout}); woke={wasAsleep}; summoned {Name(h.partner)}; goal={pg} took={took}.");
                        if (!took) { _handoffs.RemoveAt(i); continue; }   // couldn't summon (busy) -> drop; player can re-knock
                        h.summoned = true; h.summonedAt = now;
                    }
                    else
                    {
                        // Phase 2: partner is at the door. If the player isn't talking to THEM and enough time
                        // passes with no engagement, close+lock (like vanilla's unanswered-door timeout).
                        bool talkingToThem = _talkingTo != null && h.partner != null && Hid(_talkingTo) == Hid(h.partner);
                        if (!talkingToThem && now - h.summonedAt >= AnsweredTimeoutSeconds)
                        {
                            MotivesPlugin.Log.LogInfo($"[SODMotives][partner] {Name(h.partner)} went unanswered -> closing up (timeout).");
                            ReleaseHandoff(h, true);
                            _handoffs.RemoveAt(i);
                        }
                    }
                }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] tick failed: {e.Message}"); _handoffs.RemoveAt(i); }
            }
        }

        // Complete the summoned partner's answer-door goal (back to routine); if closeDoor, close + lock it so
        // the home is secured again (completing the goal doesn't run the NPC's own close-the-door step).
        private static void ReleaseHandoff(Handoff h, bool closeDoor)
        {
            if (h == null) return;
            Citizen p = h.partner;
            try
            {
                if (p != null && p.ai != null)
                {
                    var g = p.ai.currentGoal;
                    if (g != null && g.preset != null && string.Equals(g.preset.name, AnswerDoorGoal, StringComparison.OrdinalIgnoreCase))
                    {
                        g.Complete();
                        MotivesPlugin.Log.LogInfo($"[SODMotives][partner] released {Name(p)} from door duty -> back to routine.");
                    }
                }
            }
            catch { }
            if (!closeDoor) return;
            try
            {
                var d = h.door;
                Actor actor = p != null ? (Actor)p : null;
                if (d != null && actor != null)
                {
                    bool wasClosed = false; try { wasClosed = d.isClosed; } catch { }
                    if (!wasClosed) { try { d.SetOpen(0f, actor, false, 1f); } catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] close door failed: {e.Message}"); } }
                    try { d.SetLocked(d.GetDefaultLockState(), actor, false); } catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] lock door failed: {e.Message}"); }
                    MotivesPlugin.Log.LogInfo($"[SODMotives][partner] closed + locked the door (wasClosed={wasClosed}).");
                }
            }
            catch { }
        }

        // InteractionController.SetDialog(true, who): remember who we're talking to.
        internal static void OnDialogOpened(Interactable newTalkingTo) { try { _talkingTo = ResolveCitizen(newTalkingTo); } catch { _talkingTo = null; } }

        // InteractionController.SetDialog(false): the conversation closed. Release the summoned partner we were
        // talking to (close+lock). Switching is handled separately (the old handoff is removed when that partner
        // becomes the new summoner), so this only fires on a genuine end -- not mid-switch.
        internal static void OnDialogClosed()
        {
            Citizen who = _talkingTo;
            _talkingTo = null;
            if (who == null) return;
            try
            {
                int id = Hid(who);
                for (int i = _handoffs.Count - 1; i >= 0; i--)
                {
                    var h = _handoffs[i];
                    if (h != null && h.summoned && Hid(h.partner) == id)
                    {
                        ReleaseHandoff(h, true);
                        _handoffs.RemoveAt(i);
                    }
                }
            }
            catch { }
        }

        // Built once and reused. A plain (specialCase == none) preset with NO responses: ExecuteDialog
        // reports success and, finding no responses, speaks nothing (verified: it null-guards the list),
        // leaving all behaviour to our postfix. useSuccessTest is off (baseChance 1) because WE decide the
        // real outcome (is the partner actually home) in the action hook, not a dice roll.
        internal static DialogPreset Preset
        {
            get
            {
                if (_preset == null)
                {
                    try
                    {
                        var p = ScriptableObject.CreateInstance<DialogPreset>();
                        p.name = PresetName;
                        p.msgID = PresetName;                 // real label is supplied by the UpdateButtonText postfix (no DDS)
                        p.tiedToKey = Evidence.DataKey.voice; // conversational key the talk menu fetches face-to-face
                        p.specialCase = DialogPreset.SpecialCase.none;
                        p.useSuccessTest = false;
                        p.baseChance = 1f;
                        p.defaultOption = false;
                        p.ranking = 5;
                        p.removeAfterSaying = false;
                        p.affectChanceIfRestrained = -1f;
                        _preset = p;
                        MotivesPlugin.Log.LogInfo("[SODMotives][partner] preset created.");
                    }
                    catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] preset create failed: {e.Message}"); }
                }
                return _preset;
            }
        }

        // Visible whenever you are talking, in person, to someone who is home and not homeless. We do NOT
        // require a partner here: people who live alone still see the option and simply tell you so (that is
        // itself a useful deduction). The action decides what actually happens.
        internal static bool ShouldShow(Citizen saysTo)
        {
            try { return saysTo != null && saysTo.isHome && !saysTo.isHomeless; }
            catch { return false; }
        }

        // APPEAR: append our option to the voice-key list the talk menu is assembled from. The returned
        // list is a fresh per-call copy, so appending is transient (rebuilt every menu refresh) and never
        // mutates the witness's stored options. Deduped in case the key is fetched more than once per build.
        internal static void OnGetDialogOptions(Evidence.DataKey key,
            Il2CppSystem.Collections.Generic.List<EvidenceWitness.DialogOption> result)
        {
            if (!Enable || result == null) return;
            if (key != Evidence.DataKey.voice) return;
            DialogPreset preset = Preset;
            if (preset == null) return;
            try
            {
                for (int i = 0; i < result.Count; i++)
                {
                    var o = result[i];
                    if (o != null && o.preset != null && o.preset.name == PresetName) return;   // already present
                }
                var opt = new EvidenceWitness.DialogOption();
                opt.preset = preset;
                result.Add(opt);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] append failed: {e.Message}"); }
        }

        // ACTION: on click, summon the partner to the door and release the current NPC so they step away.
        internal static void OnExecute(EvidenceWitness.DialogOption dialog, Interactable saysTo)
        {
            if (!Enable || dialog == null) return;
            DialogPreset preset = null;
            try { preset = dialog.preset; } catch { }
            if (preset == null || preset.name != PresetName) return;

            try
            {
                Citizen cit = ResolveCitizen(saysTo);
                if (cit == null) { MotivesPlugin.Log.LogWarning("[SODMotives][partner] clicked but could not resolve the citizen."); return; }

                Citizen partner = null;
                try { partner = cit.partner; } catch { }
                bool partnerHome = false;
                try { partnerHome = partner != null && partner.isHome; } catch { }
                NewDoor door = FrontDoor(cit);

                // Diagnostic snapshot (helps pin down door/partner issues from a save's log).
                MotivesPlugin.Log.LogInfo($"[SODMotives][partner] clicked on {Name(cit)} @ {LocName(cit.home)} | isHome={SafeBool(() => cit.isHome)} entrances={EntranceCount(cit)} door={(door != null ? "ok" : "NULL")} | partner={(partner != null ? Name(partner) : "<none>")} partnerHome={partnerHome} partnerAt={(partner != null ? LocName(SafeLoc(partner)) : "-")} | curGoal={GoalName(cit)}");

                int seed = 0; try { seed = cit.humanID; } catch { }

                if (partner == null)
                {
                    SpeakLine(cit, Pick(seed, "I live alone.", "It's just me here.", "I live on my own."), false);
                    MotivesPlugin.Log.LogInfo($"[SODMotives][partner] {Name(cit)} lives alone (no partner).");
                    return;
                }
                if (!partnerHome)
                {
                    SpeakLine(cit, Pick(seed, "They're not home right now.", "My partner's out at the moment.", "They're not in, sorry."), false);
                    MotivesPlugin.Log.LogInfo($"[SODMotives][partner] {Name(cit)}'s partner {Name(partner)} is not home.");
                    return;
                }
                if (door == null)
                {
                    SpeakLine(cit, "I can't do that right now.", false);
                    MotivesPlugin.Log.LogWarning($"[SODMotives][partner] {Name(cit)} home has no usable entrance door.");
                    return;
                }

                // Guard: only block if a summon is already IN FLIGHT for THIS household (phase 1). Talking to an
                // arrived partner and asking again is a valid switch, and other homes are never cross-blocked.
                if (HouseholdSummoning(cit))
                {
                    SpeakLine(cit, Pick(seed, "Give them a moment...", "Hang on, one at a time...", "Give it a second..."), false);
                    MotivesPlugin.Log.LogInfo($"[SODMotives][partner] {Name(cit)} blocked: a summon is already in flight for this home.");
                    return;
                }

                // If the partner is asleep, that's fine: we wake them during the handoff (a knock can wake a
                // sleeper in vanilla too). Just vary the acknowledgement line. Uses Actor.isAsleep directly.
                bool partnerAsleep = false; try { partnerAsleep = partner.isAsleep; } catch { }

                // STAGGERED HANDOFF: say the acknowledgement (endsDialog closes the convo when the line ends),
                // free {cit} so they step back inside and CLOSE the door, and record a per-household handoff. We
                // do NOT summon the partner yet -- Tick() waits until the door closes, then wakes (if needed) and
                // summons them to come open it, so the two never meet at the doorway.
                string ack = partnerAsleep
                    ? Pick(seed, "Sure, I'll go wake them...", "Hold on, I'll get them up...", "One moment, I'll wake them...")
                    : Pick(seed, "Sure, wait a second...", "Sure, one moment...", "Of course, hold on...");
                bool spoke = SpeakLine(cit, ack, true);
                if (!spoke) PendingClose = true;

                // {cit} is now the summoner: drop any handoff where they were the answerer (so the pending close
                // for that role doesn't fire), then free them to step inside and close the door.
                RemoveHandoffsInvolving(cit);
                try { if (cit.ai != null && cit.ai.currentGoal != null) cit.ai.currentGoal.Complete(); } catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] currentGoal.Complete failed: {e.Message}"); }

                AddHandoff(cit, partner, door, cit.currentGameLocation);
                MotivesPlugin.Log.LogInfo($"[SODMotives][partner] {Name(cit)} stepping aside; will summon {Name(partner)} once the door closes (asleep={partnerAsleep}, spoke={spoke}).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] execute failed: {e.Message}"); }
        }

        // ---- diagnostic helpers ----
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
        private static NewGameLocation SafeLoc(Human h) { try { return h != null ? h.currentGameLocation : null; } catch { return null; } }
        private static string LocName(NewGameLocation l) { if (l == null) return "<null>"; try { return l.name; } catch { return "?"; } }
        private static int EntranceCount(Citizen c) { try { var h = c.home; if (h == null || h.entrances == null) return -1; return h.entrances.Count; } catch { return -1; } }
        private static string GoalName(Human h)
        {
            try
            {
                var g = h != null && h.ai != null ? h.ai.currentGoal : null;
                if (g == null) return "<none>";
                var p = g.preset;
                return p != null ? p.name : "<goal>";
            }
            catch { return "?"; }
        }

        private static Citizen ResolveCitizen(Interactable saysTo)
        {
            if (saysTo == null) return null;
            try
            {
                var actor = saysTo.isActor;
                if (actor == null) return null;
                return actor.TryCast<Citizen>();
            }
            catch { return null; }
        }

        private static NewDoor FrontDoor(Citizen cit)
        {
            try
            {
                var home = cit.home;
                if (home == null) return null;
                var ents = home.entrances;
                if (ents == null || ents.Count == 0) return null;
                var e0 = ents[0];
                return e0 != null ? e0.door : null;
            }
            catch { return null; }
        }

        // ---- NPC speech (bubbles with custom text) ----
        // The game rejects Speak()-ing a raw runtime string (Strings.Get validates the entry), so we do what
        // the interrogation gossip does: SPEAK using a known-valid dictionary/entry pair (cached from any real
        // bubble we have seen), which queues a real bubble, then REPLACE that bubble's text on render via our
        // SpeechBubbleController.Setup postfix. We can also flag the queued line to end the dialog afterwards.
        private static string _seedDict, _seedEntry;
        private static readonly System.Collections.Generic.Dictionary<System.IntPtr, string> _pendingText
            = new System.Collections.Generic.Dictionary<System.IntPtr, string>();

        // Runs from our SpeechBubbleController.Setup postfix: cache a valid seed pair, and inject our text
        // into a bubble we queued.
        internal static void OnBubbleSetup(SpeechBubbleController bubble)
        {
            if (bubble == null) return;
            try
            {
                var qe = bubble.speech;
                if (qe != null)
                {
                    // Cache the latest known-good (dict, entry) so we always have a valid seed to clone.
                    string d = qe.dictRef, en = qe.entryRef;
                    if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(en)) { _seedDict = d; _seedEntry = en; }

                    if (_pendingText.Count > 0)
                    {
                        System.IntPtr ptr = qe.Pointer;
                        if (ptr != System.IntPtr.Zero && _pendingText.TryGetValue(ptr, out var text))
                        {
                            _pendingText.Remove(ptr);
                            SetBubbleText(bubble, text);
                        }
                    }
                }
            }
            catch { }
        }

        // Make sure we have a valid (dictionary, entry) seed to clone, even before any bubble has rendered this
        // session (the first NPC talked to right after loading). We grab any real pair from Strings.stringTable
        // (Speak resolves it via Strings.Get, and we overwrite the text anyway, so which entry it is doesn't
        // matter -- it just has to be a pair Strings.Get accepts).
        private static void EnsureSeed()
        {
            if (!string.IsNullOrEmpty(_seedDict) && !string.IsNullOrEmpty(_seedEntry)) return;
            try
            {
                var table = Strings.stringTable;
                if (table == null || table.Count == 0) return;
                foreach (var dictName in table.Keys)
                {
                    if (string.IsNullOrEmpty(dictName)) continue;
                    var inner = table[dictName];
                    if (inner == null || inner.Count == 0) continue;
                    foreach (var key in inner.Keys)
                    {
                        if (!string.IsNullOrEmpty(key)) { _seedDict = dictName; _seedEntry = key; return; }
                    }
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] EnsureSeed failed: {e.Message}"); }
        }

        // Speak `text` as a bubble from `who`. If endsConversation, the queued line ends the dialog when it
        // finishes. Returns false if we had no valid seed pair yet (so the caller can fall back).
        private static bool SpeakLine(Citizen who, string text, bool endsConversation)
        {
            try
            {
                EnsureSeed();
                var sc = who != null ? who.speechController : null;
                if (sc == null || string.IsNullOrEmpty(_seedDict) || string.IsNullOrEmpty(_seedEntry))
                {
                    MotivesPlugin.Log.LogWarning("[SODMotives][partner] no speech seed available; could not speak a bubble.");
                    return false;
                }
                var q = sc.speechQueue;
                int before = q != null ? q.Count : -1;
                sc.Speak(_seedDict, _seedEntry, false, false, false, 0f, endsDialog: endsConversation);
                if (q != null && q.Count > before && q.Count > 0)
                {
                    var el = q[q.Count - 1];
                    var ptr = el != null ? el.Pointer : System.IntPtr.Zero;
                    if (ptr != System.IntPtr.Zero) { _pendingText[ptr] = text; return true; }
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] SpeakLine failed: {e.Message}"); }
            return false;
        }

        // Set a bubble's target text + word list and restart the typewriter reveal (same technique as the
        // interrogation gossip delivery).
        private static void SetBubbleText(SpeechBubbleController bubble, string text)
        {
            bubble.actualString = text;
            try
            {
                var parts = text.Split(' ');
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(parts.Length);
                for (int i = 0; i < parts.Length; i++) arr[i] = parts[i];
                bubble.words = arr;
            }
            catch { }
            bubble.revealedChars = 0;
            bubble.wordsRevealed = 0;
            bubble.setFinalText = false;
            try { if (bubble.text != null) bubble.text.text = ""; } catch { }
        }

        private static string Pick(int seed, params string[] variants)
            => variants[((seed % variants.Length) + variants.Length) % variants.Length];

        private static string Name(Human h) { if (h == null) return "someone"; try { return h.citizenName; } catch { return "someone"; } }
    }

    // APPEAR: append our option to the face-to-face (voice) option list.
    [HarmonyPatch(typeof(EvidenceWitness), nameof(EvidenceWitness.GetDialogOptions), new Type[] { typeof(Evidence.DataKey) })]
    internal static class Patch_TalkToPartner_Appear
    {
        static void Postfix(Evidence.DataKey key, Il2CppSystem.Collections.Generic.List<EvidenceWitness.DialogOption> __result)
            => TalkToPartner.OnGetDialogOptions(key, __result);
    }

    // GATE: show the option only when the target is home, not homeless, and has a partner. Overrides the
    // result for OUR preset only; every other preset keeps the vanilla availability the original computed.
    [HarmonyPatch(typeof(DialogController), nameof(DialogController.TestSpecialCaseAvailability))]
    internal static class Patch_TalkToPartner_Available
    {
        static void Postfix(DialogPreset preset, Citizen saysTo, ref bool __result)
        {
            if (!TalkToPartner.Enable || preset == null) return;
            try { if (preset.name == TalkToPartner.PresetName) __result = TalkToPartner.ShouldShow(saysTo); }
            catch { }
        }
    }

    // LABEL: set our button's text (no DDS). Runs after the vanilla msgID -> DDS lookup, so ours wins.
    [HarmonyPatch(typeof(DialogButtonController), nameof(DialogButtonController.UpdateButtonText))]
    internal static class Patch_TalkToPartner_Label
    {
        static void Postfix(DialogButtonController __instance)
        {
            if (!TalkToPartner.Enable || __instance == null) return;
            try
            {
                var opt = __instance.option;
                if (opt != null && opt.preset != null && opt.preset.name == TalkToPartner.PresetName && __instance.text != null)
                    __instance.text.text = TalkToPartner.Label;
            }
            catch { }
        }
    }

    // ACTION: perform the door swap when our option is clicked. ExecuteDialog is called once per click
    // for the clicked option (via ActionController._InvokeDialog); we read the option and act.
    [HarmonyPatch(typeof(DialogController), nameof(DialogController.ExecuteDialog))]
    internal static class Patch_TalkToPartner_Execute
    {
        static void Postfix(EvidenceWitness.DialogOption dialog, Interactable saysTo)
            => TalkToPartner.OnExecute(dialog, saysTo);
    }

    // Deferred, re-entrancy-safe dialog close: OnExecute sets PendingClose during the click; we act on it
    // on the next frame, once the click handler has fully unwound. Only used as a fallback when we could not
    // speak the acknowledgement bubble (whose endsDialog flag normally ends the conversation itself).
    [HarmonyPatch(typeof(InteractionController), nameof(InteractionController.Update))]
    internal static class Patch_TalkToPartner_DeferredClose
    {
        static void Postfix(InteractionController __instance)
        {
            // Deferred conversation close (fallback when we could not speak the closing bubble).
            if (TalkToPartner.PendingClose)
            {
                TalkToPartner.PendingClose = false;
                try { if (__instance != null) __instance.SetDialog(false, null); }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] deferred SetDialog(false) failed: {e.Message}"); }
            }

            // Staggered handoffs (per household): summon partners once doors close; time out unanswered ones.
            if (TalkToPartner.AnyHandoffs) TalkToPartner.Tick();
        }
    }

    // Caches a valid speech seed and injects our custom text into bubbles we queue (see TalkToPartner speech).
    [HarmonyPatch(typeof(SpeechBubbleController), nameof(SpeechBubbleController.Setup))]
    internal static class Patch_TalkToPartner_Bubble
    {
        static void Postfix(SpeechBubbleController __instance) => TalkToPartner.OnBubbleSetup(__instance);
    }

    // Track who the player is talking to, and on a genuine conversation end release the summoned partner they
    // were talking to (complete their answer-door goal -> back to routine, and close + lock the door).
    [HarmonyPatch(typeof(InteractionController), nameof(InteractionController.SetDialog))]
    internal static class Patch_TalkToPartner_DialogState
    {
        static void Postfix(bool val, Interactable newTalkingTo)
        {
            if (!TalkToPartner.Enable) return;
            if (val) TalkToPartner.OnDialogOpened(newTalkingTo);
            else TalkToPartner.OnDialogClosed();
        }
    }
}

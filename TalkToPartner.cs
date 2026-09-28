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

        // Anti-cycle: after a summon, block re-summoning in the same household for this many (real) seconds,
        // so the two partners don't overlap in transit, cross paths, and get stuck talking to each other.
        internal static float SwitchCooldownSeconds = 8f;
        private static readonly System.Collections.Generic.Dictionary<int, float> _cooldownUntil
            = new System.Collections.Generic.Dictionary<int, float>();
        // The AIGoalPreset name AnswerDoor assigns; used to confirm a summon actually took (asleep/busy
        // partners no-op it and keep their own goal, e.g. Awaken/Mourn).
        private const string AnswerDoorGoal = "AnswerDoor";

        private static bool OnCooldown(int humanID)
        {
            try { return _cooldownUntil.TryGetValue(humanID, out var until) && UnityEngine.Time.unscaledTime < until; }
            catch { return false; }
        }
        private static void StartCooldown(int a, int b)
        {
            if (SwitchCooldownSeconds <= 0f) return;
            try { float until = UnityEngine.Time.unscaledTime + SwitchCooldownSeconds; _cooldownUntil[a] = until; _cooldownUntil[b] = until; }
            catch { }
        }

        // Pending staggered handoff: after the player asks, we free the current NPC and wait for them to close
        // the door, THEN summon the partner (so the two never cross at the doorway). Consumed in TickHandoff,
        // driven by the InteractionController.Update postfix.
        private static Citizen _pendSummoner, _pendPartner;
        private static NewDoor _pendDoor;
        private static NewGameLocation _pendWhere;
        private static float _pendStart;
        private const float HandoffMaxWaitSeconds = 10f;   // fallback: summon anyway if the door never reports closed
        internal static bool HasPending => _pendPartner != null;

        // Called every frame while a handoff is pending. Summons the partner once the original NPC has closed
        // the door (door.isClosed) or a short fallback timeout elapses.
        internal static void TickHandoff()
        {
            NewDoor door = _pendDoor;
            Citizen partner = _pendPartner, summoner = _pendSummoner;
            NewGameLocation where = _pendWhere;
            try
            {
                float now = UnityEngine.Time.unscaledTime;
                bool timeout = now - _pendStart >= HandoffMaxWaitSeconds;
                bool doorClosed = false;
                try { doorClosed = door != null && door.isClosed; } catch { }
                if (!doorClosed && !timeout) return;   // keep waiting for the original to close the door

                // One-shot: clear the pending state before acting.
                _pendSummoner = null; _pendPartner = null; _pendDoor = null; _pendWhere = null;

                if (partner == null || door == null) return;
                bool partnerHome = false; try { partnerHome = partner.isHome; } catch { }
                if (!partnerHome) { MotivesPlugin.Log.LogInfo($"[SODMotives][partner] handoff aborted: {Name(partner)} left before the door closed."); return; }

                partner.ai.AnswerDoor(door, where, Player.Instance);
                string pg = GoalName(partner);
                bool took = string.Equals(pg, AnswerDoorGoal, StringComparison.OrdinalIgnoreCase);
                MotivesPlugin.Log.LogInfo($"[SODMotives][partner] handoff: {Name(summoner)} closed the door (doorClosed={doorClosed} timeout={timeout}); summoned {Name(partner)}; goal={pg} took={took}.");
            }
            catch (Exception e)
            {
                _pendSummoner = null; _pendPartner = null; _pendDoor = null; _pendWhere = null;
                MotivesPlugin.Log.LogWarning($"[SODMotives][partner] handoff tick failed: {e.Message}");
            }
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

                // Guard: a handoff is already in progress in some household, or this one is on cooldown. Don't
                // start another (rapid switching is what makes the two cross paths). Keep the conversation open.
                if (HasPending || OnCooldown(cit.humanID))
                {
                    SpeakLine(cit, Pick(seed, "Give them a moment...", "Hang on, one at a time...", "Give it a second..."), false);
                    MotivesPlugin.Log.LogInfo($"[SODMotives][partner] {Name(cit)} blocked (pending={HasPending} cooldown={OnCooldown(cit.humanID)}); not re-summoning.");
                    return;
                }

                // Don't try to fetch someone who is asleep (AnswerDoor no-ops for them). Best-effort, by goal.
                string pgoal = GoalName(partner);
                if (!string.IsNullOrEmpty(pgoal) && pgoal.IndexOf("sleep", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    SpeakLine(cit, Pick(seed, "They're asleep right now.", "They're in bed, I'm afraid.", "They're sleeping, sorry."), false);
                    MotivesPlugin.Log.LogInfo($"[SODMotives][partner] {Name(cit)}'s partner {Name(partner)} is asleep (goal={pgoal}); not summoning.");
                    return;
                }

                // STAGGERED HANDOFF (the fix for the two getting stuck at the doorway): say the acknowledgement
                // (flagged endsDialog so the conversation closes when the line finishes), free {cit} so they
                // step back inside and CLOSE the door, and record a pending summon. We do NOT call the partner
                // yet -- the InteractionController.Update tick waits until {cit} has actually closed the door,
                // THEN summons the partner to come open it, so the two never meet at the doorway.
                bool spoke = SpeakLine(cit, Pick(seed, "Sure, wait a second...", "Sure, one moment...", "Of course, hold on..."), true);
                if (!spoke) PendingClose = true;
                try { if (cit.ai != null && cit.ai.currentGoal != null) cit.ai.currentGoal.Complete(); } catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][partner] currentGoal.Complete failed: {e.Message}"); }

                _pendSummoner = cit; _pendPartner = partner; _pendDoor = door;
                _pendWhere = cit.currentGameLocation; _pendStart = UnityEngine.Time.unscaledTime;
                try { StartCooldown(cit.humanID, partner.humanID); } catch { }

                MotivesPlugin.Log.LogInfo($"[SODMotives][partner] {Name(cit)} stepping aside; will summon {Name(partner)} once the door closes (spoke={spoke}).");
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

        // Speak `text` as a bubble from `who`. If endsConversation, the queued line ends the dialog when it
        // finishes. Returns false if we had no valid seed pair yet (so the caller can fall back).
        private static bool SpeakLine(Citizen who, string text, bool endsConversation)
        {
            try
            {
                var sc = who != null ? who.speechController : null;
                if (sc == null || string.IsNullOrEmpty(_seedDict) || string.IsNullOrEmpty(_seedEntry))
                {
                    MotivesPlugin.Log.LogWarning("[SODMotives][partner] no speech seed cached yet; could not speak a bubble.");
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

            // Staggered handoff: summon the partner once the original has closed the door.
            if (TalkToPartner.HasPending) TalkToPartner.TickHandoff();
        }
    }

    // Caches a valid speech seed and injects our custom text into bubbles we queue (see TalkToPartner speech).
    [HarmonyPatch(typeof(SpeechBubbleController), nameof(SpeechBubbleController.Setup))]
    internal static class Patch_TalkToPartner_Bubble
    {
        static void Postfix(SpeechBubbleController __instance) => TalkToPartner.OnBubbleSetup(__instance);
    }
}

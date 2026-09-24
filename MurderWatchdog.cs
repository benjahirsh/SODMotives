using System;
using System.Collections.Generic;
using UnityEngine;

namespace SODMotives
{
    // Recovery for the rare case where one of OUR overridden motive-murders soft-locks in the
    // 'executing' state: the killer reaches the victim but never lands a lethal blow. Observed in
    // testing as the killer swinging/whiffing with no damage (razor blade held wrong, "punching"
    // rather than stabbing), the victim fleeing room to room, indefinitely — the murder state
    // machine has no 'failed' state, so it never resolves on its own (confirmed stuck ~5 in-game
    // days). Root cause is a vanilla enactment weakness that our pair-forcing exposes (we bypass
    // vanilla's own capability-weighted killer selection); we can't repair vanilla's enact, so we
    // recover from it.
    //
    // SCOPE — deliberately narrow, so we NEVER fabricate an incoherent murder:
    //   * ONLY our overridden cases (MurderSelector.OverriddenVictimIds) — vanilla murders never
    //     enter this code (they aren't in that set). We add NO Harmony patches to vanilla's
    //     enactment / combat / selection; this is a poll that reads state and, only when safe,
    //     lands the blow the AI failed to.
    //   * ONLY when the killer is CO-LOCATED with the victim (same game-location / room) — so the
    //     forensic trail is real: the killer genuinely travelled there (CCTV en route, proximity,
    //     the alibi break all already happened) and landing the blow drops spatter at a scene the
    //     killer is standing in. If the killer can't reach the victim (a DIFFERENT stall), we do
    //     nothing but LOG it — never an evidence-less body across town.
    //   * ONLY when the victim's health isn't dropping (a genuine whiff-stall, not a slow-but-working
    //     kill we'd be cutting short).
    // On intervention we land the killing blow via Actor.RecieveDamage(enableKill:true) so vanilla's
    // own death processing runs, then force the case to 'post' (which triggers vanilla's evidence
    // spawn + our motive-clue injection) if it doesn't advance itself. EVERY intervention is logged.
    internal static class MurderWatchdog
    {
        internal static bool Enable = true;
        // In-game hours (SessionData.gameTime units) stuck in 'executing' before we step in. The
        // co-location + full-health gates already guarantee it's a true stall, so this is just a
        // generous margin so a legitimately slow enact (which completes in game-minutes) is never cut.
        internal static float StallGameHours = 24f;
        // In-game hours stuck in 'waitForLocation' before we cancel an overridden case. Unlike 'executing',
        // waitForLocation has NO natural resolution when the game can't seat a location (a kidnap with no
        // valid holding den, a sniper with no vantage site), so such a case hangs forever. We cancel it so
        // the player is never soft-locked. Kept modest since the stall is permanent once it happens.
        internal static float WaitLocationStallHours = 12f;

        private static int _watchVictimId = -1;        // the overridden victim currently in 'executing'
        private static float _executingSince = 0f;     // gameTime it entered 'executing'
        private static readonly HashSet<int> _intervened = new HashSet<int>();     // victims we've already force-finished (once-guard)
        private static readonly HashSet<int> _loggedNoReach = new HashSet<int>();  // stalls we logged but chose not to act on (once each)
        private static int _awaitingPost = -1;         // a victim we've killed, waiting to flip the case to 'post'

        private static int _waitLocVictimId = -1;      // the overridden victim currently in 'waitForLocation'
        private static float _waitLocSince = 0f;       // gameTime it entered 'waitForLocation'
        private static readonly HashSet<int> _probed = new HashSet<int>();         // cases we've run the location probe on (once each)
        private static readonly HashSet<int> _waitRecovered = new HashSet<int>();  // cases we've cancelled out of a waitForLocation hang
        private static readonly HashSet<int> _observed = new HashSet<int>();       // kidnaps (any, incl. vanilla) we've logged an observation for

        // Live position sampler (diagnostic, dev-only): throttled per-victim while a kidnap is active.
        private static int _liveVid = -1;               // victim currently being sampled
        private static float _liveLastLogH = -999f;     // gameTime (hours) of the last live sample
        private static int _liveCount = 0;              // samples logged for this victim (capped so it can't spam forever)

        // Sniper diagnostic sampler (dev-only): observe a sniper case (forced or VANILLA) to learn the intended
        // vantage / target-site / shot flow, and to diagnose why a motivated sniper loops in travellingTo instead
        // of firing. Separate throttle so it doesn't fight the kidnap sampler. All gated behind EnableDebugKeys.
        private static readonly HashSet<int> _sniperObserved = new HashSet<int>();  // sniper cases we've logged the one-shot observation for
        private static int _sniperLiveVid = -1;
        private static float _sniperLiveLastLogH = -999f;
        private static int _sniperLiveCount = 0;

        // Seal the den behind the fleeing killer. Vanilla (confirmed in-game across 2 sandboxes: frontDoors
        // locked=0 the whole hold, AND a web-search confirmed vacant-address kidnap dens stay UNLOCKED) only
        // CLOSES the doors (default NPC flee behaviour), never locks the unowned den. So we CLOSE to match that
        // reliably (our swapped killer sometimes left them open) and DON'T lock — default OFF. The optional lock
        // (a "vanilla+" sealed hold) is left as a toggle but off by default, matching vanilla.
        internal static bool KidnapLockDen = false;
        private static readonly HashSet<int> _denSealed = new HashSet<int>();
        private static readonly HashSet<int> _denGoalLogged = new HashSet<int>();  // victims we've logged rebuilding the GoTo-den goal for (save/load fix, once each)

        // Kidnap RANSOM-LEAD assist. Our victim-swap bypasses the vanilla kidnap Case/objective chain, so the
        // ransom note is never delivered + no "Examine the ransom note found at <home>" objective ever appears
        // (that objective IS the "investigate the missing person's home" lead). We spawn the note ourselves and
        // register it so the game's own objective chain proceeds (see SpawnRansomNote).
        internal static bool KidnapRansomAssist = true;
        private static readonly HashSet<int> _ransomTried = new HashSet<int>();  // victims we've spawned the ransom note for (once each)

        // Kidnap KILL-DEADLINE fix. Our victim-swap leaves the kidnap's kill timers expired, so the game flips
        // kidnapKillPhase on immediately and tries to kill the held victim before any fair deadline. We keep the
        // kill gated on the game's OWN Murder.killTime (the value the ransom note counts down to), clearing the
        // premature kidnapKillPhase and blocking the kill's state transitions until then. See ShouldBlockKidnapKill.
        private static readonly HashSet<int> _killTimeReasserted = new HashSet<int>();  // victims where we've logged holding the kidnapper off
        private static readonly HashSet<int> _killBlockLogged = new HashSet<int>();  // victims where we've logged blocking the premature kill
        internal static readonly HashSet<int> KidnapReachedHold = new HashSet<int>();  // our kidnap victims whose case has gone to the hold (post/escaping/unsolved) — a later travellingTo/executing is the KILL

        internal static void ResetForNewGame()
        {
            _watchVictimId = -1; _executingSince = 0f;
            _intervened.Clear(); _loggedNoReach.Clear(); _awaitingPost = -1;
            _waitLocVictimId = -1; _waitLocSince = 0f; _probed.Clear(); _waitRecovered.Clear();
            _observed.Clear();
            _liveVid = -1; _liveLastLogH = -999f; _liveCount = 0;
            _sniperObserved.Clear(); _sniperLiveVid = -1; _sniperLiveLastLogH = -999f; _sniperLiveCount = 0;
            _ransomTried.Clear(); _killTimeReasserted.Clear();
            _killBlockLogged.Clear(); KidnapReachedHold.Clear(); _denSealed.Clear(); _denGoalLogged.Clear();
        }

        // BLOCK THE PREMATURE KILL. Decoded (ISIL) + confirmed in-game: our victim-swap leaves the kidnap's kill
        // timers expired, so the game enters the KILL sequence (kidnapKillPhase set true, then SetMurderState
        // travellingTo -> executing to have the killer travel to the victim and deliver the lethal blow) almost
        // immediately when the case goes live — long before any fair deadline (pinning killTime did NOT stop it;
        // the kill fires via other expired timers). Rather than chase each timer, we block the kill's state
        // transitions while kidnapKillPhase is active AND we're before our deadline; the victim stays held +
        // rescuable until then. Once our deadline passes we stop blocking, so a real (missable) deadline remains.
        // Called from the SetMurderState PREFIX. Returns true = block this transition.
        internal static bool ShouldBlockKidnapKill(MurderController.Murder m, MurderController.MurderState newState)
        {
            try
            {
                if (newState != MurderController.MurderState.travellingTo && newState != MurderController.MurderState.executing) return false;
                if (m == null || m.preset == null || m.preset.caseType != MurderPreset.CaseType.kidnap) return false;
                var v = m.victim; if (v == null) return false;
                int vid = v.humanID;
                if (!MurderSelector.OverriddenVictimIds.Contains(vid)) return false;
                // Only the KILL re-enters travellingTo/executing AFTER the case has gone to the hold; the ABDUCTION's
                // own travellingTo/executing happen before that (KidnapReachedHold not yet set) and must run.
                if (!KidnapReachedHold.Contains(vid)) return false;
                // Deadline = the game's OWN killTime (what the ransom note shows), so the kill matches the note.
                // Block until it's reached; if killTime isn't set yet (~0), keep blocking so the game can't kill
                // before the deadline is even established.
                float kt = 0f; try { kt = m.killTime; } catch { }
                if (kt > 0.5f && NowHours() >= kt) return false;   // deadline reached — let the kidnapper kill
                if (_killBlockLogged.Add(vid))
                    MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap-kill] BLOCKING the kidnapper's kill (state=>{newState}) until the game's killTime={kt:0.0} (now={NowHours():0.0}); victim held + rescuable until then.");
                return true;
            }
            catch { return false; }
        }

        // WALK-reachable = the victim can reach a node INSIDE the den by NORMAL pathing (allowTrespass=FALSE),
        // i.e. without walking through a locked/forbidden door. This is the property that distinguishes a vanilla
        // den (a walk-in vacant unit — the victim's "GoTo den routine" walk goal completes, VICTIM_AT_DEN=True)
        // from a locked basement (the walk stalls on the street ~89m out and the case loops). Read-only; never
        // throws. This is the pre-condition the den picker enforces so the kidnap walks natively.
        internal static bool KidnapDenWalkReachable(Human victim, NewAddress den)
        {
            try
            {
                if (victim == null || den == null) return false;
                NewNode wn = null; try { wn = victim.FindSafeTeleport(den, false, false); } catch { return false; }
                if (wn == null) return false;
                NewGameLocation wl = null; try { wl = wn.gameLocation; } catch { }
                return wl != null && wl.Pointer == den.Pointer;
            }
            catch { return false; }
        }

        // Called from Patch_MurderStateInject (our existing SetMurderState postfix, already gated to
        // overridden victims) on every state transition of one of our cases.
        internal static void OnState(int victimId, MurderController.MurderState newState)
        {
            if (newState == MurderController.MurderState.executing)
            {
                _watchVictimId = victimId;
                _executingSince = NowHours();
            }
            else if (victimId == _watchVictimId)
            {
                _watchVictimId = -1;   // left 'executing' (post / escaping / etc.) — stop timing it
            }
        }

        private static float NowHours()
        {
            try { var s = SessionData.Instance; if (s != null) return s.gameTime; } catch { }
            return 0f;
        }

        // Polled each frame from DebugTools' Update. Cheap: early-outs unless one of our cases is
        // actually sitting in 'executing'.
        internal static void Tick()
        {
            if (!Enable) return;
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) return;
                var murder = mc.GetCurrentMurder();
                if (murder == null) return;
                var victim = murder.victim; var killer = murder.murderer;
                if (victim == null || killer == null) return;
                int vid = victim.humanID;

                // KIDNAP DIAGNOSTICS (dev-only, gated behind [Debug] EnableDebugKeys): the one-shot observer +
                // the live position sampler both log for ANY kidnap incl. VANILLA, to compare vanilla vs ours
                // side by side, and the observer flips on the game's own verbose murder narration. Off by
                // default so a shipped build keeps a clean log; flip EnableDebugKeys on for a bug report.
                if (DebugTools.EnableDebugKeys)
                {
                    // OBSERVER (once per kidnap): the pair's relationship, the killer's den, the meet state.
                    try
                    {
                        if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap && _observed.Add(vid))
                        {
                            DebugTools.EnableGameVerboseLogging();
                            LogKidnapObservation(murder, killer, victim);
                            LogKidnapReloadDiag(murder, killer, victim);   // post-load state dump (diff vanilla vs ours to find what our setup fails to restore)
                        }
                    }
                    catch { }

                    // LIVE SAMPLER (throttled + capped): whether the victim/killer are actually at the den right
                    // now, and how far the victim is from it — so you can watch the meet->den walk seat.
                    try
                    {
                        bool kidnapActive = murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap
                            && (murder.state == MurderController.MurderState.waitForLocation
                                || murder.state == MurderController.MurderState.travellingTo
                                || murder.state == MurderController.MurderState.executing
                                || murder.state == MurderController.MurderState.post
                                || murder.state == MurderController.MurderState.escaping
                                || murder.state == MurderController.MurderState.unsolved);
                        if (kidnapActive)
                        {
                            float now = NowHours();
                            if (_liveVid != vid) { _liveVid = vid; _liveLastLogH = -999f; _liveCount = 0; }
                            if (_liveCount < 120 && now - _liveLastLogH >= 0.1f)
                            {
                                _liveLastLogH = now; _liveCount++;
                                LogKidnapLive(murder, killer, victim);
                            }
                        }
                    }
                    catch { }

                    // SNIPER OBSERVER (once) + LIVE SAMPLER — sniper is target-site-first: the game picks a
                    // sniperVictimSite (an exposed routine/public spot the victim visits), finds a vantage wall,
                    // then the killer travels there + shoots. Capture that flow (esp. on a VANILLA case, to learn
                    // the intended behaviour) plus whether a vantage exists for the killer+site — the leading
                    // suspect for why a motivated sniper loops in travellingTo instead of firing.
                    try
                    {
                        if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.sniper)
                        {
                            if (_sniperObserved.Add(vid))
                            {
                                DebugTools.EnableGameVerboseLogging();
                                LogSniperObservation(murder, killer, victim);
                            }
                            bool sniperActive = murder.state == MurderController.MurderState.waitForLocation
                                || murder.state == MurderController.MurderState.travellingTo
                                || murder.state == MurderController.MurderState.executing
                                || murder.state == MurderController.MurderState.post
                                || murder.state == MurderController.MurderState.escaping
                                || murder.state == MurderController.MurderState.unsolved;
                            if (sniperActive)
                            {
                                float snow = NowHours();
                                if (_sniperLiveVid != vid) { _sniperLiveVid = vid; _sniperLiveLastLogH = -999f; _sniperLiveCount = 0; }
                                if (_sniperLiveCount < 200 && snow - _sniperLiveLastLogH >= 0.05f)
                                {
                                    _sniperLiveLastLogH = snow; _sniperLiveCount++;
                                    LogSniperLive(murder, killer, victim);
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (!MurderSelector.OverriddenVictimIds.Contains(vid)) return;   // INTERVENTIONS below are OURS only — vanilla cases just get observed above

                // KILL DEADLINE (our kidnaps only) = the game's OWN Murder.killTime — the value the ransom note
                // counts down to. We deliberately do NOT override killTime, so the note's stated kill time and the
                // actual kill MATCH. The problem our swap creates is only that the game flips kidnapKillPhase on
                // immediately (which suppresses the ransom-demand setup) and tries to kill BEFORE killTime is even
                // reached. So we (a) clear kidnapKillPhase until the deadline (so the ransom objectives get built),
                // and (b) block the kill's state transitions until then (ShouldBlockKidnapKill, same killTime gate).
                if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap)
                {
                    try
                    {
                        float kt = murder.killTime; float now2 = NowHours();
                        bool deadlineReached = kt > 0.5f && now2 >= kt;   // killTime set AND reached
                        if (!deadlineReached)
                        {
                            try { if (murder.kidnapKillPhase) murder.kidnapKillPhase = false; } catch { }
                            if (kt > 0.5f && _killTimeReasserted.Add(vid))
                                MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap-kill] holding the kidnapper off until the game's own killTime={kt:0.0} (now={now2:0.0}) so the kill matches the ransom note; victim held/rescuable until then.");
                        }
                    }
                    catch (Exception ke) { MotivesPlugin.Log.LogWarning($"[SODMotives][kidnap-kill] error: {ke.Message}"); }
                }

                // SAVE/LOAD FIX (B): keep the victim's GoTo-den goal alive during the PRE-RESTRAIN phases
                // (waitForLocation + travellingTo) so a reload can't strand them. DECODED (2026-09-24, side-by-side
                // reload logs): a VANILLA kidnap victim keeps a GoTo@den goal (pri10) through these phases and it
                // survives save/load; OUR swapped victim loses it on reload (currentGoal=null, all routine goals
                // pri0), so nothing pins them -> they wander out of the den -> the seat condition breaks -> the case
                // reverts to waitForLocation. Rebuild + pin the goal so the victim commits to the den like vanilla
                // (a WALK, not a teleport). Stops at 'executing' (the abduction then injects Flee@den, which pins them).
                if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap
                    && (murder.state == MurderController.MurderState.waitForLocation || murder.state == MurderController.MurderState.travellingTo))
                {
                    NewAddress vgDen = null; try { vgDen = killer.den; } catch { }
                    if (vgDen != null) EnsureVictimDenGoal(victim, vgDen, murder, vid);
                }

                // waitForLocation stall (a kidnap with no seatable holding 'den', a sniper with no vantage
                // site): this state has no natural resolution, so the case would hang forever. The victim WALKS
                // to the den via the game's own "GoTo den routine" walk goal, leaving the real meet->den trail
                // like vanilla (the picker hands us a walk-reachable den, so the walk seats on its own:
                // waitForLocation -> travellingTo). We don't cancel while it's still walking; a genuine stall
                // past WaitLocationStallHours falls through and cancels the case -> vanilla so the player is
                // never soft-locked. A one-time IsValidLocation probe is logged (dev-only) when it stalls.
                if (murder.state == MurderController.MurderState.waitForLocation)
                {
                    if (DebugTools.EnableDebugKeys && _probed.Add(vid)) ProbeValidLocations(murder, killer, victim);

                    if (_waitLocVictimId != vid) { _waitLocVictimId = vid; _waitLocSince = NowHours(); return; }
                    if (_waitRecovered.Contains(vid)) return;
                    float wstuck = NowHours() - _waitLocSince;
                    if (wstuck < WaitLocationStallHours) return;
                    if (DebugTools.EnableDebugKeys) LogKidnapState(murder, killer, victim, "stall");   // final state before we give up
                    try { murder.CancelCurrentMurder(); } catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] waitForLocation cancel error: {ce.Message}"); }
                    _waitRecovered.Add(vid);
                    MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] RECOVERED: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} stuck in 'waitForLocation' {wstuck:F1}h — the victim never WALKED into the den in time (den not walk-reachable, or the victim wouldn't attend the meet / path to the den) — cancelled so it can't hang.");
                    return;
                }

                // travellingTo (KILLER -> den): the killer WALKS to the den to carry out the abduction (vanilla).
                // Nothing to do — the game's own goal carries them there.
                if (murder.state == MurderController.MurderState.travellingTo
                    && murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap)
                    return;

                // RANSOM-NOTE spawn (fix): vanilla spawns a ransom note (a preset lead item tagged JobTag.U) at
                // the victim's home and registers it in murder.activeMurderItems[U]; TriggerKidnappingCase then
                // adds the "Examine the ransom note found at <home>" objective (the investigate-the-home lead),
                // and examining it drives the rest of the chain (-> TriggerRansomDelivery -> call/save
                // objectives). Our swapped/rushed case never spawns that note, so no examine-note objective ever
                // appears. Spawn it ourselves ONCE when the case goes live, register it, and let the game's own
                // objective chain proceed — do NOT pre-call TriggerRansomDelivery (that jumps ahead + creates the
                // downstream objectives out of order, which is what happened before).
                // TIMING: the game runs TriggerKidnappingCase at the 'escaping' transition and checks for the note
                // THEN, so it must already exist — spawn at the earlier 'post' (right after "knocked out and
                // restrained"), not at escaping/unsolved (a hair too late, so the objective was only created at the
                // KILL's later post). _ransomTried guards to once → only the abduction's post spawns it.
                if ((murder.state == MurderController.MurderState.post || murder.state == MurderController.MurderState.escaping || murder.state == MurderController.MurderState.unsolved)
                    && murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap)
                {
                    // Re-arm the hold marker every tick while held. It is normally set by the SetMurderState
                    // postfix on the transition INTO the hold, but that transition does NOT re-fire after a
                    // save/reload (the case loads already at post/escaping/unsolved), and the marker is in-memory
                    // and cleared on every game start (INCLUDING a load). Without this, the kill-block
                    // (ShouldBlockKidnapKill) never re-arms after a reload and a held victim could be killed
                    // before the ransom deadline. Setting it here (only in the hold states) restores the block on
                    // the first tick after load, and never mislabels the abduction's own pre-hold
                    // travellingTo/executing (those states do not enter this branch).
                    KidnapReachedHold.Add(vid);
                    if (KidnapRansomAssist && _ransomTried.Add(vid)) SpawnRansomNote(murder, killer, victim);
                    // Seal the den once the abduction is done (post+) AND the killer has physically LEFT it: CLOSE
                    // the front door(s) behind them (matches vanilla's default flee behaviour reliably), and LOCK
                    // only if KidnapLockDen (optional, beyond vanilla). Once per victim.
                    if (!_denSealed.Contains(vid))
                    {
                        NewAddress dl = null; try { dl = killer.den; } catch { }
                        bool killerGone = true;
                        try { var kl = killer.currentGameLocation; killerGone = !(kl != null && dl != null && kl.Pointer == dl.Pointer); } catch { }
                        if (dl != null && killerGone) SealDen(killer, dl, vid);
                    }
                    return;
                }

                // Phase B: we've force-killed the victim; flip the case to 'post' if it didn't self-advance.
                if (_awaitingPost == vid)
                {
                    if (murder.state != MurderController.MurderState.executing) { _awaitingPost = -1; return; }   // advanced on its own
                    if (IsDead(victim))
                    {
                        try { murder.SetMurderState(MurderController.MurderState.post, true); } catch { }
                        MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] forced 'post' after force-finishing {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} — vanilla evidence + motive clues now spawn.");
                        _awaitingPost = -1;
                    }
                    return;
                }

                if (murder.state != MurderController.MurderState.executing) return;
                // A KIDNAP in 'executing' resolves to knock-out + RESTRAINT, not death — never force-KILL a kidnap
                // victim (that would turn an abduction into a murder). The killer walked here on their own
                // (walk-native), so just let the game's own abduction run.
                if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap)
                    return;
                if (_intervened.Contains(vid)) return;              // already handled this victim
                if (_watchVictimId != vid) { _watchVictimId = vid; _executingSince = NowHours(); return; }   // missed the entry — start timing now

                float stuckH = NowHours() - _executingSince;
                if (stuckH < StallGameHours) return;               // not stuck long enough yet

                // Genuine whiff-stall only: the victim's health must be essentially untouched. If damage
                // IS landing, the kill is progressing (just slowly) — leave it well alone.
                float hp = 1f; try { hp = victim.currentHealthNormalized; } catch { }
                if (hp < 0.9f) return;

                // CO-LOCATION GATE: the killer must be AT the victim, else force-killing would leave a
                // body with no coherent attacker/scene/spatter. If they can't reach each other, this is a
                // different stall — log once and leave it entirely to vanilla.
                if (!SameLocation(killer, victim))
                {
                    if (_loggedNoReach.Add(vid))
                        MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] STALL, NO INTERVENTION: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} stuck in 'executing' {stuckH:F1}h but the killer is NOT co-located ({LocName(killer)} vs {LocName(victim)}) — leaving to vanilla to avoid an evidence-less death.");
                    return;
                }

                // Land the killing blow the enactment failed to. Credits the killer + drops spatter at the
                // scene; vanilla death processing then runs, and the forced 'post' (Phase B) spawns the
                // usual evidence + our motive clues.
                Vector3 pos = Vector3.zero, dir = Vector3.zero;
                try { var n = victim.currentNode; if (n != null) pos = n.position; } catch { }
                try
                {
                    victim.RecieveDamage(9999f, killer, pos, dir, null, null, enableKill: true, forceRagdoll: true);
                }
                catch (Exception de)
                {
                    MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] force-finish RecieveDamage error: {de.Message}");
                }
                _intervened.Add(vid);
                _awaitingPost = vid;
                MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] INTERVENED: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} stalled in 'executing' {stuckH:F1}h (co-located at {LocName(victim)}, victim HP {hp:P0}); force-finished the kill.");
            }
            catch { }
        }

        // DIAGNOSTIC (dev-only): when an overridden case parks in waitForLocation, ask the game's OWN
        // Murder.IsValidLocation which places it would accept. Probes the killer/victim homes explicitly
        // (are they cohabiting? is a home a valid den?) and scans every city location to count how many
        // qualify right now. This reveals the real den predicate empirically — far more reliable than
        // decoding the 1300-call Update() by hand — and tells us how to constrain the kidnap victim pool.
        private static void ProbeValidLocations(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                NewGameLocation kh = null, vh = null;
                try { kh = killer.home; } catch { }
                try { vh = victim.home; } catch { }
                bool cohab = false; try { cohab = kh != null && vh != null && kh.Pointer == vh.Pointer; } catch { }
                string mo = "?"; try { if (murder.mo != null) mo = murder.mo.name; } catch { }
                string preset = "?"; try { if (murder.preset != null) preset = murder.preset.name; } catch { }
                log.LogInfo($"[SODMotives][den probe] {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)}  preset={preset} mo={mo}");
                log.LogInfo($"[SODMotives][den probe]   killer.home={LName(kh)} valid={ValidLoc(murder, kh)} ; victim.home={LName(vh)} valid={ValidLoc(murder, vh)} ; cohabiting={cohab}");

                var cd = CityData.Instance;
                var dir = cd != null ? cd.gameLocationDirectory : null;
                int total = 0, valid = 0; var sample = new List<string>();
                if (dir != null)
                    for (int i = 0; i < dir.Count; i++)
                    {
                        var loc = dir[i]; if (loc == null) continue;
                        total++;
                        bool ok = false; try { ok = murder.IsValidLocation(loc); } catch { }
                        if (ok) { valid++; if (sample.Count < 10) sample.Add(LName(loc)); }
                    }
                log.LogInfo($"[SODMotives][den probe]   IsValidLocation over {total} city locations -> {valid} VALID. first valid: {string.Join(" | ", sample)}");
                LogKidnapState(murder, killer, victim, "entry");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][den probe] error: {e.Message}"); }
        }

        // KIDNAP OBSERVER (dev-only): one-shot dump comparing a VANILLA kidnap to a MOD-FORCED one. The big
        // unknowns are (a) the killer<->victim RELATIONSHIP, (b) the DEN (killer's own home/property vs an
        // injected vacant unit), and (c) whether the victim attends the meet. Logs (a)+(b) here; (c) comes from
        // the game's own narration + the meet-state line. Read-only.
        private static void LogKidnapObservation(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID);
                log.LogInfo($"[SODMotives][kidnap-obs] ===== {(ours ? "MOD-FORCED" : "VANILLA")} KIDNAP: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} =====");
                log.LogInfo($"[SODMotives][kidnap-obs]   relationship killer->victim: {DescribeEdge(killer, victim)}");
                log.LogInfo($"[SODMotives][kidnap-obs]   relationship victim->killer: {DescribeEdge(victim, killer)}");
                NewAddress kden = null, khome = null, vhome = null;
                try { kden = killer.den; } catch { }
                try { khome = killer.home; } catch { }
                try { vhome = victim.home; } catch { }
                bool denIsKillerHome = false, denIsVictimHome = false;
                try { denIsKillerHome = kden != null && khome != null && kden.Pointer == khome.Pointer; } catch { }
                try { denIsVictimHome = kden != null && vhome != null && kden.Pointer == vhome.Pointer; } catch { }
                log.LogInfo($"[SODMotives][kidnap-obs]   killer.den={LName(kden)}  (killer.home={LName(khome)} denIsKillerHome={denIsKillerHome};  victim.home={LName(vhome)} denIsVictimHome={denIsVictimHome})");
                LogKidnapState(murder, killer, victim, "obs");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][kidnap-obs] error: {e.Message}"); }
        }

        // KIDNAP RELOAD DIAG (dev-only): dump the RESTORED state of a kidnap right after it's (re)observed —
        // fires once per game start incl. every load, so a save/reload captures exactly what the game rebuilt.
        // The executing-reload bug is OURS (vanilla keeps killer+victim at the den; ours sends both home), so we
        // diff this line between a VANILLA and an OURS mid-executing reload to find what our swapped setup fails
        // to restore. Prime suspect: murderGoal (the killer's abduction AI goal) + the actors' current AI goals.
        private static void LogKidnapReloadDiag(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = false; try { ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID); } catch { }
                string st = "?"; try { st = murder.state.ToString(); } catch { }
                string mg = "?"; try { mg = murder.murderGoal != null ? DescribeGoal(murder.murderGoal) : "NULL"; } catch { mg = "err"; }
                string g1 = "?", g2 = "?";
                try { g1 = murder.meetGoal1 != null ? "set" : "null"; } catch { }
                try { g2 = murder.meetGoal2 != null ? "set" : "null"; } catch { }
                string ids = "?";
                try { ids = $"victimID={murder.victimID} victimSiteID={murder.victimSiteID} victimSiteIsStreet={murder.victimSiteIsStreet} kidnapKillPhase={murder.kidnapKillPhase} killTime={murder.killTime:0.0} ransomPhase={murder.ransomPhase}"; } catch { ids = "err"; }
                log.LogInfo($"[SODMotives][kidnap-reload] {(ours ? "OURS" : "VANILLA")} state={st} murderGoal={mg} meetGoal1={g1} meetGoal2={g2} ; {ids}");
                log.LogInfo($"[SODMotives][kidnap-reload]   killer.ai: {DescribeActorGoals(killer)}");
                log.LogInfo($"[SODMotives][kidnap-reload]   victim.ai: {DescribeActorGoals(victim)}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][kidnap-reload] err: {e.Message}"); }
        }

        // Compact one-goal description: preset name @ target location (priority). Never throws.
        private static string DescribeGoal(NewAIGoal g)
        {
            try
            {
                if (g == null) return "null";
                string p = "?"; try { p = g.preset != null ? g.preset.name : "?"; } catch { }
                NewGameLocation gl = null; try { gl = g.gameLocation; } catch { }
                if (gl == null) { try { gl = g.passedGameLocation; } catch { } }
                float pr = 0f; try { pr = g.priority; } catch { }
                return $"{p}@{LName(gl)}(pri{pr:0})";
            }
            catch { return "err"; }
        }

        // Dump an actor's current AI goal + its goal list (compact) so we can see whether the abduction goals
        // (den-targeting) survived the reload, or whether the actor reverted to routine goals (home/needs).
        private static string DescribeActorGoals(Human h)
        {
            try
            {
                NewAIController ai = null; try { ai = h.ai; } catch { }
                if (ai == null) return "no ai";
                string cur = "?"; try { cur = ai.currentGoal != null ? DescribeGoal(ai.currentGoal) : "null"; } catch { }
                var parts = new List<string>();
                try { var goals = ai.goals; if (goals != null) for (int i = 0; i < goals.Count && i < 10; i++) { var g = goals[i]; if (g != null) parts.Add(DescribeGoal(g)); } } catch { }
                return $"currentGoal={cur} ; goals=[{string.Join(", ", parts.ToArray())}]";
            }
            catch { return "err"; }
        }

        // Describe the directed acquaintance edge a->b: connection types + like + known (or "NO EDGE").
        private static string DescribeEdge(Human a, Human b)
        {
            try
            {
                if (a == null || b == null) return "<null>";
                Acquaintance acq;
                if (!a.FindAcquaintanceExists(b, out acq) || acq == null) return "NO EDGE (strangers)";
                var parts = new List<string>();
                try { var conns = acq.connections; if (conns != null) for (int i = 0; i < conns.Count; i++) parts.Add(conns[i].ToString()); } catch { }
                string connStr = parts.Count > 0 ? string.Join(",", parts.ToArray()) : "(no connection types)";
                float like = float.NaN, known = float.NaN;
                try { like = acq.like; } catch { }
                try { known = acq.known; } catch { }
                return $"[{connStr}] like={like:0.00} known={known:0.00}";
            }
            catch { return "<err>"; }
        }

        // Kidnap abduction state — was the MEET set up (the thing that brings killer+victim together so the
        // killer can knock out + restrain + carry the victim to the den)? And where is the pair? This tells us
        // WHERE in the flow a stuck kidnap is stalling. Cheap; safe to call more than once.
        private static void LogKidnapState(MurderController.Murder murder, Human killer, Human victim, string tag)
        {
            var log = MotivesPlugin.Log;
            try
            {
                string mr = "<null>"; int mrid = -1;
                try { if (murder.meetRestaurant != null) mr = murder.meetRestaurant.name; } catch { }
                try { mrid = murder.meetRestaurantID; } catch { }
                bool g1 = false, g2 = false;
                try { g1 = murder.meetGoal1 != null; } catch { }
                try { g2 = murder.meetGoal2 != null; } catch { }
                string loc = "<null>"; try { if (murder.location != null) loc = murder.location.name; } catch { }
                string kloc = "?", vloc = "?";
                try { if (killer.currentGameLocation != null) kloc = killer.currentGameLocation.name; } catch { }
                try { if (victim.currentGameLocation != null) vloc = victim.currentGameLocation.name; } catch { }
                NewAddress den = null; try { den = killer.den; } catch { }
                log.LogInfo($"[SODMotives][kidnap {tag}]   meet: restaurant={mr}(id={mrid}) goal1set={g1} goal2set={g2}; location={loc}; killer.den={LName(den)}; killer@{kloc}; victim@{vloc}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][kidnap {tag}] state log error: {e.Message}"); }
        }

        // LIVE SAMPLE (dev-only): one line capturing whether the victim (and killer) are ACTUALLY at the den
        // right now, plus how far the victim is from it and whether the den's safe-teleport spot is inside the
        // den. If VICTIM_AT_DEN is never true across the whole stall, the "GoTo den routine" walk isn't landing.
        private static void LogKidnapLive(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = false; try { ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID); } catch { }
                string st = "?"; try { st = murder.state.ToString(); } catch { }
                string rp = "?"; try { rp = murder.ransomPhase.ToString(); } catch { }
                string kt = "?"; try { kt = $"killTime={murder.killTime:0.0} now={NowHours():0.0} killPhase={murder.kidnapKillPhase}"; } catch { }
                NewAddress den = null; try { den = killer.den; } catch { }
                NewGameLocation vloc = null, kloc = null;
                try { vloc = victim.currentGameLocation; } catch { }
                try { kloc = killer.currentGameLocation; } catch { }
                bool atDen = false, kAtDen = false;
                try { atDen = vloc != null && den != null && vloc.Pointer == den.Pointer; } catch { }
                try { kAtDen = kloc != null && den != null && kloc.Pointer == den.Pointer; } catch { }
                float dist = -1f;
                try
                {
                    var vn = victim.currentNode; var da = den != null ? den.anchorNode : null;
                    if (vn != null && da != null) dist = Vector3.Distance(vn.position, da.position);
                }
                catch { }
                string tp = den != null ? DescribeDenTeleport(victim, den) : "den=NULL";
                log.LogInfo($"[SODMotives][kidnap-live] {(ours ? "OURS" : "VANILLA")} [{st}] ransomPhase={rp} {kt} VICTIM_AT_DEN={atDen} victim@{LName(vloc)} ; KILLER_AT_DEN={kAtDen} killer@{LName(kloc)} ; den={LName(den)} ; dist(victim->den.anchor)={dist:0.0} ; {tp}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][kidnap-live] err: {e.Message}"); }
        }

        // SNIPER OBSERVER (dev-only, one-shot): compare a VANILLA sniper to a MOD-FORCED one — the pair's
        // relationship, the killer archetype (MO) + its site/vantage flags, homes, and the initial target site +
        // whether a vantage wall exists for it. Read-only.
        private static void LogSniperObservation(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID);
                log.LogInfo($"[SODMotives][sniper-obs] ===== {(ours ? "MOD-FORCED" : "VANILLA")} SNIPER: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} =====");
                log.LogInfo($"[SODMotives][sniper-obs]   relationship killer->victim: {DescribeEdge(killer, victim)}");
                log.LogInfo($"[SODMotives][sniper-obs]   relationship victim->killer: {DescribeEdge(victim, killer)}");
                string preset = "?"; try { if (murder.preset != null) preset = murder.preset.name; } catch { }
                string mo = "?"; try { if (murder.mo != null) mo = murder.mo.name; } catch { }
                string moFlags = "?";
                try { var m = murder.mo; if (m != null) moFlags = $"requiresSniperVantageAtHome={m.requiresSniperVantageAtHome} allow home/work/public/streets/anywhere={m.allowHome}/{m.allowWork}/{m.allowPublic}/{m.allowStreets}/{m.allowAnywhere}"; } catch { }
                log.LogInfo($"[SODMotives][sniper-obs]   preset={preset} mo={mo} ; {moFlags}");
                NewAddress kh = null, vh = null; try { kh = killer.home; } catch { } try { vh = victim.home; } catch { }
                log.LogInfo($"[SODMotives][sniper-obs]   killer.home={LName(kh)} ; victim.home={LName(vh)}");
                NewGameLocation site = null; try { site = murder.sniperVictimSite; } catch { }
                NewGameLocation loc = null; try { loc = murder.location; } catch { }
                NewGameLocation probeSite = site != null ? site : loc;   // VoyeurSniper leaves sniperVictimSite null and shoots the victim at murder.location (their home)
                log.LogInfo($"[SODMotives][sniper-obs]   sniperVictimSite={LName(site)} murder.location={LName(loc)} ; siteVantage[{LName(probeSite)}]: {DescribeSniperVantage(killer, probeSite)}");
                log.LogInfo($"[SODMotives][sniper-obs]   victim.home={LName(vh)} ; homeVantage: {DescribeSniperVantage(killer, vh)}");
                log.LogInfo($"[SODMotives][sniper-obs]   {DescribeSniperWeapon(murder)}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][sniper-obs] error: {e.Message}"); }
        }

        // SNIPER LIVE SAMPLE (dev-only, throttled): where the target site is, whether the victim is AT it, where
        // the killer is (and distances), the resolved kill-shot node, and whether a vantage wall exists for the
        // killer+site RIGHT NOW. If the site keeps changing / no vantage is ever found, that is the travellingTo
        // re-pick loop; if a vantage exists but the killer never reaches it, it is a travel/positioning problem.
        private static void LogSniperLive(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = false; try { ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID); } catch { }
                string st = "?"; try { st = murder.state.ToString(); } catch { }
                NewGameLocation site = null; try { site = murder.sniperVictimSite; } catch { }
                NewGameLocation loc = null; try { loc = murder.location; } catch { }
                NewGameLocation probeSite = site != null ? site : loc;   // real target site (VoyeurSniper uses murder.location, not sniperVictimSite)
                NewGameLocation vhome = null; try { vhome = victim.home; } catch { }
                NewGameLocation vloc = null, kloc = null;
                try { vloc = victim.currentGameLocation; } catch { }
                try { kloc = killer.currentGameLocation; } catch { }
                bool vAtSite = false; try { vAtSite = vloc != null && probeSite != null && vloc.Pointer == probeSite.Pointer; } catch { }
                float vDist = -1f, kDist = -1f, kvDist = -1f;
                var sa = probeSite != null ? SafeAnchor(probeSite) : null;
                try { var vn = victim.currentNode; if (vn != null && sa != null) vDist = Vector3.Distance(vn.position, sa.position); } catch { }
                try { var kn = killer.currentNode; if (kn != null && sa != null) kDist = Vector3.Distance(kn.position, sa.position); } catch { }
                try { var kn = killer.currentNode; var vn = victim.currentNode; if (kn != null && vn != null) kvDist = Vector3.Distance(kn.position, vn.position); } catch { }
                string shot = "?"; try { shot = murder.sniperKillShotNode.ToString(); } catch { }
                log.LogInfo($"[SODMotives][sniper-live] {(ours ? "OURS" : "VANILLA")} [{st}] site={LName(probeSite)}(vs={LName(site)}/loc={LName(loc)}) victim@{LName(vloc)} V_AT_SITE={vAtSite} dist(victim->site)={vDist:0.0} ; killer@{LName(kloc)} dist(killer->site)={kDist:0.0} dist(killer->victim)={kvDist:0.0} ; killShotNode={shot} ; siteVantage={DescribeSniperVantage(killer, probeSite)} ; homeVantage={DescribeSniperVantage(killer, vhome)} ; {DescribeSniperWeapon(murder)}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][sniper-live] err: {e.Message}"); }
        }

        // Does a vantage wall exist for this killer to shoot the given target site? Uses the game's OWN solver
        // (Toolbox.TryGetSniperVantagePoint, target-site-first). Read-only; never throws.
        private static string DescribeSniperVantage(Human killer, NewGameLocation site)
        {
            try
            {
                if (killer == null || site == null) return "vantage: killer/site null";
                var tb = Toolbox.Instance; if (tb == null) return "vantage: no Toolbox";
                float score = 0f; bool found;
                try { found = tb.TryGetSniperVantagePoint(killer, site, out _, out score); }
                catch (Exception e) { return "vantage: TryGetSniperVantagePoint threw: " + e.Message; }
                return found ? $"vantage=FOUND score={score:0.00}" : "vantage=NONE (no wall covers this killer+site)";
            }
            catch (Exception e) { return "vantage: err " + e.Message; }
        }

        // Sniper WEAPON/equipment probe (dev-only, OURS-diagnosis aid): did the killer actually acquire a rifle?
        // The acquireEquipment phase sets Murder.acquiredEquipment and fills Murder.weapon (the real Interactable);
        // weaponPreset is the intended weapon. A sniper that reaches travellingTo has PASSED acquire, so this should
        // read acquired=True with a real held weapon — if it does NOT, an acquire failure (not geometry) is the loop
        // cause, and the fix is different. Read-only; never throws.
        private static string DescribeSniperWeapon(MurderController.Murder murder)
        {
            try
            {
                if (murder == null) return "weapon: murder null";
                bool acquired = false; try { acquired = murder.acquiredEquipment; } catch { }
                string preset = "?"; try { var wp = murder.weaponPreset; preset = wp != null ? wp.name : "<null>"; } catch { }
                string held = "?"; try { var w = murder.weapon; held = w != null ? w.name : "<none>"; } catch { }
                string wstr = "?"; try { wstr = string.IsNullOrEmpty(murder.weaponStr) ? "<empty>" : murder.weaponStr; } catch { }
                return $"weapon: acquired={acquired} preset={preset} held={held} weaponStr='{wstr}'";
            }
            catch (Exception e) { return "weapon: err " + e.Message; }
        }

        // Pick a site the killer can ACTUALLY snipe the victim at, using the GAME'S OWN vantage solver
        // (Toolbox.TryGetSniperVantagePoint = the same line-of-sight check the game uses to build vanilla sniper
        // cases). The game force-fires ExecuteSniperShot even with NO line of sight (the nonsensical no-window
        // kills), so the override calls this at SELECTION time and only motivates the sniper if a real vantage
        // exists, pinning the murder to the returned site; else it leaves the case vanilla. Checks the victim's
        // HOME then their WORKPLACE (the sites a home/work sniper uses). Read-only; never throws.
        // TODO(v2): also scan public/routine sites (rooftop assassinations) + pick the killer BY vantage from the
        // victim's enemy pool, to raise the hit rate for pairs with no home/work line of sight.
        internal static bool TryPickSniperSite(Human killer, Human victim, out NewGameLocation site, out NewWall nest)
        {
            site = null; nest = null;
            try
            {
                if (killer == null || victim == null) return false;
                var tb = Toolbox.Instance; if (tb == null) return false;
                NewGameLocation home = null; try { home = victim.home; } catch { }
                if (home != null) { try { if (tb.TryGetSniperVantagePoint(killer, home, out var w1, out _)) { site = home; nest = w1; return true; } } catch { } }
                NewGameLocation work = null;
                try { var job = victim.job; var emp = job != null ? job.employer : null; if (emp != null) work = emp.placeOfBusiness; } catch { }
                if (work != null) { try { if (tb.TryGetSniperVantagePoint(killer, work, out var w2, out _)) { site = work; nest = w2; return true; } } catch { } }
                return false;
            }
            catch { return false; }
        }

        // The STREET/rooftop sniper MO (requiresSniperVantageAtHome == false, e.g. ExCopSniper): unlike VoyeurSniper
        // it sends the killer to a PUBLIC vantage overlooking the target instead of shooting only from their own
        // home. The override swaps motivated snipers to this MO so the killer actually TRAVELS to the vantage our
        // gate confirmed (VoyeurSniper just idles at home and never reaches a public nest). Cached; its per-MO score
        // boosts (a "Retired" job boost) are only preferences, so forcing it onto any motivated killer is fine.
        private static MurderMO _streetSniperMO; private static bool _streetSniperMOSearched;
        internal static MurderMO StreetSniperMO()
        {
            if (_streetSniperMOSearched) return _streetSniperMO;
            _streetSniperMOSearched = true;
            try
            {
                var mos = Resources.FindObjectsOfTypeAll<MurderMO>();
                if (mos != null)
                    for (int i = 0; i < mos.Length; i++)
                    {
                        var mo = mos[i]; if (mo == null || mo.requiresSniperVantageAtHome) continue;
                        var compat = mo.compatibleWith; if (compat == null) continue;
                        for (int j = 0; j < compat.Count; j++)
                        {
                            var p = compat[j];
                            if (p != null && p.caseType == MurderPreset.CaseType.sniper) { _streetSniperMO = mo; break; }
                        }
                        if (_streetSniperMO != null) break;
                    }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] StreetSniperMO error: {e.Message}"); }
            return _streetSniperMO;
        }

        private static NewNode SafeAnchor(NewGameLocation l) { try { return l != null ? l.anchorNode : null; } catch { return null; } }

        // Seal the den behind the fleeing killer: CLOSE the front door(s) — matches vanilla's default (the killer
        // shuts doors on the way out; our swapped killer sometimes left them open). LOCK them too only if
        // KidnapLockDen (optional, beyond vanilla — vanilla leaves the unowned den unlocked). `entrances` are the
        // address's outer doors; the interior (bathroom) door isn't in this list, so it's untouched. Once per victim.
        private static void SealDen(Human killer, NewAddress den, int vid)
        {
            try
            {
                if (den == null) return;
                if (!_denSealed.Add(vid)) return;   // once per victim
                int closed = 0, locked = 0;
                var ents = den.entrances;
                if (ents != null)
                    for (int i = 0; i < ents.Count; i++)
                    {
                        var e = ents[i]; if (e == null) continue;
                        NewDoor d = null; try { d = e.door; } catch { }
                        if (d == null) continue;
                        try { d.SetOpen(0f, killer, true); closed++; } catch { }   // close it (skip animation)
                        if (KidnapLockDen) { try { d.SetLocked(true, killer, false); locked++; } catch { } }
                    }
                MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap-seal] {MotivesPlugin.Name(killer)} closed {closed}{(KidnapLockDen ? $" + locked {locked}" : " (unlocked, like vanilla)")} front door(s) at {LName(den)}.");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][kidnap-seal] err: {e.Message}"); }
        }

        // SAVE/LOAD FIX (B): give the kidnap victim the same persistent "GoTo the den" goal a VANILLA victim has
        // during the pre-restrain phases. Vanilla's victim carries a GoTo@den goal (pri10) through waitForLocation
        // + travellingTo that survives save/load; ours loses it on reload, so nothing pins them and they wander
        // out of the den. We find that goal (present in normal play) or, if it's gone (after a reload), recreate it
        // from the game's own "go to" preset (RoutineControls.toGoGoal) targeting the den, then pin it on top so
        // the victim commits to walking to / staying in the den. Re-asserted each tick (the AI recomputes
        // priority). This is a WALK, not a teleport — the physical trail is unchanged. Never throws.
        private static void EnsureVictimDenGoal(Human victim, NewAddress den, MurderController.Murder murder, int vid)
        {
            try
            {
                if (victim == null || den == null) return;
                NewAIController ai = null; try { ai = victim.ai; } catch { }
                if (ai == null) return;

                // Find an existing goal already targeting the den (the game's own GoTo-den routine in normal play).
                NewAIGoal denGoal = null;
                try
                {
                    var goals = ai.goals;
                    if (goals != null)
                        for (int i = 0; i < goals.Count; i++)
                        {
                            var g = goals[i]; if (g == null) continue;
                            NewGameLocation gl = null, pg = null;
                            try { gl = g.gameLocation; } catch { }
                            try { pg = g.passedGameLocation; } catch { }
                            if ((gl != null && gl.Pointer == den.Pointer) || (pg != null && pg.Pointer == den.Pointer)) { denGoal = g; break; }
                        }
                }
                catch { }

                // Missing (typically after a reload) -> recreate it from the game's own "go to" goal preset.
                if (denGoal == null)
                {
                    AIGoalPreset preset = null; try { preset = RoutineControls.Instance != null ? RoutineControls.Instance.toGoGoal : null; } catch { }
                    if (preset == null) return;
                    try { denGoal = ai.CreateNewGoal(preset, NowHours(), 999f, null, null, den, null, murder, -2); }
                    catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives][kidnap-hold] CreateNewGoal(GoTo den) threw: {ce.Message}"); return; }
                    if (denGoal != null && DebugTools.EnableDebugKeys && _denGoalLogged.Add(vid))
                        MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap-hold] rebuilt victim GoTo-den goal for {MotivesPlugin.Name(victim)} -> {LName(den)} (save/load recovery; matches vanilla's persistent GoTo@den).");
                }
                if (denGoal == null) return;

                // Pin it on top so the victim commits to the den (re-asserted each tick; the AI recomputes priority).
                try { denGoal.basePriority = 100000f; } catch { }
                try { denGoal.priority = 100000f; } catch { }
                try { denGoal.isActive = true; } catch { }
                try { ai.currentGoal = denGoal; } catch { }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][kidnap-hold] EnsureVictimDenGoal err: {e.Message}"); }
        }

        // Spawn the vanilla ransom-note lead item (the preset lead tagged JobTag.U) and register it in
        // murder.activeMurderItems[U], so the game's TriggerKidnappingCase adds the "Examine the ransom note found
        // at <home>" objective for our swapped-in victim (it's gated on that dict entry existing). Replicates
        // exactly what the game's own SpawnItemsCheck would have done for a kidnap victim it set up itself.
        private static void SpawnRansomNote(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                // Already registered (game spawned it, or we already did)? Leave it.
                try { if (murder.activeMurderItems != null && murder.activeMurderItems.ContainsKey(JobPreset.JobTag.U)) { log.LogInfo("[SODMotives][ransom] ransom note (tag U) already present; leaving it."); return; } } catch { }

                var preset = murder.preset; if (preset == null) { log.LogWarning("[SODMotives][ransom] preset null; can't spawn note."); return; }
                var leads = preset.leads; if (leads == null) { log.LogWarning("[SODMotives][ransom] preset.leads null; can't spawn note."); return; }

                int noteIdx = -1; var tags = new List<string>();
                for (int i = 0; i < leads.Count; i++)
                {
                    var ld = leads[i]; if (ld == null) continue;
                    try { tags.Add(ld.itemTag.ToString()); } catch { }
                    try { if (ld.itemTag == JobPreset.JobTag.U) { noteIdx = i; break; } } catch { }
                }
                if (noteIdx < 0)
                {
                    log.LogWarning($"[SODMotives][ransom] no lead tagged U (ransom note) in preset '{SafePresetName(preset)}'. Lead tags present: {string.Join(",", tags.ToArray())}");
                    return;
                }

                var note = leads[noteIdx];
                Interactable item = null;
                try
                {
                    item = MurderController.Instance.SpawnItem(murder, note.spawnItem, note.where, note.belongsTo, note.writer, note.receiver, note.security, note.ownershipRule, note.priority, note.itemTag);
                }
                catch (Exception se) { log.LogWarning($"[SODMotives][ransom] SpawnItem(ransom note) threw: {se.Message}"); return; }
                if (item == null) { log.LogWarning("[SODMotives][ransom] SpawnItem(ransom note) returned null."); return; }

                try { if (murder.activeMurderItems != null && !murder.activeMurderItems.ContainsKey(JobPreset.JobTag.U)) murder.activeMurderItems.Add(JobPreset.JobTag.U, item); }
                catch (Exception ae) { log.LogWarning($"[SODMotives][ransom] registering note in activeMurderItems threw: {ae.Message}"); }

                string pn = "?"; try { pn = note.spawnItem != null ? note.spawnItem.name : "?"; } catch { }
                log.LogInfo($"[SODMotives][ransom] spawned ransom note (tag U, preset '{pn}') for {MotivesPlugin.Name(victim)} + registered in activeMurderItems. TriggerKidnappingCase should now add the 'Examine the ransom note' objective; examining it drives the rest of the chain.");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][ransom] SpawnRansomNote err: {e.Message}"); }
        }

        private static string SafePresetName(MurderPreset p) { try { return p != null ? p.name : "?"; } catch { return "?"; } }

        // Decode-backed helper (dev diagnostic): the game seats a kidnap only when the VICTIM is inside
        // murderer.den, and it gets them there via victim.FindSafeTeleport(den) -> walk. This reports whether the
        // safe-teleport spot is inside the den and whether the den is WALK-reachable (allowTrespass=false), which
        // is the property that distinguishes a walk-in vacant unit from a locked basement. Read-only; never throws.
        internal static string DescribeDenTeleport(Human victim, NewAddress den)
        {
            try
            {
                if (victim == null || den == null) return "victim/den null";
                NewNode node = null;
                try { node = victim.FindSafeTeleport(den, false, true); }
                catch (Exception e) { return "FindSafeTeleport threw: " + e.Message; }
                if (node == null) return "FST-node=NULL (no safe teleport spot found at all)";
                NewGameLocation nodeLoc = null; try { nodeLoc = node.gameLocation; } catch { }
                bool inside = false; try { inside = nodeLoc != null && nodeLoc.Pointer == den.Pointer; } catch { }
                Vector3 npos = default; try { npos = node.position; } catch { }
                // allowTrespass=FALSE = can the victim reach a node inside the den by NORMAL pathing (no
                // trespassing through locked/forbidden doors)? If this is false/outside while the trespass one is
                // inside, the den is walk-UNREACHABLE for the victim (why the "GoTo den" walk goal stalls).
                NewNode walkNode = null; try { walkNode = victim.FindSafeTeleport(den, false, false); } catch { }
                NewGameLocation walkLoc = null; try { walkLoc = walkNode != null ? walkNode.gameLocation : null; } catch { }
                bool walkInside = false; try { walkInside = walkLoc != null && walkLoc.Pointer == den.Pointer; } catch { }
                string walk = walkNode == null ? "WALK_REACHABLE=NULL" : $"WALK_REACHABLE_loc={LName(walkLoc)} WALK_INSIDE_DEN={walkInside}";
                // IsPublicallyOpen(forPlayer=false): is the den enterable by an NPC without keys/trespass?
                string openness = ""; try { openness = $" ; NPC_OPEN={den.IsPublicallyOpen(false)}"; } catch { openness = " ; NPC_OPEN=?"; }
                return $"FST-node.loc={LName(nodeLoc)} INSIDE_DEN={inside} node.pos={npos} ; {walk}{openness}";
            }
            catch (Exception e) { return "DescribeDenTeleport err: " + e.Message; }
        }

        private static bool ValidLoc(MurderController.Murder m, NewGameLocation l)
        { if (l == null) return false; try { return m.IsValidLocation(l); } catch { return false; } }

        private static string LName(NewGameLocation l) { try { return l != null ? l.name : "<null>"; } catch { return "?"; } }

        private static bool IsDead(Human h) { try { return h.isDead; } catch { return false; } }

        // True if the two are at the same murder scene (same game-location, or at least the same room),
        // i.e. the killer is present with the victim — the only situation in which force-finishing yields
        // a coherent crime scene.
        private static bool SameLocation(Human a, Human b)
        {
            try
            {
                var la = a.currentGameLocation; var lb = b.currentGameLocation;
                if (la != null && lb != null && la.Pointer == lb.Pointer) return true;
            }
            catch { }
            try
            {
                var ra = a.currentRoom; var rb = b.currentRoom;
                if (ra != null && rb != null && ra.Pointer == rb.Pointer) return true;
            }
            catch { }
            return false;
        }

        private static string LocName(Human h)
        {
            try { var l = h.currentGameLocation; if (l != null && !string.IsNullOrEmpty(l.name)) return l.name; } catch { }
            try { var b = h.currentBuilding; if (b != null) return "bldg " + b.buildingID; } catch { }
            return "?";
        }
    }
}

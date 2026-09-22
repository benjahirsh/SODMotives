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
        private static readonly HashSet<int> _meetForced = new HashSet<int>();     // kidnaps whose meet we've assisted (log-once guard)
        private static readonly HashSet<int> _observed = new HashSet<int>();       // kidnaps (any, incl. vanilla) we've logged an observation for

        internal static void ResetForNewGame()
        {
            _watchVictimId = -1; _executingSince = 0f;
            _intervened.Clear(); _loggedNoReach.Clear(); _awaitingPost = -1;
            _waitLocVictimId = -1; _waitLocSince = 0f; _probed.Clear(); _waitRecovered.Clear(); _meetForced.Clear();
            _observed.Clear();
        }

        // True once BOTH kidnap meet goals exist (the killer+victim rendezvous goals). Only kidnaps set these.
        private static bool MeetGoalsSet(MurderController.Murder m)
        { try { return m.meetGoal1 != null && m.meetGoal2 != null; } catch { return false; } }

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

                // KIDNAP OBSERVER (READ-ONLY, fires for ANY kidnap incl. VANILLA): capture the pair's
                // relationship, the killer's den, and the meet state so a real vanilla kidnap can be compared
                // side-by-side with our forced one. Also flips on the game's own verbose murder narration.
                // Does NOT intervene in vanilla cases — purely logging.
                try
                {
                    if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap && _observed.Add(vid))
                    {
                        DebugTools.EnableGameVerboseLogging();
                        LogKidnapObservation(murder, killer, victim);
                    }
                }
                catch { }

                if (!MurderSelector.OverriddenVictimIds.Contains(vid)) return;   // INTERVENTIONS below are OURS only — vanilla cases just get observed above

                // waitForLocation stall (a kidnap with no seatable holding 'den', a sniper with no vantage
                // site): this state has no natural resolution, so the case would hang forever. Probe the
                // game's OWN IsValidLocation once (diagnostics — reveals what a valid den is), then cancel the
                // case if it stays stuck past WaitLocationStallHours so the player is never soft-locked.
                if (murder.state == MurderController.MurderState.waitForLocation)
                {
                    if (_probed.Add(vid)) ProbeValidLocations(murder, killer, victim);

                    // MEET ASSIST (kidnap): the pair meets at the restaurant, but the game only completes the
                    // meet — which triggers the abduction (knock out -> restrain -> carry to den; travellingTo
                    // -> executing) — once meetTime exceeds a threshold, i.e. when they linger/sit at the booth.
                    // Our forced pair meets then disperses, so it never accumulates and the case hangs. Forcing
                    // meetTime ONCE didn't stick (the game recomputes it), so force it high EVERY frame while the
                    // meet goals exist, so the game's own Update completes the meet the moment it evaluates the
                    // block. Scoped to kidnaps (only they set meet goals); harmless on any other case.
                    bool meetPending = MeetGoalsSet(murder);
                    if (meetPending)
                    {
                        try { murder.meetTime = 99999f; } catch { }
                        if (_meetForced.Add(vid))
                            MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap] MEET-ASSIST: forcing meetTime high every frame for {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} so the game completes the meet -> abduction.");
                    }

                    if (_waitLocVictimId != vid) { _waitLocVictimId = vid; _waitLocSince = NowHours(); return; }
                    if (_waitRecovered.Contains(vid)) return;
                    float wstuck = NowHours() - _waitLocSince;
                    // A kidnap with its meet set up is mid-abduction (the meet-assist is driving it), so give it
                    // a much longer window before cancelling — otherwise the watchdog pre-empts the meet
                    // completion -> travellingTo (seen in testing: the case reached travellingTo just AFTER a
                    // premature cancel). A den-less / sniper stall (no meet) still cancels at the normal timeout.
                    float cap = meetPending ? WaitLocationStallHours * 6f : WaitLocationStallHours;
                    if (wstuck < cap) return;
                    LogKidnapState(murder, killer, victim, "stall");   // final state before we give up — did the meet ever set up?
                    try { murder.CancelCurrentMurder(); } catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] waitForLocation cancel error: {ce.Message}"); }
                    _waitRecovered.Add(vid);
                    MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] RECOVERED: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} stuck in 'waitForLocation' {wstuck:F1}h (no seatable location — e.g. a kidnap with no valid den) — cancelled the case so it can't hang. See the [den probe] above for why.");
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

        // DIAGNOSTIC: when an overridden case parks in waitForLocation, ask the game's OWN
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

        // KIDNAP OBSERVER: one-shot dump comparing a VANILLA kidnap to a MOD-FORCED one. The big unknowns are
        // (a) the killer<->victim RELATIONSHIP (is a vanilla kidnap victim connected to their kidnapper in a
        // way ours isn't?), (b) the DEN (does vanilla use the killer's own home/property vs our injected vacant
        // unit?), and (c) whether the victim attends the meet. Logs (a)+(b) here; (c) comes from the game's own
        // narration + the meet-state line. Read-only.
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

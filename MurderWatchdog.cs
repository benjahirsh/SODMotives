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

        private static int _watchVictimId = -1;        // the overridden victim currently in 'executing'
        private static float _executingSince = 0f;     // gameTime it entered 'executing'
        private static readonly HashSet<int> _intervened = new HashSet<int>();     // victims we've already force-finished (once-guard)
        private static readonly HashSet<int> _loggedNoReach = new HashSet<int>();  // stalls we logged but chose not to act on (once each)
        private static int _awaitingPost = -1;         // a victim we've killed, waiting to flip the case to 'post'

        internal static void ResetForNewGame()
        {
            _watchVictimId = -1; _executingSince = 0f;
            _intervened.Clear(); _loggedNoReach.Clear(); _awaitingPost = -1;
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
                if (!MurderSelector.OverriddenVictimIds.Contains(vid)) return;   // OURS only — vanilla cases never reach here

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

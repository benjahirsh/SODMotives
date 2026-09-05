using System;
using System.Collections.Generic;

namespace SODMotives
{
    // V2.1 — VICTIM-CENTRIC, MIXED-MOTIVE selection.
    // Build every real (suspect -> victim) motive edge from the event store (affairs +
    // workplace), pick a victim with a rich pool of DIFFERENT-motive suspects, then pick the
    // real killer at random from that pool. Each suspect's motive later drops its own clue;
    // vanilla's physical layer (prints/CCTV/alibi) convicts. No dummy red herrings — richness
    // comes from dense real seeding.
    internal static class MurderSelector
    {
        // Tunables (bound to BepInEx config in Plugin.Load).
        internal static bool EnableOverride = true;
        internal static int MinSuspects = 3;            // PREFER victims with at least this many real suspects
        internal static int KillerPoolSize = 10;        // killer = uniform-random among the victim's top-N suspects
        internal static bool StripSignatures = true;    // remove serial-killer calling card/moniker/graffiti on motivated cases
        // Deterministic (V2 design): force a vanilla case every Nth handled case so the
        // classic serial-killer hunt never disappears. 0 (or less) = never force vanilla.
        // TESTING DEFAULT = 0 so every new sandbox yields a mod case to test.
        internal static int VanillaCaseEvery = 0;

        // --- legacy V1/V2.0 selector knobs, kept bound so existing .cfg files don't break;
        //     no longer consulted by the victim-centric selector. Pruned in release cleanup. ---
        internal static int TopPoolSize = 40;
        internal static float RedHerringBonusPer = 0.4f;
        internal static float WeightExponent = 0.6f;
        internal static float SameTypePenalty = 0.4f;

        private static int _casesSinceForced = 0;
        private static readonly Random _rng = new Random();

        // Occasionally leave a case entirely to vanilla, preserving classic serial-killer
        // hunts (signature and all). Call once per handled case.
        internal static bool ShouldForceVanilla()
        {
            if (VanillaCaseEvery <= 0) return false;   // testing: never force vanilla
            _casesSinceForced++;
            if (_casesSinceForced >= VanillaCaseEvery) { _casesSinceForced = 0; return true; }
            return false;
        }

        // ---- bookkeeping shared with the clue injector / signature stripping / F9 ----
        // Victims whose case we overrode -> used to strip serial-killer signatures.
        internal static readonly HashSet<int> OverriddenVictimIds = new HashSet<int>();
        // The killer's chosen motive per victim -> logging / clue text.
        internal static readonly Dictionary<int, MotiveResult> MotiveByVictim = new Dictionary<int, MotiveResult>();
        // The full mixed-motive suspect pool per overridden victim -> drives clue injection (W3).
        internal static readonly Dictionary<int, List<SuspectEdge>> PoolByVictim = new Dictionary<int, List<SuspectEdge>>();
        // Back-compat: the affair behind an overridden victim's case, IF the killer's motive is
        // an affair -> still used by the (affair-only) clue injector / F9 / interrogation aids
        // until W3–W5 migrate those onto PoolByVictim.
        internal static readonly Dictionary<int, SocialEvent> AffairByVictim = new Dictionary<int, SocialEvent>();
        // Companies that already produced a workplace murder this game -> don't build new workplace
        // candidates for them (keeps EventStore bounded and avoids overlapping same-company cases).
        internal static readonly HashSet<int> UsedWorkplaceCompanies = new HashSet<int>();

        // Clear per-game selection state so a NEW sandbox in the same process doesn't misfire
        // signature-strip / clue injection / F9 against reused humanIDs. Called from SeedForNewGame.
        internal static void ResetForNewGame()
        {
            OverriddenVictimIds.Clear();
            MotiveByVictim.Clear();
            PoolByVictim.Clear();
            AffairByVictim.Clear();
            UsedWorkplaceCompanies.Clear();
            _casesSinceForced = 0;
        }

        private static bool IsValidActor(Human h)
        {
            if (h == null) return false;
            try { if (h.isDead) return false; } catch { }   // never pick a dead person as victim/suspect
            try { int age = h.GetAge(); if (age > 0 && age < 16) return false; } catch { }
            return true;
        }

        // Pick a victim rich in real, event-backed enemies, then a random killer from that pool.
        // Returns the full ranked suspect pool (strongest first) via `pool` for clue injection.
        internal static bool TryPickVictimCentric(out Human murderer, out Human victim, out List<SuspectEdge> pool)
        {
            murderer = null; victim = null; pool = null;

            // Combine STORED events (affairs, + already-materialized workplace cases) with LIVE
            // workplace candidates built on-demand from company rosters (not yet in the store).
            var events = new List<SocialEvent>();
            var stored = EventStore.All;
            if (stored != null) for (int i = 0; i < stored.Count; i++) events.Add(stored[i]);
            int liveWorkplace = 0;
            if (WorkplaceSim.Enable)
            {
                var cands = WorkplaceSim.CandidateEvents();
                if (cands != null) { events.AddRange(cands); liveWorkplace = cands.Count; }
            }
            if (events.Count == 0) return false;
            MotivesPlugin.Log.LogInfo($"[SODMotives] pool sources: {(stored != null ? stored.Count : 0)} stored + {liveWorkplace} live workplace candidate event(s).");

            // 1) Gather every real (suspect -> victim) edge from all events.
            var tmp = new List<SuspectEdge>();
            var byVictim = new Dictionary<int, Dictionary<int, SuspectEdge>>(); // victimId -> (suspectId -> strongest edge)
            var victimRef = new Dictionary<int, Human>();
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e == null) continue;
                tmp.Clear();
                try { e.CollectEdges(tmp); } catch { continue; }
                for (int j = 0; j < tmp.Count; j++)
                {
                    var ed = tmp[j];
                    if (!IsValidActor(ed.suspect) || !IsValidActor(ed.victim) || Motive.Same(ed.suspect, ed.victim)) continue;
                    // Testing (F6): force the whole case to one motive type by dropping other-type edges.
                    if (DebugTools.ForceMotiveType != MotiveType.None && ed.type != DebugTools.ForceMotiveType) continue;
                    int vid = ed.victim.humanID, sid = ed.suspect.humanID;
                    if (!byVictim.TryGetValue(vid, out var perSuspect))
                    {
                        perSuspect = new Dictionary<int, SuspectEdge>();
                        byVictim[vid] = perSuspect;
                        victimRef[vid] = ed.victim;
                    }
                    // Keep the strongest edge per (suspect,victim) — a suspect may have several motives.
                    if (!perSuspect.TryGetValue(sid, out var existing) || ed.score > existing.score)
                        perSuspect[sid] = ed;
                }
            }
            if (byVictim.Count == 0) return false;

            // 2) Prefer victims with >= MinSuspects; else DEGRADE to the richest available tier
            //    (never fall back to vanilla for the floor — the static graph would then always
            //    be vanilla). floor = min(MinSuspects, richest) so we always have candidates.
            int bestCount = 0;
            foreach (var kv in byVictim) if (kv.Value.Count > bestCount) bestCount = kv.Value.Count;
            if (bestCount == 0) return false;
            int floor = Math.Min(MinSuspects, bestCount);

            var candVictims = new List<int>();
            foreach (var kv in byVictim) if (kv.Value.Count >= floor) candVictims.Add(kv.Key);
            if (candVictims.Count == 0) return false;

            // 3) Uniform-random victim.
            int chosenVid = candVictims[_rng.Next(candVictims.Count)];
            victim = victimRef[chosenVid];

            // 4) That victim's suspects, strongest first, capped at KillerPoolSize.
            var suspects = new List<SuspectEdge>(byVictim[chosenVid].Values);
            suspects.Sort((x, y) => y.score.CompareTo(x.score));
            int poolN = Math.Min(Math.Max(1, KillerPoolSize), suspects.Count);

            // 5) Uniform-random killer among the top pool.
            var killerEdge = suspects[_rng.Next(poolN)];
            murderer = killerEdge.suspect;

            // 6) Record for clue injection / diagnostics.
            pool = suspects;
            PoolByVictim[chosenVid] = suspects;
            OverriddenVictimIds.Add(chosenVid);
            MotiveByVictim[chosenVid] = new MotiveResult
            {
                type = killerEdge.type,
                target = victim,
                score = killerEdge.score,
                detail = killerEdge.detail,
            };

            // 7) Materialize any ON-DEMAND (live workplace) events behind this victim's pool. This
            //    populates knownBy now; the affair-only clue/interrogation readers get wired onto
            //    these in W3/W4. Stored events already have id != 0. Record the company so we don't
            //    build a duplicate workplace case for it later.
            for (int i = 0; i < suspects.Count; i++)
            {
                var ev = suspects[i].evt;
                if (ev != null && ev.id == 0)   // a fresh workplace candidate not yet in the store
                {
                    Gossip.Distribute(ev);      // fill knownBy (coworkers + partners) before indexing
                    EventStore.Add(ev);         // assigns id, indexes by knower
                    if (ev.type != SocialEventType.Affair && ev.companyId >= 0)
                        UsedWorkplaceCompanies.Add(ev.companyId);
                }
            }

            // Back-compat: keep the affair-only downstream paths working until W3–W5 migrate them.
            if (killerEdge.evt != null && killerEdge.evt.type == SocialEventType.Affair)
                AffairByVictim[chosenVid] = killerEdge.evt;
            if (killerEdge.evt != null)
                EventStore.MarkKnown(killerEdge.evt, murderer.humanID);  // the killer knows their own motive event

            return true;
        }
    }
}

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
        // EXPERIMENTAL ([Troubleshooting] MotivateKidnaps): when true, the override ALSO motivates KIDNAP
        // cases (swaps in a motivated killer -> victim pair) instead of leaving them fully vanilla. Default
        // OFF — motivated kidnaps are UNPROVEN and may stall the case at waitForLocation while the game looks
        // for a viable holding "den" for the motive-chosen kidnapper. Sniper cases always stay vanilla. See
        // docs/extensions/motivated-kidnap-handover.md. Flip on only for testing (F1 force-kidnap + F9 trace).
        internal static bool MotivateKidnaps = false;
        internal static int MinSuspects = 3;            // PREFER victims with at least this many real suspects
        internal static int KillerPoolSize = 10;        // killer = uniform-random among the victim's top-N suspects
        // THE MAIN MIX KNOB: probability [0..1] a case is a relationship-MOTIVE case; the rest are left
        // entirely to vanilla (serial-killer, signature and all). Release default 1.0 = every case is a
        // motive case (the mod always shows); lower it to mix in classic vanilla serial hunts for variety.
        // Read once per case, so an edit applies to the NEXT murder.
        internal static float MotiveCaseShare = 1.0f;
        // Per-motive-FAMILY weights (each 0..1), NORMALISED by their sum at selection so any combination
        // works. When the F6 force is OFF, a case's motive family is drawn by these relative weights.
        internal static float AffairShare = 0.35f;      // infidelity / love-triangle
        internal static float WorkplaceShare = 0.46f;   // promotion + layoffs (bounded by how many bosses exist)
        internal static float PropertyShare = 0.18f;    // eviction + rent-arrears (bounded by how many landlords exist)
        internal static float FeudShare = 0.35f;        // personal feuds
        internal static float DebtShare = 0.35f;        // debts
        internal static bool StripSignatures = true;    // remove serial-killer calling card/moniker/graffiti on motivated cases

        private static readonly Random _rng = new Random();

        // KIDNAP DEN FIX. A kidnap can only seat its holding location if the killer has a den: the game's
        // Murder.IsValidLocation accepts ONLY `newLoc == murderer.den` (Human.den, a NewAddress). Vanilla
        // assigns that den (via Human.SetDen, which also decorates it with the MO's den furniture) when it
        // sets up a kidnapper it chose itself; our motive-chosen / swapped-in killer skips that, so den==null
        // and the case hangs at waitForLocation forever (confirmed: 0/870 locations valid). We assign one
        // ourselves. Start with the killer's own home as the holding den (always exists, private, pathable);
        // returns true if the killer ends up with a den. Never throws.
        internal static bool EnsureKidnapDen(Human killer, Human victim, MurderMO mo)
        {
            var log = MotivesPlugin.Log;
            try
            {
                if (killer == null) return false;
                NewAddress existing = null; try { existing = killer.den; } catch { }
                if (existing != null) { log.LogInfo($"[SODMotives][den] killer {MotivesPlugin.Name(killer)} already has a den ({SafeName(existing)}); leaving it."); return true; }

                // Holding den = the killer's own residence for now (a valid private location they control).
                NewAddress den = null; try { den = killer.home; } catch { }
                if (den == null) { log.LogInfo($"[SODMotives][den] killer {MotivesPlugin.Name(killer)} has no home to use as a den — can't seat a kidnap; watchdog will fall back."); return false; }

                try { killer.SetDen(den, mo); }
                catch (Exception se)
                {
                    log.LogWarning($"[SODMotives][den] SetDen threw ({se.Message}); setting the den field directly as a fallback.");
                    try { killer.den = den; } catch { }
                }
                NewAddress now = null; try { now = killer.den; } catch { }
                log.LogInfo($"[SODMotives][den] assigned {MotivesPlugin.Name(killer)}.den = {SafeName(now)} (decorated with MO {(mo != null ? mo.name : "<none>")}). IsValidLocation will now accept it.");
                return now != null;
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][den] EnsureKidnapDen error: {e.Message}"); return false; }
        }

        private static string SafeName(NewAddress a) { try { return a != null ? a.name : "<null>"; } catch { return "?"; } }

        // Occasionally leave a case entirely to vanilla, preserving classic serial-killer
        // hunts (signature and all). Call once per handled case.
        internal static bool ShouldForceVanilla()
        {
            // With probability (1 - MotiveCaseShare) leave this case entirely to vanilla (serial-killer,
            // signature and all). MotiveCaseShare >= 1 => never (all motive); <= 0 => always vanilla.
            return MotiveCaseShare < 1f && _rng.NextDouble() >= MotiveCaseShare;
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
        // The actual motive event behind the killer, for ANY event type (affair / promotion /
        // layoffs). Drives the type-agnostic F9 knower list + F12 nearest-knower teleport.
        internal static readonly Dictionary<int, SocialEvent> EventByVictim = new Dictionary<int, SocialEvent>();
        // Companies that already produced a workplace murder this game -> don't build new workplace
        // candidates for them (keeps EventStore bounded and avoids overlapping same-company cases).
        internal static readonly HashSet<int> UsedWorkplaceCompanies = new HashSet<int>();
        // Buildings that already produced a property (eviction / rent-arrears) murder this game ->
        // don't build new property candidates for them (keeps EventStore bounded, avoids overlap).
        internal static readonly HashSet<int> UsedBuildings = new HashSet<int>();

        // Clear per-game selection state so a NEW sandbox in the same process doesn't misfire
        // signature-strip / clue injection / F9 against reused humanIDs. Called from SeedForNewGame.
        internal static void ResetForNewGame()
        {
            OverriddenVictimIds.Clear();
            MotiveByVictim.Clear();
            PoolByVictim.Clear();
            AffairByVictim.Clear();
            EventByVictim.Clear();
            UsedWorkplaceCompanies.Clear();
            UsedBuildings.Clear();
        }

        // Persistence (pass 2): replace all per-case maps with state imported from the save sidecar.
        // Callers pass already-resolved objects (Humans via the id->Human map, events via the
        // id->SocialEvent map), so this just clears + copies. Restores F9's "mod case" verdict,
        // the suspect pool, and the knower aids (F9/F12) after a reload.
        internal static void RehydrateMaps(
            HashSet<int> overridden,
            Dictionary<int, MotiveResult> motiveByVictim,
            Dictionary<int, List<SuspectEdge>> poolByVictim,
            Dictionary<int, SocialEvent> affairByVictim,
            Dictionary<int, SocialEvent> eventByVictim,
            HashSet<int> usedCompanies,
            HashSet<int> usedBuildings)
        {
            OverriddenVictimIds.Clear();
            if (overridden != null) foreach (int v in overridden) OverriddenVictimIds.Add(v);

            MotiveByVictim.Clear();
            if (motiveByVictim != null) foreach (var kv in motiveByVictim) MotiveByVictim[kv.Key] = kv.Value;

            PoolByVictim.Clear();
            if (poolByVictim != null) foreach (var kv in poolByVictim) PoolByVictim[kv.Key] = kv.Value;

            AffairByVictim.Clear();
            if (affairByVictim != null) foreach (var kv in affairByVictim) AffairByVictim[kv.Key] = kv.Value;

            EventByVictim.Clear();
            if (eventByVictim != null) foreach (var kv in eventByVictim) EventByVictim[kv.Key] = kv.Value;

            UsedWorkplaceCompanies.Clear();
            if (usedCompanies != null) foreach (int c in usedCompanies) UsedWorkplaceCompanies.Add(c);

            UsedBuildings.Clear();
            if (usedBuildings != null) foreach (int b in usedBuildings) UsedBuildings.Add(b);
        }

        private static bool IsValidActor(Human h)
        {
            if (h == null) return false;
            try { if (h.isDead) return false; } catch { }              // never pick a dead person as victim/suspect
            try { if (h.removedFromWorld) return false; } catch { }    // nor one despawned/removed (e.g. an arrested murderer)
            try { int age = h.GetAge(); if (age > 0 && age < 16) return false; } catch { }
            // Must have a real home in a building. HOMELESS NPCs (home == null, salary 0) break the
            // vanilla murder pipeline — it stalls forever at 'acquire equipment' (no money/place to get a
            // weapon) — and give our clue engine nowhere to place notes (PlaceObject fails at a null home,
            // so 0 clues land). Vanilla's own picker avoids them; our override must too. Gating here drops
            // homeless people as BOTH killer/victim AND red-herring suspects (this method gates each edge's
            // suspect and victim), so the whole materialized pool is housed. Making the homeless murder-
            // capable is out of scope (would be its own mod).
            try { var hm = h.home; if (hm == null || hm.building == null) return false; } catch { return false; }
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
            int liveProperty = 0;
            if (PropertySim.Enable)
            {
                var pcands = PropertySim.CandidateEvents();
                if (pcands != null) { events.AddRange(pcands); liveProperty = pcands.Count; }
            }
            if (events.Count == 0) return false;
            MotivesPlugin.Log.LogInfo($"[SODMotives] pool sources: {(stored != null ? stored.Count : 0)} stored + {liveWorkplace} live workplace + {liveProperty} live property candidate event(s); Force={DebugTools.ForceLabel()}.");

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
                    // Testing (F6): force the whole case to one motive type by dropping other-type edges,
                    // and (finer) to one SPECIFIC event type when ForceEventType is set (e.g. Layoffs but
                    // not Promotion, both Professional) by dropping edges from other event types.
                    if (DebugTools.ForceMotiveType != MotiveType.None && ed.type != DebugTools.ForceMotiveType) continue;
                    if (DebugTools.ForceEventType.HasValue && (ed.evt == null || ed.evt.type != DebugTools.ForceEventType.Value)) continue;
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

            // 3) Balance case FAMILIES by the per-motive weights (F6 force OFF only). Bucket victims by
            //    which motive families their suspects cover (a mixed victim sits in several), then draw a
            //    family by the NORMALISED weights so no single motive swamps the rest.
            List<int> bucket = candVictims;
            if (DebugTools.ForceMotiveType == MotiveType.None)
            {
                var affairV = new List<int>();
                var workV = new List<int>();
                var propertyV = new List<int>();   // eviction / rent-arrears (Money via a landlord/tenant event)
                var feudV = new List<int>();        // personal feuds
                var debtV = new List<int>();        // debts (Money via a debt event)
                foreach (int vid in candVictims)
                {
                    bool hasAffair = false, hasWork = false, hasProperty = false, hasFeud = false, hasDebt = false;
                    foreach (var ed in byVictim[vid].Values)
                    {
                        switch (ed.type)
                        {
                            case MotiveType.Infidelity: hasAffair = true; break;
                            case MotiveType.Professional: hasWork = true; break;
                            case MotiveType.PersonalFeud: hasFeud = true; break;
                            case MotiveType.Money:
                                // Money splits into property (eviction/rent-arrears) vs debt by event type.
                                if (ed.evt != null && ed.evt.type == SocialEventType.Debt) hasDebt = true;
                                else hasProperty = true;
                                break;
                        }
                    }
                    if (hasAffair) affairV.Add(vid);
                    if (hasWork) workV.Add(vid);
                    if (hasProperty) propertyV.Add(vid);
                    if (hasFeud) feudV.Add(vid);
                    if (hasDebt) debtV.Add(vid);
                }
                // Draw a family by the five weights, normalised by their sum (so any 0..1 combination works).
                float pAffair = Math.Max(0f, AffairShare), pWork = Math.Max(0f, WorkplaceShare),
                      pProperty = Math.Max(0f, PropertyShare), pFeud = Math.Max(0f, FeudShare), pDebt = Math.Max(0f, DebtShare);
                float sum = pAffair + pWork + pProperty + pFeud + pDebt;
                if (sum <= 0f) bucket = candVictims;
                else
                {
                    double r = _rng.NextDouble() * sum;
                    bucket = (r < pAffair) ? affairV
                           : (r < pAffair + pWork) ? workV
                           : (r < pAffair + pWork + pProperty) ? propertyV
                           : (r < pAffair + pWork + pProperty + pFeud) ? feudV
                           : debtV;
                }
                // Chosen family empty this city -> fall back to any non-empty family.
                if (bucket == null || bucket.Count == 0)
                    bucket = affairV.Count > 0 ? affairV
                           : workV.Count > 0 ? workV
                           : propertyV.Count > 0 ? propertyV
                           : feudV.Count > 0 ? feudV
                           : debtV.Count > 0 ? debtV
                           : candVictims;
            }

            // 4) Uniform-random victim within the chosen bucket.
            int chosenVid = bucket[_rng.Next(bucket.Count)];
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
                if (ev != null && ev.id == 0)   // a fresh on-demand candidate not yet in the store
                {
                    Gossip.Distribute(ev);      // fill knownBy (audience + partners) before indexing
                    EventStore.Add(ev);         // assigns id, indexes by knower
                    if (ev.companyId >= 0 && (ev.type == SocialEventType.Promotion || ev.type == SocialEventType.Layoffs))
                        UsedWorkplaceCompanies.Add(ev.companyId);
                    else if (ev.buildingId >= 0 && (ev.type == SocialEventType.Eviction || ev.type == SocialEventType.RentArrears))
                        UsedBuildings.Add(ev.buildingId);
                }
            }

            // Back-compat: keep the affair-only downstream paths working until W3–W5 migrate them.
            if (killerEdge.evt != null && killerEdge.evt.type == SocialEventType.Affair)
                AffairByVictim[chosenVid] = killerEdge.evt;
            if (killerEdge.evt != null)
            {
                EventByVictim[chosenVid] = killerEdge.evt;               // any-type motive event (F9/F12 knower aids)
                EventStore.MarkKnown(killerEdge.evt, murderer.humanID);  // the killer knows their own motive event
            }

            return true;
        }
    }
}

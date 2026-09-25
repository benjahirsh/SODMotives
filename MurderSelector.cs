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
        // The mixer's KIDNAP slider ([Motive Mix] MotivatedKidnapShare): probability [0..1] that a KIDNAP case
        // gets a motivated killer -> victim pair (a walk-native abduction to a real holding den) instead of
        // running fully vanilla — the kidnap analogue of MotiveCaseShare for murders. 1 (default) = every kidnap
        // is motivated; 0 = kidnaps stay vanilla. Sniper cases always stay vanilla. Read once per kidnap case.
        internal static float MotivatedKidnapShare = 1f;
        // The mixer's SNIPER slider ([Motive Mix] MotivatedSniperShare): probability [0..1] that a SNIPER case
        // gets a motivated killer -> victim pair instead of running fully vanilla. Default 0 = snipers stay
        // vanilla, because a motivated sniper still LOOPS in travellingTo (the relationship-chosen killer usually
        // has no reachable vantage onto a site the victim visits; the vantage-viable constraint isn't built yet).
        // Set it to 1 to TEST motivated snipers via the natural sandbox path (the [sniper-obs]/[sniper-live]
        // diagnostics observe them) without the F3 force key. Read once per sniper case.
        internal static float MotivatedSniperShare = 0f;
        internal static int MinSuspects = 3;            // PREFER victims with at least this many real suspects
        internal static int KillerPoolSize = 10;        // killer = uniform-random among the victim's top-N suspects
        // Diagnostic: after a TryPickVictimCentric call with a preferKiller, how many of the scanned top-pool
        // suspects passed it (e.g. how many of the victim's motivated aggressors are voyeur-sniper-viable) and how
        // many were scanned. Read by the sniper override to measure the voyeur supply. Single-threaded game, so a
        // shared static is fine.
        internal static int LastPreferViableCount = 0;
        internal static int LastPoolScanned = 0;
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

                NewAddress vh = null; try { vh = victim != null ? victim.home : null; } catch { }
                NewAddress kh = null; try { kh = killer.home; } catch { }

                // Prefer a VACANT residence (nobody lives there) as a proper secret holding den — so the victim
                // is taken somewhere they don't live and the case is a real mystery. Never the victim's home
                // (they'd be "kidnapped" into their own apartment) and, since it's vacant, never a cohabited home.
                NewAddress den = PickVacantDen(killer, victim, vh, kh);
                string how = "vacant, WALK-reachable residence";
                if (den == null)
                {
                    // Fallback: the killer's own home, but ONLY if the victim doesn't live there too — holding
                    // the victim at their own address is trivially solvable (the earlier cohabiting affair bug).
                    // The fallback must ALSO be walk-reachable (else the victim can't walk in and the case just
                    // hangs), so skip it if it isn't.
                    bool fbOk = kh != null && (vh == null || !SamePlace(kh, vh));
                    if (fbOk) { try { fbOk = MurderWatchdog.KidnapDenWalkReachable(victim, kh); } catch { fbOk = false; } }
                    if (fbOk) { den = kh; how = "killer's home (walk-reachable; no vacant walk-reachable den found)"; }
                }
                if (den == null)
                {
                    log.LogInfo($"[SODMotives][den] no WALK-reachable vacant den (nor a walk-reachable killer.home) — can't seat a clean walk-native kidnap for {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)}; watchdog will cancel + fall back to vanilla.");
                    return false;
                }

                try { killer.SetDen(den, mo); }
                catch (Exception se)
                {
                    log.LogWarning($"[SODMotives][den] SetDen threw ({se.Message}); setting the den field directly as a fallback.");
                    try { killer.den = den; } catch { }
                }
                NewAddress now = null; try { now = killer.den; } catch { }
                log.LogInfo($"[SODMotives][den] assigned {MotivesPlugin.Name(killer)}.den = {SafeName(now)} [{how}] (decorated with MO {(mo != null ? mo.name : "<none>")}). IsValidLocation will now accept it.");
                // DIAGNOSTIC: the game seats the kidnap only once the victim is INSIDE this den, which it does by
                // teleporting them to victim.FindSafeTeleport(den). Report whether that safe-teleport spot is
                // actually inside the den — if not, the victim can never land in the den and the case loops.
                try { log.LogInfo($"[SODMotives][den] teleport-viability: {MurderWatchdog.DescribeDenTeleport(victim, now)}"); } catch { }
                return now != null;
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][den] EnsureKidnapDen error: {e.Message}"); return false; }
        }

        // Pick a VACANT, WALK-IN-ABLE address for the kidnapper's holding den, excluding the victim's + killer's
        // homes. Source = CityData.addressDirectory. THE ACCESS INSIGHT (2026-09-23, user's lead + in-game): the
        // victim can only WALK into a den it isn't TRESPASSING in (Human.IsTrespassing gates the AI's pathing). A
        // locked/owned unit (basement, hotel room) => the committed victim freezes on the street outside. Two den
        // kinds pass: (1) an UNOWNED "Vacant address N" unit — abandoned, no owner, so nobody trespasses; PRIVATE
        // (no residents, no public traffic) = vanilla's den; (2) an NPC-open PUBLIC venue (IsPublicallyOpen=true,
        // e.g. "Public bathrooms") — PROVEN in-game (victim walked in + was restrained) but public, can't be
        // locked. So PREFER the private "Vacant address" units, fall back to the open venue (proven; never lose
        // what works). EXCLUDE basements. If the private units still don't work, the fix is an explicit access
        // grant (NewAddress.AddOwner/AddGuestPass) in EnsureKidnapDen. Logs the pool once.
        private static NewAddress PickVacantDen(Human killer, Human victim, NewAddress victimHome, NewAddress killerHome)
        {
            try
            {
                var cd = CityData.Instance;
                var dir = cd != null ? cd.addressDirectory : null;
                if (dir == null) return null;
                var vacantAddr = new List<NewAddress>();   // unowned "Vacant address N" — private + walk-in (vanilla's den)
                var openVenue = new List<NewAddress>();     // NPC-open public venue (bathrooms) — proven walk-reachable fallback
                int totalVacant = 0, basements = 0, noDoor = 0; string vExamples = "", oExamples = "";
                for (int i = 0; i < dir.Count; i++)
                {
                    var a = dir[i]; if (a == null) continue;
                    if (victimHome != null && SamePlace(a, victimHome)) continue;
                    if (killerHome != null && SamePlace(a, killerHome)) continue;
                    int occ = 0; try { var inh = a.inhabitants; occ = inh != null ? inh.Count : 0; } catch { occ = -1; }
                    if (occ != 0) continue;                          // vacant only (no residents to interfere with the hold)
                    totalVacant++;
                    string n = null; try { n = a.name; } catch { }
                    if (!string.IsNullOrEmpty(n) && n.IndexOf("Basement", StringComparison.OrdinalIgnoreCase) >= 0) { basements++; continue; }
                    if (!string.IsNullOrEmpty(n) && n.IndexOf("Vacant", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Require a real, lockable DOOR: some "Vacant address" units are open floors (e.g. a flooded
                        // basement level) with no door on the room — the victim can't be sealed in, and the killer
                        // can't lock it. Vanilla only uses enclosed units with a door. Skip the door-less ones.
                        if (!HasLockableDoor(a)) { noDoor++; continue; }
                        vacantAddr.Add(a);
                        if (vExamples.Length < 120) vExamples += (vExamples.Length > 0 ? " | " : "") + n;
                    }
                    else
                    {
                        bool open = false; try { open = a.IsPublicallyOpen(false); } catch { }
                        if (open) { openVenue.Add(a); if (oExamples.Length < 120 && !string.IsNullOrEmpty(n)) oExamples += (oExamples.Length > 0 ? " | " : "") + n; }
                    }
                }
                MotivesPlugin.Log.LogInfo($"[SODMotives][den] den-pool: {totalVacant} vacant -> {vacantAddr.Count} 'Vacant address' w/door (private) + {openVenue.Count} NPC-open venue + {basements} basement + {noDoor} vacant-addr-no-door (all excluded).");
                MotivesPlugin.Log.LogInfo($"[SODMotives][den]   'Vacant address' (try first): {(vExamples.Length > 0 ? vExamples : "<NONE>")}");
                MotivesPlugin.Log.LogInfo($"[SODMotives][den]   open-venue fallback: {(oExamples.Length > 0 ? oExamples : "<none>")}");
                // Prefer the private unowned "Vacant address" den; fall back to a proven NPC-open venue.
                NewAddress pick = PickTeleportViable(vacantAddr, victim);
                if (pick == null) pick = PickTeleportViable(openVenue, victim);
                return pick;
            }
            catch { return null; }
        }

        // Sanity check: the candidate den must have a usable safe-teleport node (the game needs one to seat the
        // hold). WALK-reachability is now gated upstream in PickVacantDen via IsPublicallyOpen, so this is just a
        // loose "has a node" check (allowTrespass=true), scanning a bounded random sample.
        private static NewAddress PickTeleportViable(List<NewAddress> cands, Human victim)
        {
            if (cands == null || cands.Count == 0) return null;
            int tries = Math.Min(cands.Count, 120);
            for (int t = 0; t < tries; t++)
            {
                var a = cands[_rng.Next(cands.Count)];
                bool ok = true;
                try { if (victim != null) ok = victim.FindSafeTeleport(a, false, true) != null; } catch { ok = false; }
                if (ok) return a;
            }
            return null;
        }

        // True if the address has at least one entrance with a real, lockable DOOR (NodeAccess.door != null) — an
        // enclosed unit the victim can be sealed into. Filters out open "Vacant address" floors (flooded basement
        // levels etc.) that have no door, which vanilla never uses as a den. Never throws.
        private static bool HasLockableDoor(NewAddress a)
        {
            try
            {
                var ents = a.entrances;
                if (ents != null)
                    for (int i = 0; i < ents.Count; i++)
                    {
                        var e = ents[i]; if (e == null) continue;
                        try { if (e.door != null) return true; } catch { }
                    }
                try { var me = a.GetMainEntrance(); if (me != null && me.door != null) return true; } catch { }
            }
            catch { }
            return false;
        }

        private static bool SamePlace(NewGameLocation a, NewGameLocation b)
        { try { return a != null && b != null && a.Pointer == b.Pointer; } catch { return false; } }

        private static string SafeName(NewAddress a) { try { return a != null ? a.name : "<null>"; } catch { return "?"; } }

        // Occasionally leave a case entirely to vanilla, preserving classic serial-killer
        // hunts (signature and all). Call once per handled case.
        internal static bool ShouldForceVanilla()
        {
            // With probability (1 - MotiveCaseShare) leave this case entirely to vanilla (serial-killer,
            // signature and all). MotiveCaseShare >= 1 => never (all motive); <= 0 => always vanilla.
            return MotiveCaseShare < 1f && _rng.NextDouble() >= MotiveCaseShare;
        }

        // Should THIS kidnap case be motivated? Drawn per kidnap by the mixer's MotivatedKidnapShare slider.
        internal static bool ShouldMotivateKidnap()
        {
            if (MotivatedKidnapShare <= 0f) return false;
            if (MotivatedKidnapShare >= 1f) return true;
            return _rng.NextDouble() < MotivatedKidnapShare;
        }

        // Should THIS sniper case be motivated? Drawn per sniper by the mixer's MotivatedSniperShare slider.
        // Default share 0 => snipers stay vanilla (a motivated sniper still loops; see the field comment).
        internal static bool ShouldMotivateSniper()
        {
            if (MotivatedSniperShare <= 0f) return false;
            if (MotivatedSniperShare >= 1f) return true;
            return _rng.NextDouble() < MotivatedSniperShare;
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
        // killerFilter (optional): when set (SNIPER cases), the chosen killer MUST satisfy it — a
        // (candidateKiller, victim) -> bool test, used to require a real sniper vantage. If none of the
        // victim's top suspects pass, the method fails (returns false) so the caller leaves the case vanilla.
        internal static bool TryPickVictimCentric(out Human murderer, out Human victim, out List<SuspectEdge> pool, System.Func<Human, Human, bool> preferKiller = null)
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

            // DEV/TEST: restrict the victim to someone who WORKS at a specific place (DebugTools.ForceVictimWorkplace,
            // e.g. "Daffodil Ward") to reproduce a particular sniper geometry. Only applies when a matching victim with
            // motivated suspects exists; otherwise falls back to the normal pool so it never dead-ends a playtest.
            string wpFilter = null; try { wpFilter = DebugTools.ForceVictimWorkplace; } catch { }
            if (!string.IsNullOrEmpty(wpFilter))
            {
                var matched = new List<int>();
                for (int i = 0; i < candVictims.Count; i++)
                {
                    Human vh = victimRef.TryGetValue(candVictims[i], out var h) ? h : null;
                    string wp = null;
                    try { var j = vh != null ? vh.job : null; var em = j != null ? j.employer : null; var pob = em != null ? em.placeOfBusiness : null; wp = pob != null ? pob.name : null; } catch { }
                    if (wp != null && wp.IndexOf(wpFilter, StringComparison.OrdinalIgnoreCase) >= 0) matched.Add(candVictims[i]);
                }
                if (matched.Count > 0) { candVictims = matched; MotivesPlugin.Log.LogInfo($"[SODMotives] ForceVictimWorkplace='{wpFilter}': restricted victim pool to {matched.Count} employee(s) there."); }
                else MotivesPlugin.Log.LogWarning($"[SODMotives] ForceVictimWorkplace='{wpFilter}': no candidate victim with motivated suspects works there; using the normal pool.");
            }

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

            // 5) Killer among the top pool, uniform-random (every suspect is a real motivated red herring). A soft
            //    preferKiller (SNIPER: prefer a killer whose OWN home overlooks the victim, so the case can be a
            //    VoyeurSniper -- more varied + more vanilla-like than every ExCop case funnelling to the city's one
            //    best sniper street) restricts the pick to pool members that pass it, but falls back to the full
            //    uniform-random pick when NONE pass (still a real motivated killer, just an ExCop sniper). No motive
            //    is lost either way: the whole pool is event-backed suspects. Bookkeeping uses the chosen killerEdge.
            SuspectEdge killerEdge;
            int preferViable = 0;
            if (preferKiller != null)
            {
                var viable = new List<SuspectEdge>();
                for (int i = 0; i < poolN; i++)
                {
                    var e = suspects[i];
                    try { if (e.suspect != null && preferKiller(e.suspect, victim)) viable.Add(e); } catch { }
                }
                preferViable = viable.Count;
                killerEdge = viable.Count > 0 ? viable[_rng.Next(viable.Count)] : suspects[_rng.Next(poolN)];
            }
            else
            {
                killerEdge = suspects[_rng.Next(poolN)];
            }
            LastPreferViableCount = preferViable; LastPoolScanned = poolN;
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

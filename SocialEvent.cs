using System;
using System.Collections.Generic;
using UnityEngine;

namespace SODMotives
{
    // Generic social-event model (V2). Affairs first; Workplace (Promotion / Layoffs)
    // added in 2.1; landlord/tenant (Eviction / RentArrears) in 2.3; one-to-one personal
    // motives (Feud / Debt) in 2.4. Each type only has to declare its own seeding, its
    // (suspect -> victim) motive edges, its testimony phrasing, and its gossip audience.
    // Everything else (store, knowledge, gossip, interrogation, selection) is shared.
    // Workplace + property events are built ON DEMAND from live rosters/residency (WorkplaceSim /
    // PropertySim); affairs, feuds and debts are SEEDED at new-game (persistent world texture).
    // NOTE: persistence serializes the type by NAME (e.type.ToString / Enum.TryParse), so new
    // members can be appended freely without breaking older sidecars.
    internal enum SocialEventType { Affair, Promotion, Layoffs, Eviction, RentArrears, Feud, Debt }

    // A directed "X has a real, event-backed reason to harm Y" edge derived from an event.
    // The pool selector unions these across all events per victim. `score` only trims a
    // pool larger than KillerPoolSize — the actual killer pick is uniform, so exact values
    // rarely matter.
    internal struct SuspectEdge
    {
        public Human suspect;
        public Human victim;
        public float score;
        public MotiveType type;   // Infidelity | Professional | ...
        public string detail;     // human-readable motive (logging + clue/testimony flavour)
        public SocialEvent evt;   // the backing event (clue + gossip source)
    }

    internal class SocialEvent
    {
        public int id;
        public SocialEventType type;
        public Human a;          // affair: lover1 | promotion: promotee | layoffs: the boss (victim)
        public Human b;          // affair: lover2 | promotion: boss/decider | layoffs: null
        public readonly List<Human> group = new List<Human>();  // promotion: passed-over rivals | layoffs: the redundancy list (all suspects)
        public string placeName; // where it's associated (affair: a home; workplace: company name)
        public float time;       // game time it "happened" (0 = backdated/unknown for now)
        public int companyId = -1; // workplace events only: source Company.companyID (per-game dedup)
        public int buildingId = -1; // property events only: source NewBuilding.buildingID (per-game dedup). LIVE-ONLY — not serialized; only the materialize loop reads it, and UsedBuildings persists separately.

        // humanIDs of everyone who knows about this event (witnessed or told).
        public readonly HashSet<int> knownBy = new HashSet<int>();

        // Emit every (suspect -> victim) motive edge this event creates. The selector filters
        // invalid actors / self-edges and dedupes (suspect,victim) to the strongest edge.
        internal void CollectEdges(List<SuspectEdge> outList)
        {
            switch (type)
            {
                case SocialEventType.Affair: CollectAffairEdges(outList); break;
                case SocialEventType.Promotion: CollectPromotionEdges(outList); break;
                case SocialEventType.Layoffs: CollectLayoffsEdges(outList); break;
                case SocialEventType.Eviction: CollectEvictionEdges(outList); break;
                case SocialEventType.RentArrears: CollectRentArrearsEdges(outList); break;
                case SocialEventType.Feud: CollectFeudEdges(outList); break;
                case SocialEventType.Debt: CollectDebtEdges(outList); break;
            }
        }

        private void Emit(List<SuspectEdge> o, Human s, Human v, float score, MotiveType t, string detail)
        {
            if (s == null || v == null) return;
            o.Add(new SuspectEdge { suspect = s, victim = v, score = score, type = t, detail = detail, evt = this });
        }

        // Love-triangle edges: betrayed partners resent the cheater AND the lover; the secret
        // lovers are volatile; and either cheater might silence the other's partner. Any member
        // can be killer or victim (mirrors the V2.0 affair pairings, now individually scored).
        private void CollectAffairEdges(List<SuspectEdge> o)
        {
            Human pa = SafePartner(a), pb = SafePartner(b);
            string an = SafeName(a), bn = SafeName(b);
            if (pa != null)
            {
                Emit(o, pa, a, 116f, MotiveType.Infidelity, $"their partner {an} was having an affair with {bn}");
                Emit(o, pa, b, 114f, MotiveType.Infidelity, $"{bn} was sleeping with their partner {an}");
                Emit(o, a, pa, 60f, MotiveType.Infidelity, $"wanted rid of their partner {SafeName(pa)} to be with {bn}");
                Emit(o, b, pa, 65f, MotiveType.Infidelity, $"{SafeName(pa)} could expose the affair with {an}");
            }
            if (pb != null)
            {
                Emit(o, pb, b, 116f, MotiveType.Infidelity, $"their partner {bn} was having an affair with {an}");
                Emit(o, pb, a, 114f, MotiveType.Infidelity, $"{an} was sleeping with their partner {bn}");
                Emit(o, b, pb, 60f, MotiveType.Infidelity, $"wanted rid of their partner {SafeName(pb)} to be with {an}");
                Emit(o, a, pb, 65f, MotiveType.Infidelity, $"{SafeName(pb)} could expose the affair with {bn}");
            }
            Emit(o, a, b, 70f, MotiveType.Infidelity, $"a volatile secret affair with {bn}");
            Emit(o, b, a, 70f, MotiveType.Infidelity, $"a volatile secret affair with {an}");
        }

        // Promotion: a = promotee (never a suspect in their own promotion), b = boss/decider,
        // group = resentful coworkers. Each resenter has a reason to kill the rival AND the boss.
        private void CollectPromotionEdges(List<SuspectEdge> o)
        {
            string pn = SafeName(a), dn = SafeName(b);
            string co = string.IsNullOrEmpty(placeName) ? "work" : placeName;
            for (int i = 0; i < group.Count; i++)
            {
                Human r = group[i];
                if (r == null) continue;
                Emit(o, r, a, 92f, MotiveType.Professional, $"{pn} got the promotion at {co} they were passed over for");
                if (b != null)
                    Emit(o, r, b, 80f, MotiveType.Professional, $"{dn} passed them over and handed {pn} the promotion at {co}");
            }
        }

        // Layoffs: a = the boss (victim); group = the employees on the redundancy list. Every one
        // of them has a reason to kill the boss who was about to end their livelihood.
        private void CollectLayoffsEdges(List<SuspectEdge> o)
        {
            string bn = SafeName(a);
            string co = string.IsNullOrEmpty(placeName) ? "work" : placeName;
            for (int i = 0; i < group.Count; i++)
            {
                Human l = group[i];
                if (l == null) continue;
                Emit(o, l, a, 85f, MotiveType.Professional, $"{bn} had them on the layoff list at {co}");
            }
        }

        // Eviction: a = landlord (victim); group = aggrieved tenants being cleared out for a
        // redevelopment. Every tenant about to lose their home has a reason to kill the landlord.
        // Each tenant's detail names THEIR OWN unit (the home they're losing), not one shared building
        // label: the old `placeName` was just the first resident's unit (PropertySim.PlaceName), so every
        // suspect read as being cleared from the same — often wrong — address in F9/logs. Falls back to
        // placeName / "their building" only when a tenant's own address is unavailable.
        private void CollectEvictionEdges(List<SuspectEdge> o)
        {
            for (int i = 0; i < group.Count; i++)
            {
                Human t = group[i];
                if (t == null) continue;
                string where = SafeAddr(t);
                if (string.IsNullOrEmpty(where)) where = string.IsNullOrEmpty(placeName) ? "their building" : placeName;
                Emit(o, t, a, 85f, MotiveType.Money, $"{SafeName(a)} was clearing them out of {where} in the redevelopment");
            }
        }

        // RentArrears: a = tenant (victim); b = landlord (the lone suspect). A landlord fed up with
        // a tenant who wouldn't pay is the one holding the grudge.
        // RentArrears is BIDIRECTIONAL (like feuds/debts): the landlord may kill a tenant who won't pay,
        // OR the cornered tenant may kill the landlord hounding them over the arrears. Either is a suspect
        // in the other's murder, so the selector can pick either as the victim.
        private void CollectRentArrearsEdges(List<SuspectEdge> o)
        {
            // Name the tenant's OWN unit (SafeAddr(a)), not the shared first-resident building label
            // (placeName); fall back to placeName / "their building" if the unit is unavailable.
            string co = SafeAddr(a);
            if (string.IsNullOrEmpty(co)) co = string.IsNullOrEmpty(placeName) ? "their building" : placeName;
            Emit(o, b, a, 70f, MotiveType.Money, $"their tenant at {co} owed them months of back rent");
            Emit(o, a, b, 70f, MotiveType.Money, $"their landlord had been hounding them over months of unpaid rent at {co}");
        }

        // Feud: a and b have genuine bad blood (seeded from a soured relationship). Either could snap
        // and kill the other, so the grudge is BIDIRECTIONAL — each is a suspect in the other's murder.
        private void CollectFeudEdges(List<SuspectEdge> o)
        {
            string an = SafeName(a), bn = SafeName(b);
            Emit(o, a, b, 55f, MotiveType.PersonalFeud, $"bad blood with {bn}");
            Emit(o, b, a, 55f, MotiveType.PersonalFeud, $"bad blood with {an}");
        }

        // Debt (BIDIRECTIONAL, like feuds): a = debtor, b = creditor. Either can kill — a creditor fed
        // up with a debtor who stopped paying, OR a debtor who kills the creditor to be rid of the debt.
        // The "settle your debt" clue note is always the creditor's (see ClueInjector.InjectMoneyThreat),
        // anchored to the DEBTOR's home whichever of them is the victim.
        private void CollectDebtEdges(List<SuspectEdge> o)
        {
            Emit(o, b, a, 60f, MotiveType.Money, $"{SafeName(a)} owed them money and had stopped paying it back");   // creditor kills debtor
            Emit(o, a, b, 58f, MotiveType.Money, $"owed {SafeName(b)} money and wanted to be rid of the debt");      // debtor kills creditor
        }

        private static Human SafePartner(Human h) { if (h == null) return null; try { return h.partner; } catch { return null; } }

        // What an NPC who knows this WORKPLACE event would say when asked about `subject`.
        // Returns the line for the subject's OWN role in the event, or null if the subject
        // isn't part of it (or this is an affair — Interrogation composes those inline, keeping
        // that hard-won path untouched). Design rules (W4, locked):
        //   * Subject's role only — one answer reveals only the picked person's own involvement;
        //     the player assembles the suspect pool by asking about each coworker separately.
        //   * The subject is referred to as "they/them", NEVER by name — the player just picked
        //     their photo, so restating the name is redundant. Only the event's OTHER anchors
        //     (the promotee, when the subject isn't them) are named; the suspect `group` is
        //     never enumerated.
        //   * Resentment applied equally — every passed-over rival / laid-off employee gets the
        //     same bitter flavour, so the real killer reads no guiltier than the red herrings.
        //   * Layoffs are framed as an ECONOMIC necessity ("the place lost money, the boss had
        //     to make cuts"), never a literal "redundancy list" — the physical clue is the hard
        //     list; the gossip is the soft, oblique lead.
        //   * Rumour framing ("word is / I heard") so a partner-knower (told at home) isn't
        //     oddly claiming firsthand office knowledge, and every word stays TRUE.
        // `seed` should be stable per (npc, subject) so re-asking the same person is consistent.
        public string TestimonyAbout(Human subject, int seed)
        {
            if (subject == null) return null;
            int sid;
            try { sid = subject.humanID; } catch { return null; }

            switch (type)
            {
                case SocialEventType.Promotion:
                {
                    // Pointer only (V2.2): name no company or person — the promotion-letter clue carries the
                    // promotee's identity; gossip just flags a promotion in the subject's orbit. No jealousy
                    // / "passed over" framing — the player infers the rivalry from the pool.
                    if (a != null && a.humanID == sid)        // the subject IS the promotee
                        return Pick(seed,
                            "Apparently they just got a promotion.",
                            "Heard they were promoted recently.",
                            "Someone mentioned they'd landed a promotion at work.");
                    if (b != null && b.humanID == sid)        // the subject IS the boss / decider
                        return Pick(seed,
                            "Heard they gave someone a promotion at work.",
                            "Apparently they recently promoted an employee.");
                    if (InGroup(sid))                         // a rival who was up for the same promotion
                        return Pick(seed,
                            "I think they were up for a promotion recently.",
                            "I heard they were after a promotion that went to a colleague instead.",
                            "Word is they'd been chasing a promotion at work.");
                    return null;
                }

                case SocialEventType.Layoffs:
                {
                    if (a != null && a.humanID == sid)        // the subject IS the boss (victim)
                        return Pick(seed,
                            "They've been laying off some of their employees.",
                            "Apparently they've had to make some cuts to their staff.",
                            "Heard they have been putting some of their employees through a round of layoffs.");
                    if (InGroup(sid))                         // someone let go in the cuts (equal treatment)
                        return Pick(seed,
                            "Apparently they got fired from work.",
                            "I think they lost their job recently.",
                            "Someone mentioned they'd been laid off.",
                            "I gather they'd been let go from their job.");
                    return null;
                }

                case SocialEventType.Eviction:
                {
                    // A landlord runs several places, so gossip stays generic, never a specific flat
                    // (the specific address could be the killer's own home). Pointer, not detail.
                    if (a != null && a.humanID == sid)        // the subject IS the landlord (victim)
                        return Pick(seed,
                            "Word is they're clearing tenants out of one of their buildings.",
                            "Apparently they're evicting tenants from one of their properties.",
                            "Heard they've been turfing tenants out of a property they own.",
                            "I think they are clearing a building of its tenants.");
                    if (InGroup(sid))                         // a tenant being evicted (equal grievance)
                        return Pick(seed,
                            "I heard that their landlord's evicting them.",
                            "Rumour has it they're being turfed out of their place.",
                            "Someone mentioned they're being forced to move from their home.",
                            "Heard their landlord's kicking them out.");
                    return null;
                }

                case SocialEventType.RentArrears:
                {
                    if (a != null && a.humanID == sid)        // the subject IS the tenant (victim)
                        return Pick(seed,
                            "Apparently they've been struggling with money.",
                            "I think they had been having money trouble.",
                            "Heard they fell behind on their rent.",
                            "Money had been tight for them, apparently.");
                    if (b != null && b.humanID == sid)        // the subject IS the landlord (suspect)
                        return Pick(seed,
                            "Apparently they had a tenant who wouldn't pay up.",
                            "Word is one of their tenants stopped paying rent.",
                            "Heard they were chasing a tenant for unpaid rent.",
                            "I think one of their tenants had stopped paying rent.");
                    return null;
                }

                case SocialEventType.Feud:
                {
                    // NOTE (A5, 2026-09-16): gossip now COLLAPSES a subject's feuds into one bubble via
                    // Interrogation.FeudLine, so ComposeLines no longer routes feuds here. Kept for
                    // reference / any other caller of TestimonyAbout.
                    // NAME the other party: a feud has no relationship anchor and no identity-carrying
                    // clue (its threat note is anonymous handwriting), so this testimony IS the lead. Both
                    // parties are treated identically, so naming reads no guiltier for the killer than a
                    // herring. The subject stays "they/them" (the player just picked their photo).
                    Human other = (a != null && a.humanID == sid) ? b : (b != null && b.humanID == sid) ? a : null;
                    if (other == null) return null;
                    return Pick(seed,
                        $"Word is they'd had a falling-out with {SafeName(other)}.",
                        $"Heard they and {SafeName(other)} had a serious falling-out.",
                        $"They and {SafeName(other)} had fallen out, from what I hear.",
                        $"Word is there's bad blood between them and {SafeName(other)}.",
                        $"Heard they'd been at odds with {SafeName(other)}.",
                        $"Word is they and {SafeName(other)} weren't on speaking terms.");
                }

                case SocialEventType.Debt:
                {
                    // Money sphere + the other party named (no residency/roster anchor to follow otherwise).
                    if (a != null && a.humanID == sid)        // the subject IS the debtor
                        return Pick(seed,
                            $"Word is they owed {SafeName(b)} money.",
                            $"Heard they were in debt to {SafeName(b)}.",
                            $"They owed {SafeName(b)} money and hadn't paid it back, from what I hear.",
                            $"Word is they'd borrowed money off {SafeName(b)} and never repaid it.",
                            $"Heard they were behind on money they owed {SafeName(b)}.");
                    if (b != null && b.humanID == sid)        // the subject IS the creditor
                        return Pick(seed,
                            $"Word is {SafeName(a)} owed them money.",
                            $"Heard {SafeName(a)} was in debt to them.",
                            $"{SafeName(a)} owed them money and hadn't paid it back, from what I hear.",
                            $"Word is they'd lent {SafeName(a)} money that was never repaid.",
                            $"Heard {SafeName(a)} still owed them a fair bit.");
                    return null;
                }
            }
            return null;   // Affair (handled inline in Interrogation) or unknown type.
        }

        private bool InGroup(int humanId)
        {
            for (int i = 0; i < group.Count; i++)
                if (group[i] != null && group[i].humanID == humanId) return true;
            return false;
        }

        // True if `humanId` is any participant of this event (a, b, or in the group).
        internal bool InvolvesHuman(int humanId)
        {
            if (a != null && a.humanID == humanId) return true;
            if (b != null && b.humanID == humanId) return true;
            return InGroup(humanId);
        }

        // Deterministic variant pick (stable per seed; never negative-indexes).
        private static string Pick(int seed, params string[] variants)
            => variants[((seed % variants.Length) + variants.Length) % variants.Length];

        internal static string SafeName(Human h)
        {
            if (h == null) return "someone";
            try { return h.citizenName; } catch { return "someone"; }
        }

        // Testing aid: the `max` NPCs (excluding the participants) who know this event whose
        // HOME is nearest the given scene position — so the player can teleport to the scene
        // and interview the neighbours/coworkers standing closest. Formatted "Name — Address
        // (bldg B/floor F) — Dm", nearest first. One directory scan; call once per case, not per frame.
        internal List<string> NearestKnowers(Vector3 scenePos, int max, Human mustKnow = null)
        {
            var scored = new List<(float d, string line)>();
            try
            {
                var dir = CityData.Instance != null ? CityData.Instance.citizenDirectory : null;
                if (dir != null)
                    for (int i = 0; i < dir.Count; i++)
                    {
                        var h = dir[i];
                        if (h == null) continue;
                        int id = h.humanID;
                        if (InvolvesHuman(id)) continue;   // a participant isn't a knower of their own event (a/b/group)
                        if (!knownBy.Contains(id)) continue;
                        // Only list knowers who can NAME the person being investigated — they're the ones
                        // who'll identify the photo (give a name) and volunteer gossip.
                        if (mustKnow != null && !Motive.KnowsName(h, mustKnow)) continue;

                        NewAddress home = null; try { home = h.home; } catch { }
                        if (home == null) continue;

                        float dist = float.MaxValue; bool hasPos = false;
                        try { var an = home.anchorNode; if (an != null) { dist = Dist(scenePos, an.position); hasPos = true; } }
                        catch { }

                        string addr = SafeLocName(home);
                        int floor = 0; try { if (home.floor != null) floor = home.floor.floor; } catch { }
                        int bld = -1; try { if (home.building != null) bld = home.building.buildingID; } catch { }
                        string dtxt = hasPos ? $"{dist:0}m" : "?m";
                        scored.Add((dist, $"{SafeName(h)} — {addr} (bldg {bld}/floor {floor}) — {dtxt}"));
                    }
            }
            catch { }
            scored.Sort((x, y) => x.d.CompareTo(y.d));
            var outList = new List<string>();
            for (int i = 0; i < scored.Count && i < max; i++) outList.Add(scored[i].line);
            if (outList.Count == 0) outList.Add("(nobody else knows)");
            return outList;
        }

        // The single knower (excluding participants) whose home is nearest the scene — for
        // a "teleport to the closest gossip" testing hotkey.
        internal Human NearestKnowerHuman(Vector3 scenePos, Human mustKnow = null)
        {
            Human best = null; float bestD = float.MaxValue;
            try
            {
                var dir = CityData.Instance != null ? CityData.Instance.citizenDirectory : null;
                if (dir != null)
                    for (int i = 0; i < dir.Count; i++)
                    {
                        var h = dir[i];
                        if (h == null) continue;
                        int id = h.humanID;
                        if (InvolvesHuman(id)) continue;   // a participant isn't a knower of their own event (a/b/group)
                        if (!knownBy.Contains(id)) continue;
                        if (mustKnow != null && !Motive.KnowsName(h, mustKnow)) continue;
                        NewAddress home = null; try { home = h.home; } catch { }
                        if (home == null) continue;
                        float d = float.MaxValue;
                        try { var an = home.anchorNode; if (an != null) d = Dist(scenePos, an.position); } catch { }
                        if (d < bestD) { bestD = d; best = h; }
                    }
            }
            catch { }
            return best;
        }

        private static float Dist(Vector3 p, Vector3 q)
        {
            float dx = p.x - q.x, dy = p.y - q.y, dz = p.z - q.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string SafeLocName(NewGameLocation loc)
        {
            try { return loc != null ? loc.name : "?"; } catch { return "?"; }
        }

        // A human's own home-address label (the specific unit), or null if unavailable. Used so an
        // eviction suspect's motive detail names the home THEY are losing, not a shared building label.
        private static string SafeAddr(Human h)
        {
            try { var home = h != null ? h.home : null; return home != null && !string.IsNullOrEmpty(home.name) ? home.name : null; }
            catch { return null; }
        }
    }

    // Who "would know" an event, keyed by type. Each event type declares its own
    // audience: the acquaintance connection types whose holders plausibly notice it,
    // plus whether the participants' current partners are told. Seeding + interrogation
    // stay generic, so a new event type only adds a case in Audience/IncludesPartners.
    internal static class Gossip
    {
        // Affairs get noticed around the home AND the workplace, and by close friends —
        // so the player can find a knower by investigating either participant's home or job.
        private static readonly Acquaintance.ConnectionType[] AffairAudience =
        {
            Acquaintance.ConnectionType.neighbor,
            Acquaintance.ConnectionType.housemate,
            Acquaintance.ConnectionType.familiarResidence,
            Acquaintance.ConnectionType.friend,
            Acquaintance.ConnectionType.workTeam,
            Acquaintance.ConnectionType.workOther,
            Acquaintance.ConnectionType.familiarWork,
        };

        // Workplace events (promotion / chopping-block) are noticed by coworkers.
        private static readonly Acquaintance.ConnectionType[] WorkAudience =
        {
            Acquaintance.ConnectionType.workTeam,
            Acquaintance.ConnectionType.workOther,
            Acquaintance.ConnectionType.familiarWork,
        };

        // Property events (eviction / rent arrears) are noticed around the home — fellow tenants,
        // neighbours, close friends — and via the landlord edge itself, so the whole tenant pool is
        // reachable even when co-tenants aren't directly acquainted with one another.
        private static readonly Acquaintance.ConnectionType[] PropertyAudience =
        {
            Acquaintance.ConnectionType.neighbor,
            Acquaintance.ConnectionType.housemate,
            Acquaintance.ConnectionType.familiarResidence,
            Acquaintance.ConnectionType.friend,
            Acquaintance.ConnectionType.landlord,
        };

        // Feuds & debts are personal — noticed by the pair's shared social circle (friends,
        // neighbours, coworkers) and vented to partners; that circle is how a knower can point the
        // player at the other party (the testimony names them).
        private static readonly Acquaintance.ConnectionType[] SocialAudience =
        {
            Acquaintance.ConnectionType.friend,
            Acquaintance.ConnectionType.neighbor,
            Acquaintance.ConnectionType.familiarResidence,
            Acquaintance.ConnectionType.workTeam,
            Acquaintance.ConnectionType.workOther,
            Acquaintance.ConnectionType.familiarWork,
        };

        private static Acquaintance.ConnectionType[] Audience(SocialEventType t)
        {
            switch (t)
            {
                case SocialEventType.Affair: return AffairAudience;
                case SocialEventType.Promotion:
                case SocialEventType.Layoffs: return WorkAudience;
                case SocialEventType.Eviction:
                case SocialEventType.RentArrears: return PropertyAudience;
                case SocialEventType.Feud:
                case SocialEventType.Debt: return SocialAudience;
                default: return AffairAudience;
            }
        }

        // Are the participants' CURRENT partners told about this event type?
        //   Affair    -> false: a betrayed partner "knowing" is applied only at
        //                murder-selection time (assume-known), never free gossip.
        //   Workplace -> true: people vent about work rivalries to their partner.
        private static bool IncludesPartners(SocialEventType t)
        {
            switch (t)
            {
                case SocialEventType.Affair: return false;
                case SocialEventType.Promotion:
                case SocialEventType.Layoffs: return true;
                case SocialEventType.Eviction:
                case SocialEventType.RentArrears: return true;
                case SocialEventType.Feud:
                case SocialEventType.Debt: return true;
                default: return false;
            }
        }

        // Populate e.knownBy with everyone who would know, per the event's audience.
        // Call BEFORE EventStore.Add so the knower index is built from the full set.
        internal static void Distribute(SocialEvent e)
        {
            if (e == null) return;
            var conns = Audience(e.type);
            bool partners = IncludesPartners(e.type);
            AddFor(e, e.a, conns, partners);
            AddFor(e, e.b, conns, partners);
            if (e.group != null)
                for (int i = 0; i < e.group.Count; i++)
                    AddFor(e, e.group[i], conns, partners);
        }

        private static void AddFor(SocialEvent e, Human h, Acquaintance.ConnectionType[] conns, bool includePartner)
        {
            if (h == null) return;
            try { e.knownBy.Add(h.humanID); } catch { }

            if (includePartner)
            {
                Human p = null; try { p = h.partner; } catch { }
                if (p != null) e.knownBy.Add(p.humanID);
            }

            try
            {
                var list = h.acquaintances;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var acq = list[i];
                    if (acq == null || !Matches(acq, conns)) continue;
                    Human other = acq.GetOther(h);
                    if (other != null) e.knownBy.Add(other.humanID);
                }
            }
            catch { }
        }

        private static bool Matches(Acquaintance acq, Acquaintance.ConnectionType[] conns)
        {
            var c = acq.connections;
            if (c == null) return false;
            for (int i = 0; i < c.Count; i++)
                for (int j = 0; j < conns.Length; j++)
                    if (c[i] == conns[j]) return true;
            return false;
        }
    }

    // Holds all events and a per-knower index for fast interrogation lookups.
    internal static class EventStore
    {
        private static readonly List<SocialEvent> _events = new List<SocialEvent>();
        private static readonly Dictionary<int, List<SocialEvent>> _byKnower = new Dictionary<int, List<SocialEvent>>();
        private static int _nextId = 1;

        internal static int Count => _events.Count;

        internal static void Clear()
        {
            _events.Clear();
            _byKnower.Clear();
            _nextId = 1;
        }

        // Persistence (pass 2): replace the whole store with a set imported from the save sidecar,
        // PRESERVING each event's id (so the MurderSelector maps that reference events by id still
        // line up) and rebuilding the per-knower index from each event's knownBy. _nextId is set
        // past the max restored id so any NEW event created after the load can't collide with a
        // restored one. Each event is expected to already have its scalar fields + resolved
        // a/b/group/knownBy populated by the caller.
        internal static void RehydrateFrom(List<SocialEvent> events)
        {
            _events.Clear();
            _byKnower.Clear();
            int maxId = 0;
            if (events != null)
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (e == null) continue;
                    _events.Add(e);
                    if (e.id > maxId) maxId = e.id;
                    foreach (int k in e.knownBy) Index(k, e);
                }
            _nextId = maxId + 1;
        }

        internal static SocialEvent Add(SocialEvent e)
        {
            e.id = _nextId++;
            _events.Add(e);
            foreach (int k in e.knownBy) Index(k, e);
            return e;
        }

        internal static void MarkKnown(SocialEvent e, int humanId)
        {
            if (e.knownBy.Add(humanId)) Index(humanId, e);
        }

        private static void Index(int humanId, SocialEvent e)
        {
            if (!_byKnower.TryGetValue(humanId, out var list)) { list = new List<SocialEvent>(); _byKnower[humanId] = list; }
            list.Add(e);
        }

        // Events a given NPC knows about (empty list if none).
        internal static List<SocialEvent> KnownBy(int humanId)
        {
            return _byKnower.TryGetValue(humanId, out var list) ? list : _empty;
        }
        private static readonly List<SocialEvent> _empty = new List<SocialEvent>();

        internal static IReadOnlyList<SocialEvent> All => _events;
    }
}

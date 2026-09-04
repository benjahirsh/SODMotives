using System;
using System.Collections.Generic;
using UnityEngine;

namespace SODMotives
{
    // Generic social-event model (V2). Affairs first; Workplace (Promotion / ChoppingBlock)
    // added in 2.1; Feuds later. Each type only has to declare its own seeding, its
    // (suspect -> victim) motive edges, its testimony phrasing, and its gossip audience.
    // Everything else (store, knowledge, gossip, interrogation, selection) is shared.
    internal enum SocialEventType { Affair, Promotion, ChoppingBlock }

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
        public Human a;          // primary participants (affair: lover1; promotion: promotee; chopping: aggrieved)
        public Human b;          // affair: lover2; promotion/chopping: boss / decision-maker
        public readonly List<Human> group = new List<Human>();  // extra participants (promotion: resentful coworkers)
        public string placeName; // where it's associated (affair: a home; workplace: company name)
        public float time;       // game time it "happened" (0 = backdated/unknown for now)

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
                case SocialEventType.ChoppingBlock: CollectChoppingEdges(outList); break;
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

        // Chopping block: a = aggrieved employee facing termination, b = boss who put them there.
        private void CollectChoppingEdges(List<SuspectEdge> o)
        {
            if (b == null) return;
            string co = string.IsNullOrEmpty(placeName) ? "work" : placeName;
            Emit(o, a, b, 85f, MotiveType.Professional, $"{SafeName(b)} has them on the chopping block at {co}");
        }

        private static Human SafePartner(Human h) { if (h == null) return null; try { return h.partner; } catch { return null; } }

        // What an NPC who knows this event would say when asked.
        public string Testimony(int idx)
        {
            string an = SafeName(a), bn = SafeName(b);
            switch (type)
            {
                case SocialEventType.Affair:
                    // Phrased as rumour/knowledge, not a specific eyewitness claim, so every
                    // word stays TRUE and corroboratable against the real relationship graph
                    // (we don't simulate an actual sighting at a place/time).
                    switch (idx % 3)
                    {
                        case 0: return $"Word is, {an} and {bn} are seeing each other on the sly.";
                        case 1: return $"You didn't hear it from me, but {an} and {bn} have something going on behind their partners' backs.";
                        default: return $"There's talk about {an} and {bn}. More than just friends, if you follow me.";
                    }
            }
            return "Nothing comes to mind.";
        }

        internal static string SafeName(Human h)
        {
            if (h == null) return "someone";
            try { return h.citizenName; } catch { return "someone"; }
        }

        // Testing aid: the `max` NPCs (excluding the participants) who know this event whose
        // HOME is nearest the given scene position — so the player can teleport to the scene
        // and interview the neighbours/coworkers standing closest. Formatted "Name — Address
        // (bldg B/floor F) — Dm", nearest first. One directory scan; call once per case, not per frame.
        internal List<string> NearestKnowers(Vector3 scenePos, int max)
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
                        if ((a != null && a.humanID == id) || (b != null && b.humanID == id)) continue;
                        if (!knownBy.Contains(id)) continue;

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
        internal Human NearestKnowerHuman(Vector3 scenePos)
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
                        if ((a != null && a.humanID == id) || (b != null && b.humanID == id)) continue;
                        if (!knownBy.Contains(id)) continue;
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

        // Example for a future type — a workplace promotion is known by coworkers only
        // (plus partners, via IncludesPartners):
        //   private static readonly Acquaintance.ConnectionType[] WorkAudience =
        //   { workTeam, workOther, familiarWork };

        private static Acquaintance.ConnectionType[] Audience(SocialEventType t)
        {
            switch (t)
            {
                case SocialEventType.Affair: return AffairAudience;
                default: return AffairAudience;
            }
        }

        // Are the participants' CURRENT partners told about this event type?
        //   Affair    -> false: a betrayed partner "knowing" is applied only at
        //                murder-selection time (assume-known), never free gossip.
        //   Promotion -> true (future): people tell their partner good news.
        private static bool IncludesPartners(SocialEventType t)
        {
            switch (t)
            {
                case SocialEventType.Affair: return false;
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

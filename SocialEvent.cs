using System;
using System.Collections.Generic;

namespace SODMotives
{
    // Generic social-event model (V2). Affairs first; Workplace/Feuds are added later
    // by giving each type its own seeding + testimony phrasing. Everything else
    // (store, knowledge, gossip, interrogation) is shared.
    internal enum SocialEventType { Affair }

    internal class SocialEvent
    {
        public int id;
        public SocialEventType type;
        public Human a;          // primary participants
        public Human b;
        public string placeName; // where it's associated (flavour / lead)
        public float time;       // game time it "happened" (0 = backdated/unknown for now)

        // humanIDs of everyone who knows about this event (witnessed or told).
        public readonly HashSet<int> knownBy = new HashSet<int>();

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

        // Testing aid: up to `max` names of NPCs (excluding the participants) who know
        // this event — i.e. people the player can interview to uncover it.
        internal string KnowerSample(int max)
        {
            var names = new List<string>();
            try
            {
                var dir = CityData.Instance != null ? CityData.Instance.citizenDirectory : null;
                if (dir != null)
                    for (int i = 0; i < dir.Count && names.Count < max; i++)
                    {
                        var h = dir[i];
                        if (h == null) continue;
                        int id = h.humanID;
                        if ((a != null && a.humanID == id) || (b != null && b.humanID == id)) continue;
                        if (knownBy.Contains(id)) names.Add(SafeName(h));
                    }
            }
            catch { }
            return names.Count == 0 ? "(nobody else knows)" : string.Join(", ", names);
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

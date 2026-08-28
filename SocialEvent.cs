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
                    string place = string.IsNullOrEmpty(placeName) ? "" : $" over near {placeName}";
                    switch (idx % 3)
                    {
                        case 0: return $"Between us? I've seen {an} and {bn} getting awfully cosy{place}. Don't think their partners know.";
                        case 1: return $"You didn't hear it from me, but {an} and {bn} have been sneaking around together{place}.";
                        default: return $"There's something going on with {an} and {bn}. More than friends, if you catch my meaning.";
                    }
            }
            return "Nothing comes to mind.";
        }

        internal static string SafeName(Human h)
        {
            if (h == null) return "someone";
            try { return h.citizenName; } catch { return "someone"; }
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

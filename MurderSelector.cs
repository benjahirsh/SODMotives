using System;
using System.Collections.Generic;

namespace SODMotives
{
    // Chooses a motivated (killer -> victim) pair from the city's social graph,
    // using weighted-random selection among the strongest feuds, softly favouring
    // victims who have multiple motivated enemies (natural red herrings).
    internal static class MurderSelector
    {
        // Tunables (bound to BepInEx config in Plugin.Load).
        internal static bool EnableOverride = true;
        internal static int TopPoolSize = 40;          // weighted pick drawn from this many top feuds
        internal static float RedHerringBonusPer = 0.4f; // weight bonus per extra aggressor on the same victim
        internal static float WeightExponent = 0.6f;   // <1 compresses score gaps so weaker motive types surface
        internal static float SameTypePenalty = 0.4f;  // multiplier applied to a motive type that was just used
        internal static bool StripSignatures = true;   // remove serial-killer calling card/moniker/graffiti on motivated cases
        // Deterministic (V2 design): force a vanilla case every Nth handled case so the
        // classic serial-killer hunt never disappears. 0 (or less) = never force vanilla.
        // TESTING DEFAULT = 0 so every new sandbox yields a mod case to test.
        internal static int VanillaCaseEvery = 0;
        private static int _casesSinceForced = 0;

        private static readonly Random _rng = new Random();

        // Occasionally leave a case entirely to vanilla, preserving classic
        // serial-killer hunts (signature and all) for variety.
        // Call once per handled case. Returns true when this case should be left to
        // vanilla to honour the "guaranteed vanilla every N" cadence.
        internal static bool ShouldForceVanilla()
        {
            if (VanillaCaseEvery <= 0) return false;   // testing: never force vanilla
            _casesSinceForced++;
            if (_casesSinceForced >= VanillaCaseEvery) { _casesSinceForced = 0; return true; }
            return false;
        }

        // Victims whose case we overrode -> used to strip serial-killer signatures.
        internal static readonly HashSet<int> OverriddenVictimIds = new HashSet<int>();
        // Motive chosen per victim -> used by the clue injector to pick clue text.
        internal static readonly Dictionary<int, MotiveResult> MotiveByVictim = new Dictionary<int, MotiveResult>();
        // Recent motive types (most recent first) for cross-murder variety.
        private static readonly List<MotiveType> _recentTypes = new List<MotiveType>();

        private struct Candidate { public Human aggressor; public MotiveResult motive; public float weight; }

        private static bool IsValidActor(Human h)
        {
            if (h == null) return false;
            try { int age = h.GetAge(); if (age > 0 && age < 16) return false; } catch { }
            return true;
        }

        // Returns true and yields a motivated pair; false if the city has no real motives.
        internal static bool TryPick(out Human murderer, out Human victim, out MotiveResult motive)
        {
            murderer = null; victim = null; motive = default;

            CityData city = CityData.Instance;
            if (city == null || city.citizenDirectory == null) return false;
            var cits = city.citizenDirectory;
            int n = cits.Count;

            var cands = new List<Candidate>();
            var targetAggr = new Dictionary<int, int>();

            var targets = new List<MotiveResult>();
            for (int i = 0; i < n; i++)
            {
                Human a = cits[i];
                if (!IsValidActor(a)) continue;
                targets.Clear();
                Motive.CollectTargets(a, targets);   // ALL motivated targets, not just the top one
                foreach (var m in targets)
                {
                    if (m.target == null || !IsValidActor(m.target)) continue;
                    cands.Add(new Candidate { aggressor = a, motive = m });
                    int tid = m.target.humanID;
                    targetAggr[tid] = targetAggr.TryGetValue(tid, out int c) ? c + 1 : 1;
                }
            }

            if (cands.Count == 0) return false;

            MotiveType lastType = _recentTypes.Count > 0 ? _recentTypes[0] : MotiveType.None;
            for (int i = 0; i < cands.Count; i++)
            {
                var c = cands[i];
                int ac = targetAggr[c.motive.target.humanID];
                // Compress raw-score gaps (^exponent) so professional/money/feud
                // motives get a real chance against dominant infidelity scores...
                float w = (float)Math.Pow(Math.Max(0f, c.motive.score), WeightExponent);
                // ...boost red-herring-rich victims...
                w *= (1f + RedHerringBonusPer * (ac - 1));
                // ...and discourage repeating the motive type from the last murder.
                if (c.motive.type == lastType) w *= SameTypePenalty;
                c.weight = w;
                cands[i] = c;
            }
            cands.Sort((x, y) => y.weight.CompareTo(x.weight));

            int pool = Math.Min(TopPoolSize, cands.Count);
            float total = 0f;
            for (int i = 0; i < pool; i++) total += cands[i].weight;
            if (total <= 0f) return false;

            double roll = _rng.NextDouble() * total;
            float acc = 0f; int idx = 0;
            for (int i = 0; i < pool; i++) { acc += cands[i].weight; if (roll <= acc) { idx = i; break; } }

            murderer = cands[idx].aggressor;
            victim = cands[idx].motive.target;
            motive = cands[idx].motive;

            // Record for variety + signature stripping.
            _recentTypes.Insert(0, motive.type);
            if (_recentTypes.Count > 3) _recentTypes.RemoveAt(_recentTypes.Count - 1);
            OverriddenVictimIds.Add(victim.humanID);
            MotiveByVictim[victim.humanID] = motive;

            int aggrOnVictim = targetAggr[victim.humanID];
            MotivesPlugin.Log.LogInfo($"[SODMotives] selector: {cands.Count} motivated citizens; picked rank {idx + 1}/{pool} [{motive.type}]; victim has {aggrOnVictim} motivated enemy(ies).");
            return true;
        }
    }
}

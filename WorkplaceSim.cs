using System;
using System.Collections.Generic;

namespace SODMotives
{
    // Workplace-rivalry cases are built ON DEMAND from the city's LIVE company rosters — the real
    // coworker/boss relationships the game DOES model — never pre-seeded, because the game
    // simulates no promotions/firings. The murder director calls CandidateEvents() while choosing
    // a victim and merges the result into the mixed suspect pool; only the CHOSEN case's event is
    // materialized (added to the store + gossiped) so interrogation works. Two shapes, each
    // guaranteeing multiple REAL suspects straight from the roster (so "1 suspect" can't happen):
    //   Promotion — a promotee (or the promoting boss) is killed; the passed-over coworkers are suspects.
    //   Layoffs   — a boss is killed; the employees on the redundancy list are suspects.
    internal static class WorkplaceSim
    {
        internal static bool Enable = true;
        internal static int MaxSuspects = 5;          // cap suspects per workplace case
        internal static float PromotionShare = 0.4f;  // of workplace cases where both are possible, fraction that are promotions (rest = layoffs)

        private static readonly Random _rng = new Random();

        // Build (but DON'T store) one workplace case per eligible company from the live roster.
        // Returns ephemeral SocialEvents (id==0); the selector materializes only the one it picks.
        internal static List<SocialEvent> CandidateEvents()
        {
            var outList = new List<SocialEvent>();
            if (!Enable) return outList;
            int minSus = Math.Max(1, MurderSelector.MinSuspects);
            try
            {
                var city = CityData.Instance;
                if (city == null || city.citizenDirectory == null) return outList;
                var cits = city.citizenDirectory;

                // Distinct companies from employed citizens (dedupe by companyID).
                var companies = new Dictionary<int, Company>();
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = cits[i];
                    if (h == null) continue;
                    Company co = null;
                    try { if (h.job != null) co = h.job.employer; } catch { }
                    if (co == null) continue;
                    int cid; try { cid = co.companyID; } catch { continue; }
                    if (!companies.ContainsKey(cid)) companies[cid] = co;
                }

                foreach (var kv in companies)
                {
                    try
                    {
                        int cid = kv.Key;
                        if (MurderSelector.UsedWorkplaceCompanies.Contains(cid)) continue;   // one workplace case per company per game
                        Company co = kv.Value;
                        Human director = null;
                        try { director = co.director; } catch { }
                        if (!Alive(director)) director = null;
                        if (director == null) continue;   // a workplace case needs a boss victim/decider

                        // Live reports = employees with a Human, alive, excluding the director.
                        var reports = new List<Human>();
                        try
                        {
                            var roster = co.companyRoster;
                            if (roster != null)
                                for (int i = 0; i < roster.Count; i++)
                                {
                                    var occ = roster[i];
                                    if (occ == null) continue;
                                    Human emp = SafeEmployee(occ);
                                    if (!Alive(emp)) continue;
                                    if (emp.humanID == director.humanID) continue;
                                    reports.Add(emp);
                                }
                        }
                        catch { }
                        if (reports.Count < minSus) continue;   // not enough coworkers for a rich pool

                        string coName = "work";
                        try { if (!string.IsNullOrEmpty(co.name)) coName = co.name; } catch { }

                        int cap = Math.Max(MaxSuspects, minSus);   // never cap below the >= MinSuspects floor
                        bool canPromote = reports.Count >= minSus + 1;   // 1 promotee + >= minSus rivals
                        if (canPromote && _rng.NextDouble() < PromotionShare)
                        {
                            // PROMOTION: a random report got promoted; the rest are passed-over rivals.
                            var shuffled = RandomSubset(reports, reports.Count);
                            Human promotee = shuffled[0];
                            var rivals = new List<Human>();
                            for (int i = 1; i < shuffled.Count && rivals.Count < cap; i++) rivals.Add(shuffled[i]);
                            var e = new SocialEvent { type = SocialEventType.Promotion, a = promotee, b = director, placeName = coName, time = 0f, companyId = cid };
                            e.group.AddRange(rivals);
                            outList.Add(e);
                        }
                        else
                        {
                            // LAYOFFS: the boss is the victim; a sample of reports are on the redundancy list.
                            var laid = reports.Count > cap ? RandomSubset(reports, cap) : reports;
                            var e = new SocialEvent { type = SocialEventType.Layoffs, a = director, b = null, placeName = coName, time = 0f, companyId = cid };
                            e.group.AddRange(laid);
                            outList.Add(e);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { MotivesPlugin.Log.LogWarning($"[SODMotives][events] workplace candidates error: {ex.Message}"); }
            return outList;
        }

        private static bool Alive(Human h)
        {
            if (h == null) return false;
            try { if (h.isDead) return false; } catch { }
            return true;
        }

        private static List<Human> RandomSubset(List<Human> src, int n)
        {
            var copy = new List<Human>(src);
            int take = Math.Min(n, copy.Count);
            for (int i = 0; i < take; i++)   // partial Fisher-Yates
            {
                int j = i + _rng.Next(copy.Count - i);
                var tmp = copy[i]; copy[i] = copy[j]; copy[j] = tmp;
            }
            if (take == copy.Count) return copy;
            var outList = new List<Human>();
            for (int i = 0; i < take; i++) outList.Add(copy[i]);
            return outList;
        }

        private static Human SafeEmployee(Occupation occ)
        {
            if (occ == null) return null;
            try { return occ.employee; } catch { return null; }
        }
    }
}

using System;
using System.Collections.Generic;

namespace SODMotives
{
    // Landlord<->tenant cases are built ON DEMAND from the city's LIVE residency graph — the real
    // landlord/tenant relationships the game DOES model (Human.GetLandlord()) — never pre-seeded,
    // because the game simulates no rent/eviction. The murder director calls CandidateEvents() while
    // choosing a victim and merges the result into the mixed suspect pool; only the CHOSEN case's
    // event is materialized (added to the store + gossiped) so interrogation works. Two shapes:
    //   Eviction    — a landlord is killed; the tenants they're clearing out (redevelopment) are the
    //                 suspects (one-owner -> many-tenants, a Layoffs parallel with a real anchor).
    //   RentArrears — a tenant is killed; their landlord (fed up with unpaid rent) is the lone
    //                 suspect (a one-to-one motive that mainly enriches a tenant's MIXED pool).
    //
    // NOTE (verified this session): NewBuilding has no real `owner` property (the decompiled `owner`
    // is a lambda-closure field), so the landlord is resolved PER RESIDENT via Human.GetLandlord().
    internal static class PropertySim
    {
        internal static bool Enable = true;
        internal static int MaxTenantSuspects = 6;        // cap eviction suspects per building
        internal static float EvictionShare = 0.6f;       // of eligible buildings, fraction that are eviction (rest = rent arrears)

        private static readonly Random _rng = new Random();

        // Build (but DON'T store) one property case per eligible building from the live residency
        // graph. Returns ephemeral SocialEvents (id==0); the selector materializes only the one it picks.
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

                // Group alive residents by their home building, and cache each one's landlord.
                var byBuilding = new Dictionary<int, List<Human>>();
                var landlordOf = new Dictionary<int, Human>();   // humanID -> their landlord (cached; GetLandlord isn't free)
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = cits[i];
                    if (!Alive(h)) continue;
                    NewAddress home = null; try { home = h.home; } catch { }
                    NewBuilding b = null; try { b = home != null ? home.building : null; } catch { }
                    if (b == null) continue;
                    int bid; try { bid = b.buildingID; } catch { continue; }
                    if (!byBuilding.TryGetValue(bid, out var list)) { list = new List<Human>(); byBuilding[bid] = list; }
                    list.Add(h);
                    Human ll = null; try { ll = h.GetLandlord(); } catch { }
                    landlordOf[h.humanID] = ll;
                }

                var landlordsUsed = new HashSet<int>();   // dedupe eviction candidates per landlord (a landlord can own several buildings)

                foreach (var kv in byBuilding)
                {
                    try
                    {
                        int bid = kv.Key;
                        if (MurderSelector.UsedBuildings.Contains(bid)) continue;   // one property case per building per game
                        var residents = kv.Value;

                        // The building's landlord = the (first non-null, shared) landlord of its residents.
                        Human landlord = null;
                        for (int i = 0; i < residents.Count && landlord == null; i++)
                        {
                            Human ll = null; landlordOf.TryGetValue(residents[i].humanID, out ll);
                            if (Alive(ll)) landlord = ll;
                        }
                        if (landlord == null) continue;   // player-owned / no landlord — skip

                        // Tenants = residents whose landlord is this one, excluding the landlord themselves.
                        var tenants = new List<Human>();
                        for (int i = 0; i < residents.Count; i++)
                        {
                            Human t = residents[i];
                            if (t == null || t.humanID == landlord.humanID) continue;
                            Human tl = null; landlordOf.TryGetValue(t.humanID, out tl);
                            if (tl != null && tl.humanID == landlord.humanID) tenants.Add(t);
                        }
                        if (tenants.Count == 0) continue;

                        string place = PlaceName(residents);

                        bool canEvict = tenants.Count >= minSus && !landlordsUsed.Contains(landlord.humanID);
                        if (canEvict && _rng.NextDouble() < EvictionShare)
                        {
                            // EVICTION: the landlord is killed; a sample of tenants being cleared out are suspects.
                            int cap = Math.Max(MaxTenantSuspects, minSus);
                            var grp = tenants.Count > cap ? RandomSubset(tenants, cap) : tenants;
                            var e = new SocialEvent { type = SocialEventType.Eviction, a = landlord, b = null, placeName = place, time = 0f, companyId = -1, buildingId = bid };
                            e.group.AddRange(grp);
                            outList.Add(e);
                            landlordsUsed.Add(landlord.humanID);
                        }
                        else
                        {
                            // RENT ARREARS: one random tenant is killed; the landlord is the lone suspect.
                            Human victimTenant = tenants[_rng.Next(tenants.Count)];
                            var e = new SocialEvent { type = SocialEventType.RentArrears, a = victimTenant, b = landlord, placeName = place, time = 0f, companyId = -1, buildingId = bid };
                            outList.Add(e);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { MotivesPlugin.Log.LogWarning($"[SODMotives][events] property candidates error: {ex.Message}"); }
            return outList;
        }

        private static bool Alive(Human h)
        {
            if (h == null) return false;
            try { if (h.isDead) return false; } catch { }
            return true;
        }

        // A readable name for the building — a representative resident's home address, else a generic.
        private static string PlaceName(List<Human> residents)
        {
            if (residents != null)
                for (int i = 0; i < residents.Count; i++)
                {
                    try { var home = residents[i].home; if (home != null && !string.IsNullOrEmpty(home.name)) return home.name; }
                    catch { }
                }
            return "their building";
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
    }
}

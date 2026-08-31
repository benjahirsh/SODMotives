using System;
using System.Collections.Generic;
using HarmonyLib;

namespace SODMotives
{
    // Seeds the event store from the city's existing affairs (paramour links) and
    // distributes knowledge to some acquaintances, so interrogation yields real leads
    // from the very start. New-affair generation + gossip come in later steps.
    internal static class AffairSim
    {
        internal static void SeedForNewGame()
        {
            try
            {
                EventStore.Clear();
                var city = CityData.Instance;
                if (city == null || city.citizenDirectory == null) return;
                var cits = city.citizenDirectory;
                int n = cits.Count;

                var donePairs = new HashSet<long>();
                int affairs = 0;

                for (int i = 0; i < n; i++)
                {
                    Human c = cits[i];
                    if (c == null) continue;
                    Human lover = null;
                    try { lover = c.paramour; } catch { }
                    if (lover == null) continue;

                    long key = PairKey(c.humanID, lover.humanID);
                    if (!donePairs.Add(key)) continue;   // count each affair once

                    var e = new SocialEvent
                    {
                        type = SocialEventType.Affair,
                        a = c,
                        b = lover,
                        placeName = PlaceHint(c, lover),
                        time = 0f,
                    };
                    Gossip.Distribute(e);   // participants + their neighbours/coworkers/friends
                    EventStore.Add(e);
                    affairs++;
                }

                MotivesPlugin.Log.LogInfo($"[SODMotives][events] seeded {affairs} affairs; {EventStore.Count} events; knowledge distributed to neighbours/coworkers.");
            }
            catch (Exception ex) { MotivesPlugin.Log.LogWarning($"[SODMotives][events] seed error: {ex}"); }
        }

        private static string PlaceHint(Human a, Human b)
        {
            try { if (b != null && b.home != null) return b.home.name; } catch { }
            try { if (a != null && a.home != null) return a.home.name; } catch { }
            return "";
        }

        private static long PairKey(int x, int y)
        {
            int lo = Math.Min(x, y), hi = Math.Max(x, y);
            return ((long)lo << 32) | (uint)hi;
        }
    }

    // Seed once per game start.
    [HarmonyPatch(typeof(MurderController), nameof(MurderController.OnStartGame))]
    internal static class Patch_OnStartGame_Seed
    {
        static void Postfix()
        {
            try { AffairSim.SeedForNewGame(); }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][events] OnStartGame seed: {e.Message}"); }
        }
    }
}

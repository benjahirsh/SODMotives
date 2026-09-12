using System;
using System.Collections.Generic;

namespace SODMotives
{
    // One-to-one PERSONAL motives (V2.4): feuds and debts. Unlike workplace/property (built on demand
    // from real rosters/residency), these have NO game anchor — the game models no grudges or money —
    // so they are fully SYNTHESIZED and SEEDED at new-game, exactly like affairs: added to the
    // EventStore as persistent world texture, witnessed by the pair's social circle, and surfaced by
    // interrogation + an anonymous handwritten threat-note clue.
    //
    // Grounding (so the fabrications still feel real and are player-followable):
    //   * Feud — seeded from a genuinely SOURED directed edge (Acquaintance.like < Motive.SouredLike):
    //             two adults who really do dislike each other in-game. Bidirectional (either kills).
    //   * Debt — seeded from a real SOCIAL acquaintance edge (friend/neighbour/coworker they actually
    //             know, known >= NameKnownThreshold): a plausible lender/borrower pair. One-directional
    //             (creditor kills the debtor who stopped paying — mirrors RentArrears, reuses its clue).
    //
    // Seeded from AffairSim.SeedForNewGame (the single new-game/reseed entry point that already
    // clears the store + resets the per-case maps), so feuds/debts ride the same lifecycle as affairs.
    internal static class FeudSim
    {
        internal static bool EnableFeuds = true;
        internal static bool EnableDebts = true;
        internal static int MaxFeuds = 40;   // cap synthesized feuds per city (avoid swamping affairs)
        internal static int MaxDebts = 40;   // cap synthesized debts per city

        private static readonly Random _rng = new Random();

        // Append seeded feud + debt events to the store. Assumes EventStore was already cleared by the
        // caller (AffairSim.SeedForNewGame). Dedupes pairs across BOTH types (a pair gets at most one
        // synthesized motive), and never reuses a pair already carrying a seeded motive.
        internal static void Seed()
        {
            try
            {
                var city = CityData.Instance;
                if (city == null || city.citizenDirectory == null) return;
                var cits = city.citizenDirectory;
                int n = cits.Count;

                int playerId = -1;
                try { if (Player.Instance != null) playerId = Player.Instance.humanID; } catch { }

                var usedPairs = new HashSet<long>();   // pairs already carrying a seeded feud/debt
                var feudCands = new List<(Human a, Human b)>();
                var debtCands = new List<(Human a, Human b)>();

                for (int i = 0; i < n; i++)
                {
                    Human c = cits[i];
                    if (!Valid(c, playerId)) continue;
                    // Per-citizen guard (matches Gossip.AddFor / Motive.CollectTargets): a throw walking one
                    // citizen's acquaintance graph skips only THEM, so seeding continues for the rest of the
                    // city rather than the whole-method catch aborting all feud/debt candidate collection.
                    try
                    {
                        var list = c.acquaintances;
                        if (list == null) continue;
                        int m = list.Count;
                        for (int j = 0; j < m; j++)
                        {
                            var acq = list[j];
                            if (acq == null) continue;
                            Human other = null; try { other = acq.GetOther(c); } catch { }
                            if (!Valid(other, playerId) || other.humanID == c.humanID) continue;

                            // Feud: c genuinely dislikes `other` (directed like soured). c = the resentful
                            // aggressor; the edge is made bidirectional in CollectFeudEdges anyway.
                            if (EnableFeuds)
                            {
                                float like = float.NaN; try { like = acq.like; } catch { }
                                if (!float.IsNaN(like) && like < Motive.SouredLike)
                                    feudCands.Add((c, other));
                            }

                            // Debt: a real social relationship they'd plausibly lend across. Emitted once per
                            // ordered directed edge; direction (who owes whom) is settled at seed time below.
                            if (EnableDebts && IsSocial(acq))
                            {
                                float known = 0f; try { known = acq.known; } catch { }
                                if (known >= Motive.NameKnownThreshold)
                                    debtCands.Add((c, other));
                            }
                        }
                    }
                    catch { }
                }

                int feuds = SeedFrom(feudCands, MaxFeuds, usedPairs, isFeud: true);
                int debts = SeedFrom(debtCands, MaxDebts, usedPairs, isFeud: false);
                MotivesPlugin.Log.LogInfo($"[SODMotives][events] seeded {feuds} feud(s) + {debts} debt(s) from {feudCands.Count} soured / {debtCands.Count} social candidate edge(s).");
            }
            catch (Exception ex) { MotivesPlugin.Log.LogWarning($"[SODMotives][events] feud/debt seed error: {ex}"); }
        }

        // Shuffle `cands`, then take up to `max` whose pair isn't already used, creating + distributing
        // + storing one event each. Feuds are bidirectional (a = the resentful party). Debts pick a
        // random debtor/creditor split so a = debtor (victim) and b = creditor (suspect/writer).
        private static int SeedFrom(List<(Human a, Human b)> cands, int max, HashSet<long> usedPairs, bool isFeud)
        {
            if (max <= 0 || cands.Count == 0) return 0;
            Shuffle(cands);
            int made = 0;
            for (int i = 0; i < cands.Count && made < max; i++)
            {
                var (a, b) = cands[i];
                long key = PairKey(a.humanID, b.humanID);
                if (!usedPairs.Add(key)) continue;   // one synthesized motive per pair

                SocialEvent e;
                if (isFeud)
                {
                    e = new SocialEvent { type = SocialEventType.Feud, a = a, b = b, placeName = "", time = 0f };
                }
                else
                {
                    // Randomly assign who owes whom; a = debtor (victim), b = creditor (suspect/writer).
                    Human debtor = a, creditor = b;
                    if (_rng.Next(2) == 0) { debtor = b; creditor = a; }
                    e = new SocialEvent { type = SocialEventType.Debt, a = debtor, b = creditor, placeName = "", time = 0f };
                }

                Gossip.Distribute(e);   // fill knownBy (social circle + partners) before indexing
                EventStore.Add(e);
                made++;
            }
            return made;
        }

        private static bool Valid(Human h, int playerId)
        {
            if (h == null) return false;
            try { if (h.humanID == playerId) return false; } catch { }
            try { if (h.isDead) return false; } catch { }
            try { if (h.removedFromWorld) return false; } catch { }
            return true;
        }

        // A connection type across which lending money / a personal grudge reads plausibly — i.e. two
        // people who genuinely know each other, not a fleeting public-figure/stranger routing value.
        private static bool IsSocial(Acquaintance acq)
        {
            var c = acq.connections;
            if (c == null) return false;
            for (int i = 0; i < c.Count; i++)
            {
                switch (c[i])
                {
                    case Acquaintance.ConnectionType.friend:
                    case Acquaintance.ConnectionType.neighbor:
                    case Acquaintance.ConnectionType.housemate:
                    case Acquaintance.ConnectionType.workTeam:
                    case Acquaintance.ConnectionType.workOther:
                    case Acquaintance.ConnectionType.familiarResidence:
                    case Acquaintance.ConnectionType.familiarWork:
                        return true;
                }
            }
            return false;
        }

        private static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                var tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }

        private static long PairKey(int x, int y)
        {
            int lo = Math.Min(x, y), hi = Math.Max(x, y);
            return ((long)lo << 32) | (uint)hi;
        }
    }
}

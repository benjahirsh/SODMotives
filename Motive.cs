using System;
using System.Collections.Generic;

namespace SODMotives
{
    internal enum MotiveType { None, Infidelity, Professional, Money, PersonalFeud }

    internal struct MotiveResult
    {
        public Human target;
        public float score;
        public MotiveType type;
        public string detail;   // human-readable, for logging + later evidence text

        public bool HasMotive => type != MotiveType.None && score > 0f;
        public override string ToString() =>
            target == null ? "<none>"
            : $"{MotivesPlugin.Name(target)}  score={MotivesPlugin.F(score)}  [{type}]  {detail}";
    }

    // Derives directed motive (A wants to harm B) from the game's existing social
    // graph. Structural situations (affairs, workplace, landlord) are the primary
    // driver; the game's directed `like` value is an amplifier + a catch-all
    // "bad blood" signal for the rare genuinely-negative tail.
    internal static class Motive
    {
        // From calibration: mean like ~0.58, range ~0.26..0.81. Neutral point:
        internal const float Neutral = 0.58f;
        // Below this, a relationship reads as genuinely soured:
        internal const float SouredLike = 0.45f;
        // Minimum total score to count as a real motive:
        internal const float MinMotive = 12f;

        internal static bool Same(Human a, Human b)
            => a != null && b != null && a.humanID == b.humanID;

        // Directed like A->B, or NaN if no edge.
        internal static float Like(Human a, Human b)
        {
            if (a == null || b == null) return float.NaN;
            try
            {
                Acquaintance acq;
                if (a.FindAcquaintanceExists(b, out acq) && acq != null) return acq.like;
            }
            catch { }
            return float.NaN;
        }

        // 0 (neutral/positive) .. ~32 (deep dislike), from directed like.
        internal static float LikeAmp(float like)
            => float.IsNaN(like) ? 0f : Math.Max(0f, (Neutral - like)) * 100f;

        internal static bool EdgeHasConn(Human a, Human b, Acquaintance.ConnectionType type, out Acquaintance edge)
        {
            edge = null;
            try
            {
                Acquaintance acq;
                if (a.FindAcquaintanceExists(b, out acq) && acq != null)
                {
                    edge = acq;
                    var conns = acq.connections;
                    if (conns != null)
                        for (int i = 0; i < conns.Count; i++)
                            if (conns[i] == type) return true;
                }
            }
            catch { }
            return false;
        }

        internal static bool SameEmployer(Human a, Human b)
        {
            try
            {
                var ja = a.job; var jb = b.job;
                if (ja == null || jb == null) return false;
                var ea = ja.employer; var eb = jb.employer;
                if (ea == null || eb == null) return false;
                return !string.IsNullOrEmpty(ea.name) && ea.name == eb.name;
            }
            catch { return false; }
        }

        // Score A's motive to harm B. Returns type None if nothing meaningful.
        internal static MotiveResult Score(Human a, Human b)
        {
            var r = new MotiveResult { target = b, type = MotiveType.None, score = 0f, detail = "" };
            if (Same(a, b)) return r;

            float like = Like(a, b);
            float amp = LikeAmp(like);

            // Collect structural candidates (base, type, detail).
            MotiveType bestType = MotiveType.None; float bestBase = 0f; string bestDetail = "";
            void Consider(float bse, MotiveType t, string d)
            { if (bse > bestBase) { bestBase = bse; bestType = t; bestDetail = d; } }

            // ---- Infidelity / love-triangle ----
            try
            {
                Human aPartner = a.partner;   // A's spouse/partner
                Human aParamour = a.paramour; // A's secret affair partner

                // B is A's partner, and B is cheating (has a paramour).
                // (Victim = the live-in partner -> murder at the shared home.)
                if (Same(aPartner, b) && b.paramour != null)
                    Consider(116f, MotiveType.Infidelity,
                        $"your partner {MotivesPlugin.Name(b)} is having an affair with {MotivesPlugin.Name(b.paramour)}");

                // B is the person A's partner is cheating with (the interloper).
                // (Victim = the lover, at THEIR place -> different scene, less obvious killer.)
                // Kept close to the above so scene/suspect variety is ~50/50.
                if (aPartner != null && Same(aPartner.paramour, b))
                    Consider(114f, MotiveType.Infidelity,
                        $"{MotivesPlugin.Name(b)} is sleeping with your partner {MotivesPlugin.Name(aPartner)}");

                // A and B are secret affair partners -> volatile, crime-of-passion.
                if (Same(aParamour, b))
                    Consider(70f, MotiveType.Infidelity,
                        $"{MotivesPlugin.Name(b)} is your secret affair partner");

                // B is the betrayed partner of A's affair partner (B could expose/threaten A).
                if (aParamour != null && Same(aParamour.partner, b))
                    Consider(65f, MotiveType.Infidelity,
                        $"{MotivesPlugin.Name(b)} is the partner of your lover {MotivesPlugin.Name(aParamour)}");

                // Soured lovers (current lover connection but low like).
                Acquaintance loverEdge;
                if (EdgeHasConn(a, b, Acquaintance.ConnectionType.lover, out loverEdge) && like < SouredLike)
                    Consider(50f + amp, MotiveType.Infidelity,
                        $"your relationship with {MotivesPlugin.Name(b)} has soured (like {MotivesPlugin.F(like)})");
            }
            catch { }

            // ---- Professional ----
            try
            {
                if (SameEmployer(a, b) && like < Neutral)
                {
                    Acquaintance bossEdge;
                    bool bIsBoss = EdgeHasConn(a, b, Acquaintance.ConnectionType.boss, out bossEdge);
                    float gap = 0f;
                    try { gap = Math.Max(0f, b.job.salary - a.job.salary); } catch { }
                    float p = 18f + (bIsBoss ? 34f : 0f) + Math.Min(20f, gap * 0.1f);
                    string company = "";
                    try { company = a.job.employer.name; } catch { }
                    Consider(p, MotiveType.Professional,
                        bIsBoss ? $"{MotivesPlugin.Name(b)} is your boss at {company}"
                                : $"workplace friction with {MotivesPlugin.Name(b)} at {company}");
                }
            }
            catch { }

            // ---- Money (landlord/rent as a V1 proxy) ----
            try
            {
                Acquaintance llEdge;
                if (EdgeHasConn(a, b, Acquaintance.ConnectionType.landlord, out llEdge) && like < Neutral)
                    Consider(24f + amp * 0.5f, MotiveType.Money,
                        $"{MotivesPlugin.Name(b)} is your landlord (money/rent)");
            }
            catch { }

            // ---- Compose ----
            if (bestType != MotiveType.None)
            {
                // Structural motive dominates; like amplifies it.
                r.type = bestType;
                r.detail = bestDetail;
                r.score = bestBase + amp;
                return r;
            }

            // No structural motive: fall back to the rare "bad blood" tail.
            if (like < SouredLike && amp > 0f)
            {
                r.type = MotiveType.PersonalFeud;
                r.detail = $"bad blood with {MotivesPlugin.Name(b)} (like {MotivesPlugin.F(like)})";
                r.score = amp;
                return r;
            }

            return r; // None
        }

        // Collect EVERY motivated target for A (partner, paramour, their cross-links,
        // and all acquaintances). Emitting all pairs — not just the top one — lets the
        // selector pick "kill the lover" / professional / money cases, not only
        // "betrayed partner kills the cohabiting cheater".
        internal static void CollectTargets(Human a, List<MotiveResult> outList)
        {
            if (a == null) return;
            var seen = new HashSet<int>();

            void Try(Human b)
            {
                if (b == null || Same(a, b) || !seen.Add(b.humanID)) return;
                var m = Score(a, b);
                if (m.HasMotive) outList.Add(m);
            }

            try
            {
                Try(a.partner);
                Try(a.paramour);
                if (a.partner != null) Try(a.partner.paramour);
                if (a.paramour != null) Try(a.paramour.partner);

                var list = a.acquaintances;
                if (list != null)
                {
                    int n = list.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var acq = list[i];
                        if (acq == null) continue;
                        Try(acq.GetOther(a));
                    }
                }
            }
            catch { }
        }

        // A's single strongest motive target (used by the F9 shortlist / diagnostics).
        internal static MotiveResult BestTarget(Human a)
        {
            var all = new List<MotiveResult>();
            CollectTargets(a, all);
            var best = new MotiveResult { type = MotiveType.None, score = 0f };
            foreach (var m in all) if (m.score > best.score) best = m;
            return best;
        }
    }
}

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppList = Il2CppSystem.Collections.Generic.List<Acquaintance>;

namespace SODMotives
{
    // Observation-only build: hooks the murder pipeline and logs real behavior.
    // It intentionally changes NOTHING in the game yet — this is how we lock the
    // design to reality (call order, whether victim depends on murderer, and the
    // actual range/meaning of Acquaintance.like) before writing the override.
    [BepInPlugin(Guid, "SOD Motives", "1.0.0")]
    public class MotivesPlugin : BasePlugin
    {
        public const string Guid = "com.benhirsh.sodmotives";
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        public override void Load()
        {
            Log = base.Log;
            Banner();

            // Config (BepInEx/config/com.benhirsh.sodmotives.cfg)
            MurderSelector.EnableOverride = Config.Bind("General", "EnableOverride", true,
                "Replace vanilla killer/victim selection with a motivated pair from the social graph.").Value;
            // V2.1 victim-centric selector knobs.
            MurderSelector.MinSuspects = Config.Bind("Selection", "MinSuspects", 3,
                "PREFER victims with at least this many real event-backed suspects. If none qualify, degrade to the richest available victim (never vanilla for the floor).").Value;
            MurderSelector.KillerPoolSize = Config.Bind("Selection", "KillerPoolSize", 10,
                "The real killer is picked uniformly at random from the victim's top-N strongest suspects.").Value;
            MurderSelector.WorkplaceCaseShare = Config.Bind("Selection", "WorkplaceCaseShare", 0.5f,
                "When the F6 force is OFF: target fraction of mod cases that feature a workplace motive, so the far-more-numerous affairs don't swamp workplace. 0 = affairs only, 1 = workplace only.").Value;
            Motive.NameKnownThreshold = Config.Bind("Selection", "NameKnownThreshold", 0.35f,
                "Minimum directed familiarity (Acquaintance.known, 0..1) for an NPC to count as knowing a person's NAME (able to identify their photo). Gates interrogation gossip + the F9 knower list. Real relationships sit ~0.6-0.9; casual acquaintances ~0.1-0.2.").Value;
            // Legacy V1/V2.0 knobs — kept bound so existing .cfg files don't break; no longer
            // consulted by the victim-centric selector. Pruned in release cleanup.
            MurderSelector.TopPoolSize = Config.Bind("Selection", "TopPoolSize", 40,
                "(Legacy, unused) Weighted-random pick was drawn from this many of the strongest feuds.").Value;
            MurderSelector.RedHerringBonusPer = Config.Bind("Selection", "RedHerringBonusPer", 0.4f,
                "(Legacy, unused) Selection weight bonus per EXTRA motivated enemy the victim has.").Value;
            MurderSelector.WeightExponent = Config.Bind("Selection", "WeightExponent", 0.6f,
                "(Legacy, unused) Below 1.0 compressed score gaps so weaker motives surfaced.").Value;
            MurderSelector.SameTypePenalty = Config.Bind("Selection", "SameTypePenalty", 0.4f,
                "(Legacy, unused) Weight multiplier for a motive type used in the previous murder.").Value;
            MurderSelector.StripSignatures = Config.Bind("Flavour", "StripSignatures", true,
                "Remove serial-killer calling card/moniker/graffiti from motivated cases so they read as personal crimes.").Value;
            MurderSelector.VanillaCaseEvery = Config.Bind("Flavour", "VanillaCaseEvery", 0,
                "Force a full vanilla serial-killer case every Nth case (deterministic). 0 = never force vanilla (testing: every case is a mod case). Production suggestion: 3-4.").Value;
            // Workplace-rivalry cases (V2.1) — built on-demand from live company rosters at murder time.
            WorkplaceSim.Enable = Config.Bind("Workplace", "EnableWorkplace", true,
                "Add workplace-rivalry cases (promotions + layoffs) as murder-motive sources, derived on-demand from real company rosters.").Value;
            WorkplaceSim.MaxSuspects = Config.Bind("Workplace", "MaxSuspects", 5,
                "Maximum suspects per workplace case (passed-over rivals / employees on the layoff list).").Value;
            WorkplaceSim.PromotionShare = Config.Bind("Workplace", "PromotionShare", 0.4f,
                "Of workplace cases where both are possible, the fraction that are promotions; the rest are layoffs (a boss-victim, most reliably multi-suspect).").Value;
            // Landlord/tenant (property) cases (V2.3) — built on-demand from the live residency graph.
            PropertySim.Enable = Config.Bind("Property", "EnableProperty", true,
                "Add landlord/tenant cases (eviction/redevelopment + rent arrears) as murder-motive sources, derived on-demand from real landlord/tenant relationships.").Value;
            PropertySim.MaxTenantSuspects = Config.Bind("Property", "MaxTenantSuspects", 6,
                "Maximum tenant suspects per eviction case (the aggrieved tenants being cleared out for redevelopment).").Value;
            MurderSelector.PropertyCaseShare = Config.Bind("Property", "PropertyCaseShare", 0.25f,
                "When the F6 force is OFF: target fraction of mod cases that feature a landlord/property motive (affair = the remainder after workplace + property).").Value;
            PropertySim.EvictionShare = Config.Bind("Property", "EvictionShare", 0.6f,
                "Of eligible buildings, the fraction whose case is an eviction (landlord victim, many tenant suspects); the rest are rent-arrears (a tenant victim, the landlord suspect).").Value;

            ClueInjector.Enable = Config.Bind("Clues", "InjectClues", true,
                "Inject deliberately-ambiguous physical notes per motivated case, hinting at motives.").Value;
            ClueInjector.MaxClues = Config.Bind("Clues", "MaxCluesPerCase", 8,
                "Safety cap on total motive clues per case (one per event-suspect: love letter / promotion letter + rival threats / termination notices).").Value;
            ClueInjector.ObviousNames = Config.Bind("Clues", "ObviousTestNames", true,
                "TESTING: rename injected notes to 'MODCLUE ...' so they're easy to find. Set false for normal play.").Value;
            ClueInjector.FingerprintChance = Config.Bind("Clues", "FingerprintChance", 0.7f,
                "Chance (0..1) a note carries the author's fingerprints. Below that, it's traceable only by handwriting.").Value;
            ClueInjector.WorkplaceClueShare = Config.Bind("Clues", "WorkplaceClueShare", 0.5f,
                "Chance (0..1) a given clue is placed at the victim's WORKPLACE rather than home. Motive-agnostic: any motive's clue can land at either, so location never betrays the motive.").Value;

            // TESTING DEFAULT: force the first (and every) new murder to a landlord/property (eviction)
            // case so V2.3 is fast to test. Cycle in-game with F6 (off / affair / professional / money).
            // SET TO MotiveType.None FOR RELEASE.
            DebugTools.ForceMotiveType = MotiveType.Money;

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(MotivesPlugin).Assembly);
            DebugTools.Register();
            Persistence.Register();   // save/reload replay of custom notes (drives the replay poll)
            Log.LogInfo($"[SODMotives] Patches applied. Override = {(MurderSelector.EnableOverride ? "ON (motivated murders)" : "OFF (observation only)")}.");
        }

        private void Banner()
        {
            Log.LogInfo("========================================================");
            Log.LogInfo("==  SOD MOTIVES MOD  —  v1.0.0                        ==");
            Log.LogInfo("==  If you can read this, the mod is LOADED & ACTIVE.  ==");
            Log.LogInfo("==  Murders will be driven by NPC relationships.       ==");
            Log.LogInfo("========================================================");
        }

        // ---- logging helpers -------------------------------------------------

        // Pre-format floats to avoid interpolation-handler ambiguity from interop refs.
        internal static string F(float x) => float.IsNaN(x) ? "NaN" : x.ToString("0.00");

        internal static string Name(Human h)
        {
            if (h == null) return "<null>";
            try { return $"{h.citizenName}#{h.humanID}"; }
            catch { return "<name-error>"; }
        }

        // Directed like from a -> b (how A feels about B). Returns NaN if no edge.
        internal static float DirectedLike(Human a, Human b)
        {
            if (a == null || b == null) return float.NaN;
            try
            {
                Acquaintance acq;
                if (a.FindAcquaintanceExists(b, out acq) && acq != null)
                    return acq.like;
            }
            catch (Exception e) { Log.LogWarning($"[SODMotives] DirectedLike error: {e.Message}"); }
            return float.NaN;
        }

        // Log a human's most-negative acquaintances (the "who might resent them" landscape).
        internal static void LogTopNegative(string label, Human h, int topN = 6)
        {
            if (h == null) { Log.LogInfo($"[SODMotives]   {label}: <null>"); return; }
            try
            {
                Il2CppList list = h.acquaintances;
                if (list == null) { Log.LogInfo($"[SODMotives]   {label}: no acquaintance list"); return; }

                var rows = new List<(string name, float like, string conns)>();
                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    Acquaintance a = list[i];
                    if (a == null) continue;
                    Human other = a.GetOther(h);
                    if (other == null) continue;
                    rows.Add((Name(other), a.like, ConnSummary(a)));
                }
                rows.Sort((x, y) => x.like.CompareTo(y.like)); // most negative first

                Log.LogInfo($"[SODMotives]   {label}: {rows.Count} acquaintances. Most-negative:");
                int shown = Math.Min(topN, rows.Count);
                for (int i = 0; i < shown; i++)
                    Log.LogInfo($"[SODMotives]       like={F(rows[i].like).PadLeft(7)}  {rows[i].name}  [{rows[i].conns}]");
            }
            catch (Exception e) { Log.LogWarning($"[SODMotives]   {label}: acq-scan error: {e.Message}"); }
        }

        internal static string ConnSummary(Acquaintance a)
        {
            try
            {
                var conns = a.connections;
                if (conns == null || conns.Count == 0) return $"secret:{a.secretConnection}";
                var parts = new List<string>();
                for (int i = 0; i < conns.Count; i++) parts.Add(conns[i].ToString());
                return string.Join(",", parts);
            }
            catch { return "?"; }
        }

        // One-line description of a human's motive-relevant attributes.
        internal static void Describe(string label, Human h)
        {
            if (h == null) { Log.LogInfo($"[SODMotives]   {label}: <null>"); return; }
            try
            {
                string partner = h.partner != null ? Name(h.partner) : "-";
                string paramour = h.paramour != null ? Name(h.paramour) : "-";
                string job = "-";
                try
                {
                    var occ = h.job;
                    if (occ != null)
                    {
                        string emp = occ.employer != null ? occ.employer.name : "?";
                        job = $"{occ.name} @ {emp} (salary {F(occ.salary)})";
                    }
                }
                catch { }
                Log.LogInfo($"[SODMotives]   {label}: {Name(h)}  partner={partner}  paramour={paramour}  job={job}");
            }
            catch (Exception e) { Log.LogWarning($"[SODMotives]   {label}: describe error: {e.Message}"); }
        }
    }

    // One-time city-wide calibration report: real like distribution + strongest
    // motive structures. Runs once, on the first observed murder.
    internal static class CityScan
    {
        private static bool _done;

        public static void RunOnce()
        {
            if (_done) return;
            _done = true;
            var log = MotivesPlugin.Log;
            try
            {
                var city = CityData.Instance;
                if (city == null || city.citizenDirectory == null) { log.LogWarning("[SODMotives] CityScan: no city/citizens."); return; }
                var cits = city.citizenDirectory;
                int n = cits.Count;
                log.LogInfo("[SODMotives] ############ CITY MOTIVE CALIBRATION ############");
                log.LogInfo($"[SODMotives]   citizens = {n}");

                // ---- like distribution over all directed edges ----
                int edges = 0;
                float min = float.MaxValue, max = float.MinValue, sum = 0f;
                int[] buckets = new int[10]; // 0.0..1.0 in 0.1 steps
                int below35 = 0, below30 = 0, below25 = 0;
                var worst = new List<(float like, string desc)>();
                var affairs = new List<string>();

                for (int i = 0; i < n; i++)
                {
                    Human c = cits[i];
                    if (c == null) continue;

                    // affair / love-triangle detection
                    try
                    {
                        if (c.paramour != null)
                        {
                            string betrayed = c.partner != null ? MotivesPlugin.Name(c.partner) : "(no partner)";
                            affairs.Add($"{MotivesPlugin.Name(c)} has paramour {MotivesPlugin.Name(c.paramour)}  | betrayed partner: {betrayed}");
                        }
                    }
                    catch { }

                    var list = c.acquaintances;
                    if (list == null) continue;
                    int m = list.Count;
                    for (int j = 0; j < m; j++)
                    {
                        Acquaintance a = list[j];
                        if (a == null) continue;
                        float lk = a.like;
                        if (float.IsNaN(lk)) continue;
                        edges++; sum += lk;
                        if (lk < min) min = lk;
                        if (lk > max) max = lk;
                        int b = (int)(lk * 10f); if (b < 0) b = 0; if (b > 9) b = 9; buckets[b]++;
                        if (lk < 0.35f) below35++;
                        if (lk < 0.30f) below30++;
                        if (lk < 0.25f) below25++;
                        if (lk < 0.38f)
                        {
                            Human other = a.GetOther(c);
                            worst.Add((lk, $"{MotivesPlugin.Name(c)} -> {MotivesPlugin.Name(other)}  [{MotivesPlugin.ConnSummary(a)}]"));
                        }
                    }
                }

                log.LogInfo($"[SODMotives]   directed edges = {edges}  like: min={MotivesPlugin.F(min)} max={MotivesPlugin.F(max)} mean={MotivesPlugin.F(edges>0?sum/edges:0f)}");
                var sb = new System.Text.StringBuilder();
                for (int b = 0; b < 10; b++) sb.Append($"[{b/10.0:0.0}-{(b+1)/10.0:0.0}):{buckets[b]} ");
                log.LogInfo($"[SODMotives]   histogram {sb}");
                log.LogInfo($"[SODMotives]   edges below 0.35={below35}  below 0.30={below30}  below 0.25={below25}");

                worst.Sort((x, y) => x.like.CompareTo(y.like));
                int wshow = Math.Min(20, worst.Count);
                log.LogInfo($"[SODMotives]   --- {worst.Count} edges under 0.38; worst {wshow}: ---");
                for (int i = 0; i < wshow; i++)
                    log.LogInfo($"[SODMotives]       like={MotivesPlugin.F(worst[i].like).PadLeft(6)}  {worst[i].desc}");

                log.LogInfo($"[SODMotives]   --- affairs/love-triangles found: {affairs.Count} ---");
                int ashow = Math.Min(15, affairs.Count);
                for (int i = 0; i < ashow; i++) log.LogInfo($"[SODMotives]       {affairs[i]}");

                // ---- MOTIVE ENGINE: strongest directed motives citywide (the pool a
                //      weighted pick would draw the killer/victim from) ----
                var motives = new List<(float score, MotiveType type, string aggressor, string desc)>();
                var victimAggressorCount = new Dictionary<int, int>();
                for (int i = 0; i < n; i++)
                {
                    Human a = cits[i];
                    if (a == null) continue;
                    MotiveResult m = Motive.BestTarget(a);
                    if (!m.HasMotive) continue;
                    motives.Add((m.score, m.type, MotivesPlugin.Name(a), m.detail));
                    if (m.target != null)
                    {
                        int vid = m.target.humanID;
                        victimAggressorCount[vid] = victimAggressorCount.TryGetValue(vid, out int c) ? c + 1 : 1;
                    }
                }
                motives.Sort((x, y) => y.score.CompareTo(x.score));
                log.LogInfo($"[SODMotives]   --- MOTIVE ENGINE: {motives.Count} citizens have a real motive; top 20: ---");
                int mshow = Math.Min(20, motives.Count);
                for (int i = 0; i < mshow; i++)
                    log.LogInfo($"[SODMotives]       score={MotivesPlugin.F(motives[i].score).PadLeft(7)} [{motives[i].type}] {motives[i].aggressor}: {motives[i].desc}");

                int multi = 0; foreach (var kv in victimAggressorCount) if (kv.Value >= 2) multi++;
                log.LogInfo($"[SODMotives]   victims with >=2 motivated aggressors (natural red-herrings): {multi}");
                log.LogInfo("[SODMotives] ################################################");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives] CityScan error: {e}"); }
        }
    }

    // ---- pipeline hooks (postfix = observe after the game runs its own logic) ----

    // Hide the lazily-created writer "from" connection on the mod's anonymous rent-arrears threat notes,
    // once the game auto-creates their facts (allFacts is empty at inject time, so it can't be done there).
    // Keeps the note's handwriting a LATENT match (not a spoiler); hidden once per note, so a later player
    // handwriting-match re-reveals the same fact through a different path.
    [HarmonyPatch(typeof(Evidence), nameof(Evidence.AutoCreateFacts), new Type[] { typeof(bool) })]
    internal static class Patch_Evidence_AutoCreateFacts
    {
        static void Postfix(Evidence __instance, bool discovery)
        {
            try
            {
                if (__instance == null) return;
                int id = -1;
                try { var it = __instance.interactable; if (it != null) id = it.id; } catch { }
                if (id < 0 || !ClueInjector.AnonWriterNoteIds.Contains(id)) return;
                Human w = null; try { w = __instance.writer; } catch { }
                ClueInjector.HideWriterConnection(__instance, w);   // removes the auto-created writer "From" fact (logs only if it removed any)
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] hook: AutoCreateFacts postfix: {e.Message}"); }
        }
    }

    // The choke point every fact-link passes through as it is CREATED (fires whenever/however the writer
    // link is added, unlike AutoCreateFacts which only ran once with no matching fact). For the mod's
    // tracked threat notes: hide the writer link the instant it appears (by GetOther==writer, key==handwriting,
    // or the fromEvidence flag) and LOG every link so we can see how the "from" is actually keyed.
    [HarmonyPatch]
    internal static class Patch_Evidence_AddFactLinkExe
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Evidence), "AddFactLinkExe",
                new Type[] { typeof(Fact), typeof(Evidence.DataKey), typeof(bool) });

        static void Postfix(Evidence __instance, Fact newFact, Evidence.DataKey newKey, bool thisIsTheFromEvidence)
        {
            try
            {
                if (__instance == null || newFact == null) return;
                int id = -1;
                try { var it = __instance.interactable; if (it != null) id = it.id; } catch { }
                if (id < 0 || !ClueInjector.AnonWriterNoteIds.Contains(id)) return;

                Human w = null; try { w = __instance.writer; } catch { }
                Evidence wev = null; if (w != null) { try { wev = w.evidenceEntry; } catch { } }
                bool matchOther = false;
                try { var o = newFact.GetOther(__instance); if (o != null && wev != null && o.Pointer == wev.Pointer) matchOther = true; } catch { }
                string pn = "?"; try { if (newFact.preset != null) pn = newFact.preset.name; } catch { }
                MotivesPlugin.Log.LogInfo($"[SODMotives] hook: AddFactLinkExe note={id} key={newKey} fromEv={thisIsTheFromEvidence} matchWriter={matchOther} preset='{pn}' (removal handled after AutoCreateFacts).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] hook: AddFactLinkExe: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.TriggerNextMurder))]
    internal static class Patch_TriggerNextMurder
    {
        static void Postfix()
        {
            MotivesPlugin.Log.LogInfo("[SODMotives] >>> TriggerNextMurder() fired.");
        }
    }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.PickNewMurderer))]
    internal static class Patch_PickNewMurderer
    {
        static void Postfix(MurderController __instance)
        {
            Human m = __instance.currentMurderer;
            MotivesPlugin.Log.LogInfo($"[SODMotives] --- PickNewMurderer() -> murderer = {MotivesPlugin.Name(m)}");
        }
    }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.PickNewVictim))]
    internal static class Patch_PickNewVictim
    {
        static void Postfix(MurderController __instance)
        {
            Human m = __instance.currentMurderer;
            Human v = __instance.currentVictim;
            MotivesPlugin.Log.LogInfo($"[SODMotives] --- PickNewVictim() -> victim = {MotivesPlugin.Name(v)} (murderer currently {MotivesPlugin.Name(m)})");
        }
    }

    // ===== THE OVERRIDE: swap in a motivated (killer -> victim) pair =====
    // Prefix runs before ExecuteNewMurder builds the Murder object. victimSite is
    // null at this point (the game derives location/clues from the victim), so we
    // just replace the murderer + victim and let the whole pipeline rebuild.
    [HarmonyPatch(typeof(MurderController), nameof(MurderController.ExecuteNewMurder))]
    [HarmonyPriority(Priority.First)]
    internal static class Patch_ExecuteNewMurder_Override
    {
        static void Prefix(MurderController __instance, ref Human newMurderer, ref Human newVictim, MurderPreset preset, ref NewGameLocation victimSite)
        {
            if (!MurderSelector.EnableOverride) return;
            // Only touch ordinary generated (proc-gen sandbox) MURDERS. Special case
            // types (kidnap, sniper) and cover-ups/story are left entirely to the game
            // — forcing a motivated pair onto e.g. a kidnapping hangs it at waitForLocation.
            try
            {
                if (!__instance.procGenLoopActive)
                {
                    MotivesPlugin.Log.LogInfo("[SODMotives] override: not a proc-gen sandbox murder; leaving vanilla case untouched.");
                    return;
                }
                if (preset != null && preset.caseType != MurderPreset.CaseType.murder)
                {
                    MotivesPlugin.Log.LogInfo($"[SODMotives] override: special case type '{preset.caseType}' (kidnap/sniper) — leaving vanilla, untouched.");
                    return;
                }
                if (MurderSelector.ShouldForceVanilla())
                {
                    MotivesPlugin.Log.LogInfo("[SODMotives] override: forced VANILLA case (every-N cadence) — leaving it untouched, signature and all.");
                    return;
                }
            }
            catch { }
            try
            {
                Human m, v;
                // V2.1: pick a victim rich in real, event-backed enemies (affairs + workplace),
                // then a RANDOM killer from that mixed-motive pool. Every suspect is a real red
                // herring; vanilla's physical evidence (built around the chosen killer) convicts.
                if (MurderSelector.TryPickVictimCentric(out m, out v, out var suspectPool))
                {
                    // Commit the override FIRST so a hiccup in the logging block below can't leave
                    // the vanilla pair in place while the mod victim is already in the bookkeeping.
                    newMurderer = m; newVictim = v; victimSite = null;
                    __instance.currentMurderer = m; __instance.currentVictim = v;

                    var killerMotive = MurderSelector.MotiveByVictim.TryGetValue(v.humanID, out var mr) ? mr : default;
                    MotivesPlugin.Log.LogInfo("[SODMotives] ************ MOTIVATED MURDER (V2.1 mixed pool) ************");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   vanilla would have been: {MotivesPlugin.Name(newMurderer)} -> {MotivesPlugin.Name(newVictim)}");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   VICTIM: {MotivesPlugin.Name(v)}  ({(suspectPool != null ? suspectPool.Count : 0)} real suspect(s) in pool)");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   KILLER: {MotivesPlugin.Name(m)}  [{killerMotive.type}] {killerMotive.detail}");
                    if (suspectPool != null)
                    {
                        MotivesPlugin.Log.LogInfo("[SODMotives]   SUSPECT POOL (each a real red herring; forensics convicts the one):");
                        for (int i = 0; i < suspectPool.Count; i++)
                        {
                            var s = suspectPool[i];
                            bool isKiller = s.suspect != null && m != null && s.suspect.humanID == m.humanID;
                            MotivesPlugin.Log.LogInfo($"[SODMotives]       {(isKiller ? "* " : "  ")}{MotivesPlugin.F(s.score).PadLeft(6)}  {MotivesPlugin.Name(s.suspect)}  [{s.type}] {s.detail}");
                        }
                    }
                    // Back-compat: on an affair-backed kill, log nearest knowers (F12 interro aid).
                    try
                    {
                        if (MurderSelector.AffairByVictim.TryGetValue(v.humanID, out var affair) && affair != null)
                        {
                            UnityEngine.Vector3 scenePos = default; string sceneName = "victim's home";
                            try { if (v.home != null && v.home.anchorNode != null) { scenePos = v.home.anchorNode.position; sceneName = v.home.name; } } catch { }
                            MotivesPlugin.Log.LogInfo($"[SODMotives]   nearest affair-knowers to {sceneName}:");
                            foreach (var ln in affair.NearestKnowers(scenePos, 6)) MotivesPlugin.Log.LogInfo($"[SODMotives]       {ln}");
                        }
                    }
                    catch { }
                    MotivesPlugin.Log.LogInfo("[SODMotives] **********************************************************");
                }
                else
                {
                    string filt = DebugTools.ForceMotiveType != MotiveType.None
                        ? $" (FORCE MOTIVE={DebugTools.ForceMotiveType} active — no victim has enough suspects of that type; F6 to clear)"
                        : "";
                    MotivesPlugin.Log.LogInfo($"[SODMotives] override: no event-backed suspect pool available; leaving vanilla pick untouched.{filt}");
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] override error (leaving vanilla pick): {e}"); }
        }
    }

    // Validation: confirm the (possibly overridden) victim still gets a real location.
    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.SetMurderLocation))]
    internal static class Patch_SetMurderLocation
    {
        static void Postfix(NewGameLocation newLoc)
        {
            string site = "<null>";
            try { if (newLoc != null) site = newLoc.name; } catch { site = "<err>"; }
            MotivesPlugin.Log.LogInfo($"[SODMotives] [flow] SetMurderLocation -> {site}");
        }
    }

    // ===== Inject motive clues once the murder is actually underway =====
    // Placement needs the world ready; early states (research/waitForLocation) have
    // no scene/placement context and SpawnItem returns null. Inject at 'executing'.
    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.SetMurderState))]
    internal static class Patch_MurderStateInject
    {
        static void Postfix(MurderController.Murder __instance, MurderController.MurderState newState)
        {
            try
            {
                if (__instance == null || __instance.victim == null) return;
                if (!MurderSelector.OverriddenVictimIds.Contains(__instance.victim.humanID)) return;

                // TRACE every state transition so we can see where a stuck case stalls.
                string mo = "?"; try { if (__instance.mo != null) mo = __instance.mo.name; } catch { }
                string loc = "?"; try { loc = __instance.location != null ? __instance.location.name : "<null>"; } catch { }
                MotivesPlugin.Log.LogInfo($"[SODMotives][trace] {MotivesPlugin.Name(__instance.murderer)} -> {MotivesPlugin.Name(__instance.victim)}  state=>{newState}  mo={mo}  loc={loc}");

                // Inject clues only once the murder has actually happened.
                if (newState == MurderController.MurderState.post)
                    ClueInjector.InjectForCase(__instance);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue hook error: {e.Message}"); }
        }
    }

    // ===== Strip serial-killer signatures from our motivated cases =====
    // A personal crime of passion shouldn't leave a serial-killer calling card,
    // nickname, or graffiti. Skip those generators for murders we overrode.
    internal static class SignatureStrip
    {
        internal static bool ShouldStrip(MurderController.Murder m)
        {
            if (!MurderSelector.StripSignatures || m == null) return false;
            try { return m.victim != null && MurderSelector.OverriddenVictimIds.Contains(m.victim.humanID); }
            catch { return false; }
        }
    }

    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.PickNewCallingCard))]
    internal static class Patch_StripCallingCard
    {
        static bool Prefix(MurderController.Murder __instance)
        {
            if (SignatureStrip.ShouldStrip(__instance))
            {
                MotivesPlugin.Log.LogInfo("[SODMotives] signature: skipped calling card (motivated case).");
                return false; // skip original
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.GenerateMoniker))]
    internal static class Patch_StripMoniker
    {
        static bool Prefix(MurderController.Murder __instance)
        {
            if (SignatureStrip.ShouldStrip(__instance)) { return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.GenerateGraffiti))]
    internal static class Patch_StripGraffiti
    {
        static bool Prefix(MurderController.Murder __instance)
        {
            if (SignatureStrip.ShouldStrip(__instance)) { return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.ExecuteNewMurder))]
    internal static class Patch_ExecuteNewMurder
    {
        static void Postfix(Human newMurderer, Human newVictim)
        {
            var log = MotivesPlugin.Log;
            log.LogInfo("[SODMotives] ================ MURDER FINALIZED ================");
            log.LogInfo($"[SODMotives]   MURDERER: {MotivesPlugin.Name(newMurderer)}");
            log.LogInfo($"[SODMotives]   VICTIM:   {MotivesPlugin.Name(newVictim)}");
            float mv = MotivesPlugin.DirectedLike(newMurderer, newVictim);
            float vm = MotivesPlugin.DirectedLike(newVictim, newMurderer);
            log.LogInfo($"[SODMotives]   like(murderer->victim) = {MotivesPlugin.F(mv)}   like(victim->murderer) = {MotivesPlugin.F(vm)}");
            log.LogInfo($"[SODMotives]   (NaN like = no acquaintance edge exists between them)");
            MotivesPlugin.Describe("MURDERER info", newMurderer);
            MotivesPlugin.Describe("VICTIM   info", newVictim);
            MotivesPlugin.LogTopNegative("MURDERER feelings", newMurderer);
            MotivesPlugin.LogTopNegative("VICTIM feelings", newVictim);

            // ---- DRY RUN: what the motive engine WOULD do (no behavior change yet) ----
            log.LogInfo("[SODMotives]   ----- MOTIVE ENGINE DRY-RUN -----");
            MotiveResult vanillaMotive = Motive.Score(newMurderer, newVictim);
            log.LogInfo($"[SODMotives]   murderer's motive toward the ACTUAL victim: {(vanillaMotive.HasMotive ? vanillaMotive.ToString() : "NONE (vanilla picked an unmotivated victim)")}");
            MotiveResult best = Motive.BestTarget(newMurderer);
            if (best.HasMotive)
            {
                bool matches = best.target != null && Motive.Same(best.target, newVictim);
                log.LogInfo($"[SODMotives]   engine would target: {best}");
                log.LogInfo($"[SODMotives]   -> {(matches ? "SAME as vanilla victim" : "DIFFERENT from vanilla victim")}");
            }
            else
            {
                log.LogInfo("[SODMotives]   engine found NO strong motive for this murderer (would re-roll murderer in real mode).");
            }
            log.LogInfo("[SODMotives] ==================================================");
            // One-time citywide calibration dump on the first observed murder.
            CityScan.RunOnce();
        }
    }
}

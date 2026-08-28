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
            MurderSelector.TopPoolSize = Config.Bind("Selection", "TopPoolSize", 40,
                "Weighted-random pick is drawn from this many of the strongest feuds.").Value;
            MurderSelector.RedHerringBonusPer = Config.Bind("Selection", "RedHerringBonusPer", 0.4f,
                "Selection weight bonus per EXTRA motivated enemy the victim has (favours red-herring-rich victims).").Value;
            MurderSelector.WeightExponent = Config.Bind("Selection", "WeightExponent", 0.6f,
                "Below 1.0 compresses score gaps so professional/money/feud motives surface, not only infidelity.").Value;
            MurderSelector.SameTypePenalty = Config.Bind("Selection", "SameTypePenalty", 0.4f,
                "Weight multiplier for a motive type used in the previous murder (lower = more variety between cases).").Value;
            MurderSelector.StripSignatures = Config.Bind("Flavour", "StripSignatures", true,
                "Remove serial-killer calling card/moniker/graffiti from motivated cases so they read as personal crimes.").Value;
            MurderSelector.VanillaChance = Config.Bind("Flavour", "VanillaSerialKillerChance", 0.2f,
                "Chance (0..1) to leave a murder as a full vanilla serial-killer case (signature and all) instead of a motivated one.").Value;
            ClueInjector.Enable = Config.Bind("Clues", "InjectClues", true,
                "Inject deliberately-ambiguous physical notes per motivated case, hinting at motives.").Value;
            ClueInjector.MaxClues = Config.Bind("Clues", "MaxCluesPerCase", 3,
                "Notes injected per case: the killer's plus red herrings from other motivated suspects (so motive alone isn't a giveaway).").Value;
            ClueInjector.ObviousNames = Config.Bind("Clues", "ObviousTestNames", true,
                "TESTING: rename injected notes to 'MODCLUE ...' so they're easy to find. Set false for normal play.").Value;
            ClueInjector.FingerprintChance = Config.Bind("Clues", "FingerprintChance", 0.7f,
                "Chance (0..1) a note carries the author's fingerprints. Below that, it's traceable only by handwriting.").Value;

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(MotivesPlugin).Assembly);
            DebugTools.Register();
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
                if (MurderSelector.ShouldLeaveVanilla())
                {
                    MotivesPlugin.Log.LogInfo("[SODMotives] override: rolled a VANILLA case this time (serial-killer variety) — leaving it untouched, signature and all.");
                    return;
                }
            }
            catch { }
            try
            {
                if (MurderSelector.TryPick(out Human m, out Human v, out MotiveResult mot))
                {
                    MotivesPlugin.Log.LogInfo("[SODMotives] ****************** MOTIVE OVERRIDE ******************");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   vanilla would have been: {MotivesPlugin.Name(newMurderer)} -> {MotivesPlugin.Name(newVictim)}");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   MOTIVATED PICK:          {MotivesPlugin.Name(m)} -> {MotivesPlugin.Name(v)}");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   MOTIVE: [{mot.type}] {mot.detail}  (score {MotivesPlugin.F(mot.score)})");
                    MotivesPlugin.Log.LogInfo("[SODMotives] ****************************************************");
                    newMurderer = m;
                    newVictim = v;
                    victimSite = null; // force the game to recompute the site for the new victim
                    __instance.currentMurderer = m;
                    __instance.currentVictim = v;
                }
                else
                {
                    MotivesPlugin.Log.LogInfo("[SODMotives] override: no motivated pair available this time; leaving vanilla pick untouched.");
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

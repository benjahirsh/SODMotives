using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
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
    [BepInPlugin(Guid, "SOD Motives", "1.3.8")]
    public class MotivesPlugin : BasePlugin
    {
        public const string Guid = "com.opi.sodmotives";
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        // A3: every knob maps to a re-apply action, run at load AND whenever the value changes (in-game
        // ConfigurationManager overlay, a .cfg reload, or our own writes). This is what makes edits
        // actually take effect — previously values were copied into static fields once and never re-read.
        private readonly Dictionary<ConfigEntryBase, Action> _applyByEntry = new Dictionary<ConfigEntryBase, Action>();

        public override void Load()
        {
            Log = base.Log;
            Banner();

            // Config (BepInEx/config/com.opi.sodmotives.cfg). Every knob is bound via BindApply so it
            // stays LIVE — re-applied into its static field on any change (in-game overlay / .cfg reload).
            // An edit takes effect on the NEXT case; the [Feud] seeding caps take effect on the next NEW GAME.

            // --- General ---
            BindApply("General", "EnableOverride", true,
                "Replace vanilla killer/victim selection with a motivated pair from the social graph.",
                v => MurderSelector.EnableOverride = v);

            // --- Motive Mix: how many cases are mod vs vanilla, and the blend of motive families ---
            BindApply("Motive Mix", "MotiveCaseShare", 1.0f,
                "MAIN KNOB: fraction (0..1) of murders that are relationship-MOTIVE cases; the rest are left as vanilla serial-killer cases. Default 1 = every case is a motive case (so the mod always shows). Lower it to mix in some classic untraceable serial-killer hunts (with their signatures) for variety; 0 = all vanilla. Applies to the next case.",
                v => MurderSelector.MotiveCaseShare = v, R01(), Order(100));
            BindApply("Motive Mix", "SeparateCasePerKiller", true,
                "Open a FRESH case for each motivated murder instead of letting the game bundle them all into its single ongoing serial-killer 'Murder Case'. Vanilla reuses one murder case for every proc-gen victim (one serial killer, one case); the mod's murders each have a DIFFERENT killer, so without this the game folds them into one case and overwrites its resolve questions to the newest victim whenever a body is found (what looks like the case 'switching' to an unrelated killer). On (default) = every motivated murder is its own case, like the first murder of a fresh game. Only affects the mod's own cases; any mixed-in vanilla serial murders still bundle. Applies at the next victim discovery.",
                v => MurderSelector.SeparateCasePerKiller = v, null, Order(99));
            BindApply("Motive Mix", "MotivatedKidnapShare", 1f,
                "KIDNAP analogue of MotiveCaseShare: fraction (0..1) of KIDNAP cases that get a motivated killer->victim pair (a walk-native abduction to a real holding den — the victim walks there and is restrained, leaving a real trail); the rest run vanilla. Default 1 = every kidnap is motivated; 0 = kidnaps stay vanilla. Requires the sandbox 'Kidnapping' case type ON. Sniper cases have their own slider (MotivatedSniperShare). Applies to the next kidnap.",
                v => MurderSelector.MotivatedKidnapShare = v, R01(), Order(95));
            BindApply("Motive Mix", "MotivatedSniperShare", 1f,
                "SNIPER analogue of MotiveCaseShare / MotivatedKidnapShare: fraction (0..1) of SNIPER cases that get a motivated killer->victim pair; the rest run vanilla. Default 1 = every sniper case is motivated; 0 = snipers stay vanilla. Requires the sandbox 'Sniper cases' type ON. The MO is chosen by a killer-home line-of-sight test (VoyeurSniper if the killer's home overlooks the victim's home/work, shooting from the killer's own window; else the street/rooftop ExCopSniper). For ExCop the watchdog seeds a shot site from the city's scored street vantages (a random pick among the top-ranked, climbing toward the best if no shot lines up); the nest, the shot and a clean give-up to a vanilla site are all left to the game. Applies to the next sniper.",
                v => MurderSelector.MotivatedSniperShare = v, R01(), Order(90));
            BindApply("Motive Mix", "StructuralVictimWeight", 0.5f,
                "How often a BOSS or LANDLORD may be the VICTIM, relative to everyone else (1 = as likely as anyone; 0.5 = half as likely, the default; 0 = only when no other victim is available). Bosses and landlords are structural NPCs (a company needs its director, a building its landlord), so keeping this below 1 keeps more of them alive. It doubles as the boss/promotee and landlord/tenant dial: down-weighting the boss shifts a workplace case toward the PROMOTEE, and down-weighting the landlord shifts a property case toward the TENANT. Applies to the next case; murders, kidnaps and snipers all use it. (Debug: press the VictimSampler key, default F2, to measure the effect instantly.)",
                v => MurderSelector.StructuralVictimWeight = v, R01(), Order(85));
            // The five motive families — each a 0..1 weight, NORMALISED together, so any mix works (they need
            // not sum to 1). Set one to 0 to drop that motive from the blend.
            BindApply("Motive Mix", "AffairShare", 0.35f,
                "Relative weight of AFFAIR (infidelity / love-triangle) cases in the blend.",
                v => MurderSelector.AffairShare = v, R01());
            BindApply("Motive Mix", "WorkplaceShare", 0.46f,
                "Relative weight of WORKPLACE (promotion + layoffs) cases. NOTE: bounded by how many bosses/companies exist — a high weight can't create more workplace cases than the city has candidates.",
                v => MurderSelector.WorkplaceShare = v, R01());
            BindApply("Motive Mix", "PropertyShare", 0.18f,
                "Relative weight of PROPERTY (eviction + rent-arrears) cases. NOTE: bounded by how many landlords exist — best kept rare; in small sandboxes landlords can run out.",
                v => MurderSelector.PropertyShare = v, R01());
            BindApply("Motive Mix", "FeudShare", 0.35f,
                "Relative weight of personal-FEUD cases in the blend.",
                v => MurderSelector.FeudShare = v, R01());
            BindApply("Motive Mix", "DebtShare", 0.35f,
                "Relative weight of DEBT cases in the blend.",
                v => MurderSelector.DebtShare = v, R01());
            // --- Selection ---
            BindApply("Selection", "MinSuspects", 3,
                "PREFER victims with at least this many real event-backed suspects. If none qualify, degrade to the richest available victim (never vanilla for the floor).",
                v => MurderSelector.MinSuspects = v);
            BindApply("Selection", "KillerPoolSize", 10,
                "The real killer is picked uniformly at random from the victim's top-N strongest suspects.",
                v => MurderSelector.KillerPoolSize = v);

            // --- Workplace (built on-demand from live company rosters at murder time) ---
            BindApply("Workplace", "EnableWorkplace", true,
                "Add workplace-rivalry cases (promotions + layoffs) as murder-motive sources, derived on-demand from real company rosters.",
                v => WorkplaceSim.Enable = v);
            BindApply("Workplace", "MaxSuspects", 5,
                "Maximum suspects per workplace case (passed-over rivals / employees on the layoff list).",
                v => WorkplaceSim.MaxSuspects = v);
            BindApply("Workplace", "PromotionShare", 0.7f,
                "Of workplace cases, the fraction that are promotions; the rest are layoffs. NOTE: pushing this LOW (more layoffs) is bounded by how many bosses exist to be the layoff victim.",
                v => WorkplaceSim.PromotionShare = v, R01());

            // --- Property (built on-demand from the live residency graph) ---
            BindApply("Property", "EnableProperty", true,
                "Add landlord/tenant cases (eviction/redevelopment + rent arrears) as murder-motive sources, derived on-demand from real landlord/tenant relationships.",
                v => PropertySim.Enable = v);
            BindApply("Property", "MaxTenantSuspects", 6,
                "Maximum tenant suspects per eviction case (the aggrieved tenants being cleared out for redevelopment).",
                v => PropertySim.MaxTenantSuspects = v);
            BindApply("Property", "MaxArrearsPerLandlord", 3,
                "Cap on rent-arrears cases per landlord. Rent-arrears is bidirectional, so a landlord-victim aggregates one delinquent tenant per case into the suspect pool; this bounds that (and the 'chasing N tenants' gossip) so a big landlord doesn't produce an overkill pool.",
                v => PropertySim.MaxArrearsPerLandlord = v);
            BindApply("Property", "EvictionShare", 0.3f,
                "Of property cases, the fraction that are evictions (a landlord victim, many aggrieved tenant suspects); the rest are rent-arrears (bidirectional — landlord OR tenant can be the victim). NOTE: bounded by landlord count.",
                v => PropertySim.EvictionShare = v, R01());

            // --- Feud (SEEDED AT NEW GAME — these apply to the next NEW GAME, not the current city) ---
            BindApply("Feud", "EnableFeuds", true,
                "(New Game) Seed personal-feud cases (bad blood between two citizens), synthesized from genuinely soured relationships. Bidirectional: either party can be killer or victim.",
                v => FeudSim.EnableFeuds = v);
            BindApply("Feud", "EnableDebts", true,
                "(New Game) Seed debt cases (a creditor fed up with a debtor who stopped paying), synthesized over real acquaintance edges. Bidirectional: either party can be killer or victim.",
                v => FeudSim.EnableDebts = v);
            BindApply("Feud", "MaxFeuds", 40,
                "(New Game) Cap on synthesized feuds seeded per city.",
                v => FeudSim.MaxFeuds = v);
            BindApply("Feud", "MaxDebts", 40,
                "(New Game) Cap on synthesized debts seeded per city.",
                v => FeudSim.MaxDebts = v);

            // --- Clues ---
            BindApply("Clues", "MaxCluesPerCase", 8,
                "Safety cap on total motive clues per case (one per event-suspect: love letter / promotion letter + rival threats / termination notices).",
                v => ClueInjector.MaxClues = v);
            BindApply("Clues", "WorkplaceClueShare", 0.5f,
                "Chance (0..1) a given clue is placed at the victim's WORKPLACE rather than home. Motive-agnostic: any motive's clue can land at either, so location never betrays the motive.",
                v => ClueInjector.WorkplaceClueShare = v, R01());
            BindApply("Clues", "EmailClueShare", 0.5f,
                "Chance (0..1) an eligible clue (affair love-letter, redundancy list, redevelopment plan) arrives as an EMAIL in an NPC's inbox instead of a physical note — never both. Promotion is exempt (always BOTH channels). RentArrears/Feud/Debt are always physical (handwriting + fingerprint, which email can't carry).",
                v => ClueInjector.EmailClueShare = v, R01());

            // --- Talk to Partner ---
            BindApply("Talk to Partner", "Enable", true,
                "Adds a 'Can I talk to your partner?' option when you speak to someone AT THEIR HOME. Choosing it sends the person you were talking to away and their partner comes to answer the door, so you can question them too (if they live alone or their partner is out, they say so; a sleeping partner is woken). Purely additive: it needs none of the DialogAdditions mod's dependencies.",
                v => TalkToPartner.Enable = v);

            // --- Troubleshooting: you shouldn't normally need these — fixes, test aids, and inverted toggles. ---
            BindApply("Troubleshooting", "MotiveCaseWatchdog", true,
                "Supervises the mod's own motive cases: recovers one whose killer or victim gets destroyed so the game can't jam, force-finishes a case stuck in 'executing' with the killer present (see StallGameHours), and manages motivated sniper setup. Leave this ON. Turn it OFF only for troubleshooting, to check whether a stuck or misbehaving case is caused by this supervision. Off disables all of the above and only ever affects the mod's own cases, never vanilla. (Renamed from UnstickStalledMurders; an old setting of that name is ignored and this defaults back to on.)",
                v => MurderWatchdog.Enable = v);
            BindApply("Troubleshooting", "StallGameHours", 24f,
                "In-game hours a mod murder may sit stalled in 'executing' before the watchdog force-finishes it (only if killer is present and no damage is landing).",
                v => MurderWatchdog.StallGameHours = v);
            BindApply("Troubleshooting", "WaitLocationStallHours", 12f,
                "In-game hours a mod case may sit stalled in 'waitForLocation' (no seatable location — e.g. a kidnap with no valid holding den) before the watchdog CANCELS it so it can't hang the case indefinitely. A one-time IsValidLocation den probe is logged the moment it stalls.",
                v => MurderWatchdog.WaitLocationStallHours = v);
            BindApply("Troubleshooting", "StripSignatures", true,
                "Remove serial-killer calling card / moniker / graffiti from motivated cases so they read as personal crimes. (Off = motive cases keep the vanilla serial-killer signatures.)",
                v => MurderSelector.StripSignatures = v);
            BindApply("Troubleshooting", "DisableClueInjection", false,
                "Turn OFF motive-clue injection (physical notes + emails). Default off = clues ON.",
                v => ClueInjector.Enable = !v);
            BindApply("Troubleshooting", "DisableInterrogation", false,
                "Turn OFF mod interrogation gossip (the extra answer appended to 'do you know this person?'). Default off = interrogation ON.",
                v => Interrogation.Enable = !v);
            BindApply("Troubleshooting", "ObviousTestNames", false,
                "TESTING: rename injected notes to 'MODCLUE ...' so they're easy to find. Leave false for normal play; set true only to locate mod clues while testing.",
                v => ClueInjector.ObviousNames = v);

            // --- Debug (developer tooling) ---
            BindApply("Debug", "EnableDebugKeys", false,
                "Developer tooling. OFF (default): all debug hotkeys are inert EXCEPT the case-solution overlay (CaseSolutionOverlay, default F9), the on-screen dev indicators are hidden, and verbose per-case logging is silenced. ON: enables the full test loop (force event, trigger murder, ghost, teleports, always-answer) plus verbose logs. Does NOT affect the F9 overlay. Set CaseSolutionOverlay to None in [Debug Keys] if you don't want F9 at all.",
                v => DebugTools.EnableDebugKeys = v);
            BindApply("Debug", "ShowSuspectPoolAndKnowers", false,
                "In the F9 case-solution overlay, also list the full SUSPECT POOL (with the killer marked) and the victim/killer KNOWER interview lists. Off (default): F9 shows just the core solution (case type, state, killer, victim, scene, motive, injected clues). Independent of EnableDebugKeys.",
                v => DebugTools.ShowSuspectPoolAndKnowers = v);

            // --- Debug Keys (rebindable hotkeys; KeyCode renders as a key-binder in the overlay) ---
            BindApply("Debug Keys", "TriggerMurder", UnityEngine.KeyCode.F4,
                "TESTING: create the next case NOW (fast test loop), letting the game choose the case type naturally (usually a murder, occasionally a kidnap/sniper), created directly rather than waiting for the vanilla scheduler (whose own TriggerNextMurder button never actually spawns a case). Unlike ForceSniperCase (F3) it does not force a special case type. Set to None to disable.",
                v => DebugTools.KeyTriggerMurder = v);
            BindApply("Debug Keys", "TeleportToKiller", UnityEngine.KeyCode.F8,
                "Teleport to the KILLER's CURRENT position (where they are right now) so you can tail them during a case.",
                v => DebugTools.KeyTeleportKiller = v);
            BindApply("Debug Keys", "CaseSolutionOverlay", UnityEngine.KeyCode.F9,
                "Toggle the on-screen case-solution overlay (killer / victim / suspect pool / injected clues).",
                v => DebugTools.KeyCaseSolution = v);
            BindApply("Debug Keys", "TestAccess", UnityEngine.KeyCode.F7,
                "Toggle TEST ACCESS: ghost mode (NPCs ignore you; invincible) AND always-answer (NPCs never refuse 'do you know this person?') together, since they're used as a pair. Set to None to disable.",
                v => DebugTools.KeyTestAccess = v);
            BindApply("Debug Keys", "TeleportToCityHall", UnityEngine.KeyCode.Home,
                "Teleport to City Hall (a fixed central landmark — handy when a case scene is unresolved). Set to None to disable.",
                v => DebugTools.KeyTeleportCityHall = v);
            BindApply("Debug Keys", "TimeBoost", UnityEngine.KeyCode.End,
                "Toggle fast-forward: ON = the game's 'simulation' time speed (fastest) so slow phases and the 24h stall watchdog play out quickly; OFF = normal speed. Set to None to disable.",
                v => DebugTools.KeyTimeBoost = v);
            BindApply("Debug Keys", "TeleportToScene", UnityEngine.KeyCode.F10,
                "Teleport to the current crime scene.",
                v => DebugTools.KeyTeleportScene = v);
            BindApply("Debug Keys", "TeleportToVictim", UnityEngine.KeyCode.F11,
                "Teleport to the VICTIM's CURRENT position (where they are right now) so you can tail them during a case.",
                v => DebugTools.KeyTeleportVictim = v);

            // Live re-apply: when a knob changes (overlay edit / .cfg reload), push it into its static field.
            Config.SettingChanged += (sender, e) =>
            {
                try
                {
                    var cs = (e as SettingChangedEventArgs)?.ChangedSetting;
                    if (cs != null && _applyByEntry.TryGetValue(cs, out var apply)) apply();
                }
                catch (Exception ex) { Log.LogWarning($"[SODMotives] config live-apply error: {ex.Message}"); }
            };

            // No forced motive type by default — the Motive Mix weights drive the blend. Cycle a force
            // in-game with F6 to test one specific event type.
            DebugTools.ForceMotiveType = MotiveType.None;

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

        // ---- A3 config helpers -----------------------------------------------

        // Bind a knob, apply it now, and register it for live re-apply when it changes (overlay / .cfg).
        // range = optional AcceptableValueRange (renders a slider + clamps); ui = optional overlay hints.
        private void BindApply<T>(string section, string key, T def, string desc, Action<T> apply,
                                  AcceptableValueBase range = null, ConfigurationManagerAttributes ui = null)
        {
            ConfigDescription cd =
                (range == null && ui == null) ? new ConfigDescription(desc)
              : (ui == null) ? new ConfigDescription(desc, range)
              : new ConfigDescription(desc, range, ui);
            ConfigEntry<T> entry = Config.Bind(section, key, def, cd);
            ConfigEntryBase baseEntry = entry;
            Action applyNow = () =>
            {
                // Float knobs: snap to 2 decimals so the overlay (which renders the raw value with no
                // format control) shows a clean number like 0.5 / 0.48 instead of 0.4823529. Writing the
                // rounded value back re-fires SettingChanged; on that pass it is already clean, so it applies.
                if (baseEntry is ConfigEntry<float> fe)
                {
                    float r = (float)Math.Round(fe.Value, 2, MidpointRounding.AwayFromZero);
                    if (Math.Abs(r - fe.Value) > 0.0005f) { fe.Value = r; return; }
                }
                apply(entry.Value);
            };
            applyNow();
            _applyByEntry[entry] = applyNow;
        }

        private static AcceptableValueRange<float> R01() => new AcceptableValueRange<float>(0f, 1f);         // 0..1 slider
        private static ConfigurationManagerAttributes Order(int order) => new ConfigurationManagerAttributes { Order = order };  // float toward top of section

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

    // Overlay integration: the BepInEx ConfigurationManager / BepInExConfigManager overlays recognise a
    // settings-tags class purely by NAME ("ConfigurationManagerAttributes") via reflection — no assembly
    // reference needed. We declare only the few fields we use (nullable so unset = the overlay's default).
    public sealed class ConfigurationManagerAttributes
    {
        public int? Order;
        public bool? Browsable;
        public bool? IsAdvanced;
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
                // #20: when the player prints one of our body-list emails, the printout is an
                // EvidencePrintedVmail whose auto-facts are only From/To — add the body-named citizen
                // connections so its connections tab matches the physical clue (once per printout).
                try { var pv = __instance.TryCast<EvidencePrintedVmail>(); if (pv != null) ClueInjector.ConnectPrintedVmail(pv); } catch { }
                int id = -1;
                try { var it = __instance.interactable; if (it != null) id = it.id; } catch { }
                // Latent letters (affair): strip the auto-created "To" fact so the recipient isn't named — the
                // receiver FIELD stays set (renders the salutation initial), only the board connection goes.
                if (id >= 0 && ClueInjector.AnonReceiverNoteIds.Contains(id))
                {
                    Human r = null; try { r = __instance.reciever; } catch { }
                    ClueInjector.HideReceiverConnection(__instance, r);
                }
                if (id < 0 || !ClueInjector.AnonWriterNoteIds.Contains(id)) return;
                Human w = null; try { w = __instance.writer; } catch { }
                ClueInjector.HideWriterConnection(__instance, w);   // removes the auto-created writer "From" fact (logs only if it removed any)
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] hook: AutoCreateFacts postfix: {e.Message}"); }
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
            // The game picks a fresh serial killer when the previous one is CAUGHT (the vanilla case was solved) or at
            // game start. Either way the vanilla serial-killer episode is over, so clear the serialization lock and let
            // motive cases resume. (We never drive PickNewMurderer ourselves -- our overrides set currentMurderer
            // directly -- so this only fires on a genuine game serial-killer change.)
            if (DebugTools.VanillaSerialActive)
            {
                DebugTools.VanillaSerialActive = false;
                MotivesPlugin.Log.LogInfo("[SODMotives] serialize: serial killer replaced (PickNewMurderer) — vanilla mode cleared; motive cases may resume.");
            }
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
        static void Prefix(MurderController __instance, ref Human newMurderer, ref Human newVictim, MurderPreset preset, ref MurderMO motive, ref NewGameLocation victimSite)
        {
            _restoreSerialKiller = false;   // reset per call; set true only when we actually commit an override below
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
                // ===== A vanilla serial-killer episode locks SUBSEQUENT murders to that killer =====
                // Once the game's own serial killer is at large (a vanilla murder case is open) we leave EVERY new
                // murder vanilla, so the base game bundles them into its one ongoing case WITH its own per-crime-scene
                // objectives -- we never steer the bundling. A vanilla episode MAY start while our motive cases are
                // open; those stay open and solvable, we just don't start a NEW motive case during the episode. The
                // share chooses the mode when no vanilla episode is running. The lock clears when the serial killer is
                // caught -- the game then runs PickNewMurderer (Patch_PickNewMurderer clears the flag).
                bool vanillaMode = DebugTools.VanillaSerialActive;

                if (preset != null && preset.caseType != MurderPreset.CaseType.murder)
                {
                    // Special case types (kidnap/sniper): motivate per their own shares -- but NOT during vanilla mode
                    // (keep the serial-killer episode entirely vanilla). A motivated kidnap/sniper is itself a mod case.
                    bool allowKidnap = !vanillaMode && preset.caseType == MurderPreset.CaseType.kidnap && MurderSelector.ShouldMotivateKidnap();
                    bool allowSniper = !vanillaMode && preset.caseType == MurderPreset.CaseType.sniper && MurderSelector.ShouldMotivateSniper();
                    if (!allowKidnap && !allowSniper)
                    {
                        MotivesPlugin.Log.LogInfo(vanillaMode
                            ? $"[SODMotives] override: special case type '{preset.caseType}' left vanilla (serialized: vanilla serial killer at large until solved)."
                            : $"[SODMotives] override: special case type '{preset.caseType}' left vanilla, untouched (kidnap share {MurderSelector.MotivatedKidnapShare:0.##}, sniper share {MurderSelector.MotivatedSniperShare:0.##}).");
                        return;
                    }
                    MotivesPlugin.Log.LogInfo(allowSniper
                        ? "[SODMotives] override: motivating a SNIPER case (mimic-vanilla: voyeur/ExCop MO by LOS, watchdog seeds the site, shot + force-kill deferred to the game)."
                        : "[SODMotives] override: motivating a KIDNAP case (walk-native abduction to a real den).");
                }
                else
                {
                    // Regular murder: a vanilla episode (vanillaMode) keeps every subsequent murder vanilla until the
                    // killer is caught; otherwise the share decides. A vanilla episode may BEGIN while motive cases are
                    // open (they keep coexisting + stay solvable); we simply never start a NEW motive case during it,
                    // so the serial killer's murders all bundle into one case. We do NOT force motivated otherwise.
                    bool goVanilla = vanillaMode ? true : MurderSelector.ShouldForceVanilla();
                    if (goVanilla)
                    {
                        DebugTools.VanillaSerialActive = true;   // vanilla serial killer at large until caught
                        MotivesPlugin.Log.LogInfo(vanillaMode
                            ? "[SODMotives] override: leaving this murder VANILLA (serialized: same serial killer still at large — bundled with the game's own crime-scene objectives)."
                            : "[SODMotives] override: leaving this murder VANILLA (share chose vanilla) — starting a vanilla serial-killer episode; its later murders stay vanilla until it is solved.");
                        return;
                    }
                }
            }
            catch { }
            try
            {
                Human m, v;
                // V2.1: pick a victim rich in real, event-backed enemies (affairs + workplace), then a killer from
                // that mixed-motive pool. Every suspect is a real red herring; vanilla's physical evidence (built
                // around the chosen killer) convicts. SNIPER: PREFER a killer whose OWN home overlooks the victim
                // (soft preference, no motive cost -- the whole pool is real suspects), so the case can be a
                // VoyeurSniper (shot from the killer's window at the victim's home/work). That mirrors vanilla (whose
                // snipers are mostly voyeur) and spreads the sites out, instead of every ExCop case funnelling to the
                // city's single best sniper street. If no pool suspect is voyeur-viable, it falls back to a random
                // motivated killer + ExCopSniper (street/rooftop). The MO is set to match the chosen pair below.
                bool isSniper = preset != null && preset.caseType == MurderPreset.CaseType.sniper;
                System.Func<Human, Human, bool> preferKiller = isSniper
                    ? (System.Func<Human, Human, bool>)((k, vv) => MurderWatchdog.SniperVoyeurViable(k, vv))
                    : null;
                // Reserve every NPC already committed to an OPEN case so a new motive case can't reuse them. This
                // covers (a) the game's own serial killer (currentMurderer) -- kept alive + distinct for vanilla
                // murders; and (b) the killer AND victim of every unsolved murder, mod or vanilla -- both are "spoken
                // for" (the killer is busy until caught, the victim is being/has been killed, both removed from the
                // sandbox). Without this the motive engine could pick the serial killer as a victim (we murdered them
                // -> dead killer -> the next vanilla case stalled at acquire-equipment), target an in-flight vanilla
                // victim, or reuse another open case's killer (breaking it). Only when the split is on (the game's
                // one-case-at-a-time model has no concurrency to protect).
                HashSet<int> reserved = null;
                if (MurderSelector.SeparateCasePerKiller)
                    reserved = BuildReservedParticipants(__instance);
                if (MurderSelector.TryPickVictimCentric(out m, out v, out var suspectPool, preferKiller, reserved))
                {
                    if (isSniper)
                    {
                        // MO by vantage flavour, mirroring the game's two sniper types. Selection already PREFERRED a
                        // voyeur-viable killer; re-test the chosen pair: the killer's own home overlooks the victim's
                        // home/work (non-cohabiting) -> VoyeurSniper (shoot from the window, works even for a homebody
                        // victim); else ExCopSniper (street/rooftop). Site/vantage/shot AND the force-kill fallback are
                        // DEFERRED to the game; the watchdog seeds the site via the game's own picker so the state
                        // machine engages exactly like vanilla. No pin, no patience net.
                        bool voyeur = MurderWatchdog.SniperVoyeurViable(m, v);
                        var chosen = MurderWatchdog.SniperMO(voyeur);
                        if (chosen != null) motive = chosen;
                        if (DebugTools.EnableDebugKeys)
                            MotivesPlugin.Log.LogInfo($"[SODMotives][sniper] voyeur-viable suspects in the victim's pool: {MurderSelector.LastPreferViableCount}/{MurderSelector.LastPoolScanned} (voyeur preferred at selection).");
                        MotivesPlugin.Log.LogInfo($"[SODMotives] override: SNIPER -- motivated pair {MotivesPlugin.Name(m)} -> {MotivesPlugin.Name(v)}; MO {(chosen != null ? chosen.name : "left to the game")} ({(voyeur ? "voyeur: killer home overlooks victim" : "street/rooftop")}); site/shot deferred to the game (watchdog seeds the site).");
                    }

                    // Preserve the game's OWN serial killer for any following VANILLA murder (see the fields below):
                    // capture currentMurderer BEFORE we overwrite it, then restore it in the postfix once this mod
                    // murder is built. Done for REGULAR and SNIPER mod murders, but NOT kidnaps. A regular murder is
                    // instant, and a sniper runs its shot off its OWN Murder object (execution is not load-bearing on
                    // currentMurderer -- see the field note below), so restoring currentMurderer is safe for both -- and
                    // it stops a following vanilla murder from REUSING the mod killer, which otherwise gave the mod case
                    // and the vanilla case the same press-moniker name and bundled them into one case. A KIDNAP is the
                    // sole exclusion: its kill is a DEFERRED re-cycle and the game pins that kill on whoever
                    // currentMurderer is at the ransom deadline, so restoring there drives the kill onto the wrong
                    // person and hangs the held victim. Kidnap-vs-vanilla name overlap stays outstanding until the
                    // base-game kidnap flow is fixed (kidnaps already do not complete on the current build).
                    bool isKidnap = preset != null && preset.caseType == MurderPreset.CaseType.kidnap;
                    if (MurderSelector.SeparateCasePerKiller && !isKidnap)
                    {
                        try { _serialKillerToRestore = __instance.currentMurderer; _restoreSerialKiller = true; } catch { _restoreSerialKiller = false; }
                    }

                    // Commit the override FIRST so a hiccup in the logging block below can't leave
                    // the vanilla pair in place while the mod victim is already in the bookkeeping.
                    newMurderer = m; newVictim = v; victimSite = null;   // sniper: game picks the site (home/work); other cases: game derives it from the victim's home
                    __instance.currentMurderer = m; __instance.currentVictim = v;

                    // KIDNAP: the swapped-in killer needs a den or the case hangs at waitForLocation
                    // (IsValidLocation accepts only murderer.den). Assign a vacant, walk-in-able "Vacant address"
                    // den (unowned, so the victim can walk in) — same path the F2 force key uses.
                    if (preset != null && preset.caseType == MurderPreset.CaseType.kidnap)
                        MurderSelector.EnsureKidnapDen(m, v, motive);

                    var killerMotive = MurderSelector.MotiveByVictim.TryGetValue(v.humanID, out var mr) ? mr : default;
                    MotivesPlugin.Log.LogInfo("[SODMotives] ************ MOTIVATED MURDER (V2.1 mixed pool) ************");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   vanilla would have been: {MotivesPlugin.Name(newMurderer)} -> {MotivesPlugin.Name(newVictim)}");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   VICTIM: {MotivesPlugin.Name(v)}  ({(suspectPool != null ? suspectPool.Count : 0)} real suspect(s) in pool)");
                    MotivesPlugin.Log.LogInfo($"[SODMotives]   KILLER: {MotivesPlugin.Name(m)}  [{killerMotive.type}] {killerMotive.detail}");
                    // Verbose diagnostics (full suspect pool + nearest case-knowers) only under [Debug]
                    // EnableDebugKeys — a shipped build keeps the concise 3-line summary above.
                    if (DebugTools.EnableDebugKeys)
                    {
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
                        // Log nearest case-knowers (ANY motive type) as an interrogation aid (F12 jumps there).
                        try
                        {
                            if (MurderSelector.EventByVictim.TryGetValue(v.humanID, out var evt) && evt != null)
                            {
                                UnityEngine.Vector3 scenePos = default; string sceneName = "victim's home";
                                try { if (v.home != null && v.home.anchorNode != null) { scenePos = v.home.anchorNode.position; sceneName = v.home.name; } } catch { }
                                MotivesPlugin.Log.LogInfo($"[SODMotives]   nearest case-knowers to {sceneName}:");
                                foreach (var ln in evt.NearestKnowers(scenePos, 6)) MotivesPlugin.Log.LogInfo($"[SODMotives]       {ln}");
                            }
                        }
                        catch { }
                    }
                    MotivesPlugin.Log.LogInfo("[SODMotives] **********************************************************");
                }
                else
                {
                    string filt = (DebugTools.ForceEventType.HasValue || DebugTools.ForceMotiveType != MotiveType.None)
                        ? $" (FORCE={DebugTools.ForceLabel()} active — no victim has enough suspects of that type; F6 to cycle/clear)"
                        : "";
                    MotivesPlugin.Log.LogInfo($"[SODMotives] override: no event-backed suspect pool available; leaving vanilla pick untouched.{filt}");
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] override error (leaving vanilla pick): {e}"); }
        }

        // ===== Preserve the game's own serial killer across our overrides =====
        // Vanilla keeps ONE persistent serial killer in currentMurderer and REUSES it for the next murder (it only
        // re-picks via PickNewMurderer at game start / after the killer is caught). When we override a murder we set
        // currentMurderer to OUR motivated killer -- which the game then reuses for any following VANILLA murder, so
        // the vanilla murder inherits a mod killer. With the per-killer split that put a mod case and a vanilla case
        // on the SAME killer: identical case name (the press moniker is stored on the shared killer) and solving one
        // "caught" that killer and cleaned the other case's bodies + objectives (OnCaseSolved cleans the solved
        // case's killer's murders). We restore the game's serial killer once the mod murder is built, so the mod
        // murder keeps our killer on its own Murder object while vanilla murders keep using the game's serial killer.
        // Safe: execution runs off the Murder object (concurrent murders already prove currentMurderer is not
        // load-bearing for a murder in progress), and GetCurrentMurder keys on currentVictim (which we leave alone).
        private static Human _serialKillerToRestore;
        private static bool _restoreSerialKiller;

        // Collect humanIDs already committed to an OPEN case, so a new motive case never reuses them: the game's
        // persistent serial killer (currentMurderer), plus the killer AND victim of every unsolved murder (mod or
        // vanilla). Participants of SOLVED cases are released (killer caught, case done). Cheap: a handful of murders.
        private static HashSet<int> BuildReservedParticipants(MurderController mc)
        {
            var set = new HashSet<int>();
            try { var sk = mc.currentMurderer; if (sk != null) set.Add(sk.humanID); } catch { }
            try
            {
                foreach (var list in new[] { mc.activeMurders, mc.inactiveMurders })
                {
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        MurderController.Murder mm = null; try { mm = list[i]; } catch { }
                        if (mm == null) continue;
                        Human mv = null, mk = null;
                        try { mv = mm.victim; } catch { }
                        try { mk = mm.murderer; } catch { }
                        int mvid = -1; try { if (mv != null) mvid = mv.humanID; } catch { }
                        if (mvid >= 0 && Patch_PreserveCrimeScene.VictimCaseFinished(mvid)) continue;   // finished -> released
                        if (mvid >= 0) set.Add(mvid);
                        try { if (mk != null) set.Add(mk.humanID); } catch { }
                    }
                }
            }
            catch { }
            return set;
        }

        static void Postfix(MurderController __instance)
        {
            try
            {
                // Defensive: only restore a LIVE serial killer. The reservation above keeps it alive, but if it is
                // somehow dead we must not point currentMurderer at a corpse (that stalls the next vanilla murder at
                // acquire-equipment). Leaving currentMurderer as the mod killer is the safe fallback (no stall).
                bool skAlive = false; try { skAlive = _serialKillerToRestore != null && !_serialKillerToRestore.isDead; } catch { skAlive = false; }
                if (_restoreSerialKiller && __instance != null && skAlive)
                {
                    __instance.currentMurderer = _serialKillerToRestore;
                    MotivesPlugin.Log.LogInfo($"[SODMotives] serial-killer: restored the game's serial killer ({MotivesPlugin.Name(_serialKillerToRestore)}) after a mod override, so following vanilla murders keep their own killer (not the mod one).");
                }
                else if (_restoreSerialKiller && !skAlive)
                {
                    MotivesPlugin.Log.LogWarning("[SODMotives] serial-killer: captured serial killer is not alive; leaving currentMurderer as the mod killer to avoid stalling a vanilla murder on a dead killer.");
                }
            }
            catch { }
            finally { _restoreSerialKiller = false; _serialKillerToRestore = null; }
        }
    }

    // ===== Per-killer case split =====
    // Vanilla keeps ONE ongoing "Murder Case" and folds every proc-gen murder into it: at each victim
    // discovery MurderController.OnVictimDiscovery searches CasePanelController.activeCases for an active
    // (unsubmitted) murder case and REUSES it instead of opening a new one, then rebuilds THAT case's resolve
    // questions from the latest murder (GetCurrentMurder). For one serial killer that is coherent; for our
    // motivated murders -- each a DIFFERENT killer -- it bundles unrelated crimes into a single case and
    // overwrites the earlier victim's questions (the player-reported "the case switched to an unrelated killer").
    //
    // The board (CasePanelController.UpdateCaseControls) makes one tab per case in activeCases with NO caseType
    // filter, so murder cases CAN coexist as separate tabs just like side-op cases -- vanilla simply never makes
    // a second one. So we open a fresh murder case OURSELVES, in a prefix, while every existing case is still in
    // activeCases: CreateNewCase adds it to the list and runs UpdateCaseControls with all cases present, so the
    // new tab appears alongside the old ones (the previous bug hid the old cases during that rebuild, which
    // destroyed their tabs). We then AssignActiveCase to the new case so this victim's evidence + resolve
    // questions attach to it. The original OnVictimDiscovery then runs, finds a matching murder case (ours), takes
    // its reuse branch and skips its own creation -- leaving currentActiveCase pointing at our fresh case.
    //
    // Our (overridden) murders each get their OWN case (the 2nd+; the game's very first murder is left to vanilla).
    // A vanilla serial-killer episode is kept in its own case too: we open a fresh tracked case for the episode's
    // FIRST murder so it can't fold into a mod case, and let the game bundle every later murder of that episode in
    // natively (with its own per-scene objectives). Motivated and vanilla episodes are serialized by the override.
    [HarmonyPatch(typeof(MurderController), nameof(MurderController.OnVictimDiscovery))]
    internal static class Patch_OnVictimDiscovery_SeparateCase
    {
        static void Prefix(MurderController __instance)
        {
            try
            {
                if (!MurderSelector.EnableOverride || !MurderSelector.SeparateCasePerKiller) return;
                MurderController.Murder murder = __instance.GetCurrentMurder();
                if (murder == null || murder.victim == null) return;
                int vid = murder.victim.humanID;

                CasePanelController cpc = CasePanelController.Instance;
                var list = cpc != null ? cpc.activeCases : null;
                if (cpc == null || list == null) return;

                // Is there already an OPEN (unsubmitted) murder case? The FIRST murder of a game has none, so we
                // leave it entirely to vanilla (100% vanilla creation + naming + timing), whether it is a mod or a
                // vanilla murder. Same reuse test the game's own OnVictimDiscovery predicate uses (caseType murder,
                // status not submitted/closable/archived). The postfix tags that first case as a mod case if ours.
                bool anyOpenMurderCase = false;
                for (int i = 0; i < list.Count; i++)
                {
                    Case c = null; try { c = list[i]; } catch { }
                    if (c == null) continue;
                    try
                    {
                        if (c.caseType == Case.CaseType.murder
                            && c.caseStatus != Case.CaseStatus.submitted
                            && c.caseStatus != Case.CaseStatus.closable
                            && c.caseStatus != Case.CaseStatus.archived)
                        { anyOpenMurderCase = true; break; }
                    }
                    catch { }
                }
                bool ours = MurderSelector.OverriddenVictimIds.Contains(vid);

                if (!ours)
                {
                    // VANILLA serial-killer murder. A vanilla episode may coexist with open mod cases, so steer the
                    // FIRST murder of the episode into a fresh, TRACKED vanilla case (so it can't reuse/pollute an
                    // open mod case). Every SUBSEQUENT vanilla murder bundles into that case NATIVELY -- we must NOT
                    // re-point the active case then, because the game adds each new scene's objective on its own
                    // reuse path and a manual AssignActiveCase suppresses it (that was the missing-objective bug).
                    // When the serial killer is caught the case closes; the next episode opens a new tracked case.
                    if (!AnyOpenVanillaCase(cpc))
                    {
                        string vn = "New Murder Case"; try { vn = Strings.Get("ui.interface", "New Murder Case"); } catch { }
                        Case vfresh = cpc.CreateNewCase(Case.CaseType.murder, Case.CaseStatus.handInNotCollected, true, vn);
                        if (vfresh != null)
                        {
                            __instance.AssignActiveCase(vfresh);
                            try { DebugTools.VanillaCaseIds.Add(vfresh.id); } catch { }
                            MotivesPlugin.Log.LogInfo($"[SODMotives] case-split: opened a fresh VANILLA serial-killer case for {MotivesPlugin.Name(murder.victim)} (id={vfresh.id}); its later murders bundle in natively.");
                        }
                    }
                    return;
                }

                // MOD murder: give it its OWN case. The FIRST murder of the game (no open murder case yet) is left
                // entirely to vanilla (100% vanilla creation + naming + timing); our split only runs for the 2nd+.
                if (!anyOpenMurderCase) return;
                // Skip if this victim already has a case -- CaseToVictim records every discovery, so a reverse match
                // means this body was already processed (don't make a second case if discovery fires twice).
                foreach (var kv in DebugTools.CaseToVictim) if (kv.Value == vid) return;

                // Name the new case with the SAME initial string vanilla gives a murder case -- Strings.Get(
                // "ui.interface","New Murder Case") -- via the same CreateNewCase call, so it is indistinguishable
                // from a vanilla case on the board and inherits vanilla's own naming + display timing. NEVER a
                // custom/victim title: that would reveal the murder is a mod case (only the F9 panel should).
                string caseName = "New Murder Case";
                try { caseName = Strings.Get("ui.interface", "New Murder Case"); } catch { }

                // Open the new case NOW, with every existing case still in activeCases so the board keeps their tabs
                // and adds one for this (never remove around CreateNewCase -- that destroys the other tabs). Then
                // point the controller's active case at it. The game's own body finds this active murder case and
                // reuse-skips its own creation, leaving currentActiveCase = ours. Tag it as a mod case.
                Case fresh = cpc.CreateNewCase(Case.CaseType.murder, Case.CaseStatus.handInNotCollected, true, caseName);
                if (fresh != null)
                {
                    __instance.AssignActiveCase(fresh);
                    try { DebugTools.ModCaseIds.Add(fresh.id); } catch { }
                    MotivesPlugin.Log.LogInfo($"[SODMotives] case-split: opened a fresh vanilla-named MOD murder case for {MotivesPlugin.Name(murder.victim)} (own board tab).");
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] case-split prefix error (leaving vanilla bundling): {e.Message}"); }
        }

        // True if an OPEN vanilla serial-killer case we created for this episode is still on the board. Used to steer
        // only the FIRST vanilla murder of an episode into a fresh tracked case; later ones bundle in natively.
        private static bool AnyOpenVanillaCase(CasePanelController cpc)
        {
            try
            {
                var list = cpc != null ? cpc.activeCases : null;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    Case c = null; try { c = list[i]; } catch { }
                    if (c == null) continue;
                    int cid = -1; try { cid = c.id; } catch { }
                    if (!DebugTools.VanillaCaseIds.Contains(cid)) continue;   // only the vanilla case(s) WE opened
                    bool isMurder = false; try { isMurder = c.caseType == Case.CaseType.murder; } catch { }
                    if (!isMurder) continue;
                    bool open = false; try { var s = c.caseStatus; open = s != Case.CaseStatus.submitted && s != Case.CaseStatus.closable && s != Case.CaseStatus.archived; } catch { }
                    if (open) return true;
                }
            }
            catch { }
            return false;
        }

        // Record which victim this discovery's case ended up being (whether we split it or vanilla created/reused
        // it), so the F9 overlay can follow the case SELECTED on the board. Runs for every murder, mod or vanilla.
        // ALSO give each of OUR murders a UNIQUE press MONIKER (MonikerGen): the game names a murder case by the
        // killer's moniker, but our concurrent murders share the city's one serial-killer moniker, so all cases came
        // out identically named. MonikerGen generates a distinct one per murder (vanilla generator, style fallback)
        // and GetMonkier is overridden to return it -- which drives BOTH the board tab (ApplyMonikerToCaseCheck) and
        // the resolution typewriter, so they match and read distinctly.
        static void Postfix(MurderController __instance)
        {
            try
            {
                Case c = __instance.currentActiveCase;
                if (c == null) return;
                int cid = -1; try { cid = c.id; } catch { }
                // NEVER fold a VANILLA bundle case into our bookkeeping. GetCurrentMurder can momentarily point at a
                // mod victim while the active case is the vanilla one; recording that would (a) paint the vanilla
                // case with a mod moniker via Patch_UniqueCaseTab ("both called Marble Philatelist"), and (b) make
                // our solve-cleanup fire on the vanilla case. Vanilla cases stay the base game's, entirely.
                if (DebugTools.VanillaCaseIds.Contains(cid)) return;

                MurderController.Murder m = __instance.GetCurrentMurder();
                if (m == null || m.victim == null) return;
                int vid = m.victim.humanID;
                // Only OUR motivated victims enter CaseToVictim / ModCaseIds / CaseMonikers. A vanilla victim (not
                // overridden) is left out so nothing of ours (naming, solve-cleanup, F9) ever attaches to it.
                if (!MurderSelector.OverriddenVictimIds.Contains(vid)) return;

                DebugTools.CaseToVictim[cid] = vid;
                try { DebugTools.ModCaseIds.Add(cid); } catch { }
                if (MurderSelector.EnableOverride && MurderSelector.SeparateCasePerKiller)
                    MonikerGen.EnsureFor(m);
            }
            catch { }
        }
    }

    // Mints a UNIQUE press moniker per motivated murder and stores it (victim id -> "Adj Noun"), from curated
    // vanilla-STYLE word pools. (We deliberately do NOT invoke the game's own Murder.GenerateMoniker here -- calling
    // that complex method out of its normal setup context hard-crashed the game between cases. These pools read like
    // the game's monikers and are guaranteed present, unique and stable.) Patch_CaseMoniker returns the stored value
    // from GetMonkier, which drives BOTH the board tab and the resolution typewriter.
    internal static class MonikerGen
    {
        private static readonly HashSet<string> Used = new HashSet<string>();
        private static readonly string[] Adjs =
            { "Rotten", "Velvet", "Midnight", "Crimson", "Silent", "Hollow", "Pale", "Gilded", "Ashen", "Grinning",
              "Weeping", "Porcelain", "Rusted", "Marble", "Shrouded", "Whispering", "Faceless", "Gentle", "Patient", "Tin" };
        private static readonly string[] Nouns =
            { "Magpie", "Lantern", "Harlequin", "Cobbler", "Usher", "Clockmaker", "Philatelist", "Gardener", "Curator",
              "Milkman", "Organist", "Taxidermist", "Valentine", "Sandman", "Cartographer", "Lamplighter", "Watchmaker",
              "Undertaker", "Ferryman", "Locksmith" };

        internal static void EnsureFor(MurderController.Murder m)
        {
            try
            {
                if (m == null || m.victim == null) return;
                int vid = m.victim.humanID;
                if (DebugTools.CaseMonikers.ContainsKey(vid)) return;   // already named

                string mon = Fallback(vid);
                int guard = 0;
                // Avoid a moniker already in use.
                while (Used.Contains(mon) && guard < 64) { guard++; mon = Fallback(vid + guard * 7919); }
                Used.Add(mon);
                DebugTools.CaseMonikers[vid] = mon;
                MotivesPlugin.Log.LogInfo($"[SODMotives] case-moniker: '{mon}' for {MotivesPlugin.Name(m.victim)} (drives tab + typewriter).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] case-moniker error: {e.Message}"); }
        }

        private static string Fallback(int k)
        {
            if (k < 0) k = -k;
            return Adjs[k % Adjs.Length] + " " + Nouns[(k / Adjs.Length) % Nouns.Length];
        }
    }

    // Override the killer moniker for OUR murders so both the board tab and the resolution typewriter show the
    // unique per-case name MonikerGen stored (the game otherwise returns the shared city serial-killer moniker).
    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.GetMonkier))]
    internal static class Patch_CaseMoniker
    {
        static void Postfix(MurderController.Murder __instance, ref string __result)
        {
            try
            {
                if (!MurderSelector.EnableOverride || !MurderSelector.SeparateCasePerKiller || __instance == null) return;
                // The game is currently naming a VANILLA case: do NOT substitute a mod press-name, even if this call
                // happens to be for a mod victim's murder (ApplyMonikerToCaseCheck can drift via GetCurrentMurder).
                // That drift is what stamped a mod moniker onto a vanilla case.
                if (DebugTools.NamingVanillaCase) return;
                Human v = null; try { v = __instance.victim; } catch { }
                if (v == null) return;
                if (DebugTools.CaseMonikers.TryGetValue(v.humanID, out string mon) && !string.IsNullOrEmpty(mon))
                    __result = mon;
            }
            catch { }
        }
    }

    // Make the board TAB distinct per case. The game names every concurrent case from ONE shared murder, so even
    // with unique per-murder monikers the tabs collapse to a single name. After vanilla applies its name (at the
    // resolution-form rename), overwrite OUR case's tab with the moniker stored for THAT case's own victim, so the
    // board reads distinctly. Timing-guarded to vanilla's own rename (only once the name is off the generic
    // "New Murder Case"), so the name still appears only after the form. (The resolution typewriter still reads one
    // shared murder's moniker -- a known, accepted limitation.)
    [HarmonyPatch(typeof(Case), nameof(Case.ApplyMonikerToCaseCheck))]
    internal static class Patch_UniqueCaseTab
    {
        // While the game names a VANILLA case, suppress our GetMonkier mod-substitution (Patch_CaseMoniker) so a
        // drifted GetCurrentMurder can't stamp a mod moniker onto it. Set here, cleared in the postfix's finally.
        static void Prefix(Case __instance)
        {
            DebugTools.NamingVanillaCase = false;
            try { if (__instance != null && DebugTools.VanillaCaseIds.Contains(__instance.id)) DebugTools.NamingVanillaCase = true; } catch { }
        }

        static void Postfix(Case __instance)
        {
            try
            {
                if (!MurderSelector.EnableOverride || !MurderSelector.SeparateCasePerKiller || __instance == null) return;
                if (__instance.caseType != Case.CaseType.murder) return;
                int cid = -1; try { cid = __instance.id; } catch { }
                if (DebugTools.VanillaCaseIds.Contains(cid)) return;                                        // never rename a vanilla case
                if (!DebugTools.CaseToVictim.TryGetValue(cid, out int vid)) return;                         // not one of ours
                if (!DebugTools.CaseMonikers.TryGetValue(vid, out string mon) || string.IsNullOrEmpty(mon)) return;
                string def = "New Murder Case"; try { def = Strings.Get("ui.interface", "New Murder Case"); } catch { }
                string cur = null; try { cur = __instance.name; } catch { }
                if (string.IsNullOrEmpty(cur) || cur == def) return;   // vanilla hasn't named it yet -> keep generic until the form
                if (cur == mon) return;                                // already ours
                __instance.name = mon;
                try { var cpc = CasePanelController.Instance; if (cpc != null) cpc.UpdateCaseControls(); } catch { }
                MotivesPlugin.Log.LogInfo($"[SODMotives] case-tab: named case id={cid} '{mon}' (unique per case).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] case-tab error: {e.Message}"); }
            finally { DebugTools.NamingVanillaCase = false; }   // always clear the suppression flag set in the prefix
        }
    }

    // ===== Preserve a split case's crime scene until it is solved =====
    // Vanilla keeps ONE murder scene: when the next murder takes over, the previous Murder's OnCleanCrimeScene
    // tears the old scene down (Interactable.Delete/MarkAsTrash/RemoveFromPlacement on its placed items -- the
    // body among them, leaving only the blood pool). With our per-killer split the previous case is still OPEN,
    // so that teardown wrongly strips the body + evidence of a case the player hasn't solved yet (the reported
    // "the previous body/killer/victim got removed before I solved it"). We block OnCleanCrimeScene for OUR
    // murders while their case is still open, and let it run once the case is solved/closed (vanilla cleanup).
    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.OnCleanCrimeScene))]
    internal static class Patch_PreserveCrimeScene
    {
        static bool Prefix(MurderController.Murder __instance)
        {
            try
            {
                if (!MurderSelector.EnableOverride || !MurderSelector.SeparateCasePerKiller) return true;  // vanilla behaviour
                Human v = null; try { v = __instance.victim; } catch { }
                if (v == null) return true;
                if (!MurderSelector.OverriddenVictimIds.Contains(v.humanID)) return true;  // not one of ours
                if (VictimCaseFinished(v.humanID))
                {
                    MotivesPlugin.Log.LogInfo($"[SODMotives][diag] OnCleanCrimeScene ALLOWED for {MotivesPlugin.Name(v)} (case counts as finished) — vanilla cleanup runs.");
                    return true;                             // solved -> allow vanilla cleanup
                }
                MotivesPlugin.Log.LogInfo($"[SODMotives] preserve-scene: blocked crime-scene cleanup for {MotivesPlugin.Name(v)} (case still open; keeping body + evidence).");
                return false;  // skip the teardown so the body + evidence survive for the still-open case
            }
            catch { return true; }
        }

        // True if the victim's case has been solved/closed (so vanilla may clean its scene). Finds the case id
        // from DebugTools.CaseToVictim, then the Case in the panel's active/archived lists. Unknown -> false
        // (preserve), the safe default for an open case.
        internal static bool VictimCaseFinished(int victimId)
        {
            try
            {
                // The player has started resolving this case -> let vanilla's solve-cleanup run (status flips too
                // late to rely on during Case.Resolve, which was causing stuck bodies / inconsistent cleanup).
                if (DebugTools.SolvedVictims.Contains(victimId)) return true;
                int caseId = -1;
                foreach (var kv in DebugTools.CaseToVictim) if (kv.Value == victimId) { caseId = kv.Key; break; }
                if (caseId < 0) return false;
                var cpc = CasePanelController.Instance;
                if (cpc == null) return false;
                Case c = FindCaseById(cpc.activeCases, caseId);
                if (c == null) c = FindCaseById(cpc.archivedCases, caseId);
                if (c == null) return false;
                try { if (c.isSolved) return true; } catch { }
                try
                {
                    var s = c.caseStatus;
                    if (s == Case.CaseStatus.submitted || s == Case.CaseStatus.closable || s == Case.CaseStatus.archived) return true;
                }
                catch { }
                return false;
            }
            catch { return false; }
        }

        internal static Case FindCaseById(Il2CppSystem.Collections.Generic.List<Case> list, int caseId)
        {
            try
            {
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                {
                    Case c = null; try { c = list[i]; } catch { }
                    if (c == null) continue;
                    try { if (c.id == caseId) return c; } catch { }
                }
            }
            catch { }
            return null;
        }
    }

    // (Case naming is handled at the source now: each of our murders gets a unique monkierPre/monkierPost in
    // Patch_OnVictimDiscovery_SeparateCase.Postfix, so vanilla's own ApplyMonikerToCaseCheck names the tab AND the
    // resolution typewriter show the same unique moniker. No Case.name override -- that desynced the typewriter.)

    // Helper + a log-only Case.Resolve diagnostic. Playtest confirmed the body remover is Human.RemoveFromWorld
    // (handled by Patch_PreserveVictimBody above), NOT Case.Resolve -- the Resolve log never fired. Kept as a
    // cheap confirmation for when the player actually solves a case.
    internal static class CaseParticipants
    {
        internal static bool IsOurParticipant(Human h, out string role)
        {
            role = null;
            try
            {
                if (h == null) return false;
                int id = h.humanID;
                if (MurderSelector.OverriddenVictimIds.Contains(id)) { role = "our victim"; return true; }
                var mc = MurderController.Instance;
                if (mc != null)
                {
                    foreach (var list in new[] { mc.activeMurders, mc.inactiveMurders })
                    {
                        if (list == null) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var m = list[i]; if (m == null) continue;
                            try
                            {
                                if (m.murderer != null && m.murderer.humanID == id && m.victim != null
                                    && MurderSelector.OverriddenVictimIds.Contains(m.victim.humanID))
                                { role = "our killer"; return true; }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }

    // ===== Keep a split case's victim BODY at the scene until the case is solved =====
    // Confirmed from a playtest: when the NEXT murder starts, vanilla calls Human.RemoveFromWorld(true) on the
    // PREVIOUS victim -- despawning the body (blood pool remains) and relocating the citizen to a default street
    // (the player kept teleporting to that empty "Sherman Boulevard"). Vanilla keeps one body at a time; our split
    // keeps the old case open, so this strips a body the player still needs. We block the removal of OUR victims
    // while their case is unsolved, and allow it once solved (vanilla cleanup). Only val==true (the removal) is
    // blocked; val==false (restore to world) always passes. Scoped to dead victims only -- live NPCs (incl.
    // killers) are never blocked, so normal area-unload is untouched.
    [HarmonyPatch(typeof(Human), nameof(Human.RemoveFromWorld))]
    internal static class Patch_PreserveVictimBody
    {
        static bool Prefix(Human __instance, bool val)
        {
            try
            {
                if (!MurderSelector.EnableOverride || !MurderSelector.SeparateCasePerKiller || __instance == null) return true;
                if (!val) return true;   // only block the REMOVAL; always allow restore-to-world
                int id = __instance.humanID;
                if (!MurderSelector.OverriddenVictimIds.Contains(id)) return true;   // only our victims (bodies)
                if (Patch_PreserveCrimeScene.VictimCaseFinished(id)) return true;    // solved -> allow vanilla cleanup
                MotivesPlugin.Log.LogInfo($"[SODMotives] preserve-body: blocked RemoveFromWorld for {MotivesPlugin.Name(__instance)} (case still open; keeping the body at the scene).");
                return false;   // keep the body where it was killed, for the still-open case
            }
            catch { return true; }
        }
    }

    // NOTE: a previous attempt BLOCKED Objective.Cancel/Remove (return false) to keep a prior case's objectives.
    // That CRASHED/froze the game (the game clears objectives via a `while (list.Count>0) list[0].Remove()` loop,
    // and blocking left the list non-empty forever). NEVER block Objective.Remove. Instead we block the HIGHER-LEVEL
    // decommission below -- a single call, not a loop.

    // ===== Keep a split case's crime-scene STATUS until it is solved =====
    // The explore-crime-scene objective (and the crime-scene tape) drop for the OLDEST case once a THIRD opens -- the
    // game caps active crime scenes (~2) and DECOMMISSIONS the oldest via NewGameLocation.SetAsCrimeScene(false),
    // which strips its tape + "Explore Reported Crime Scene" objective. (With only 2 cases it never fires, matching
    // the report.) We block that decommission for OUR still-open cases' scenes so the scene + objective survive; once
    // the case is solved we allow it (vanilla cleanup, which also clears the body). Single call, no remove-loop, so
    // it's safe unlike the Objective.Remove block. Logs so the 3-case repro confirms the mechanism + timing.
    [HarmonyPatch(typeof(NewGameLocation), nameof(NewGameLocation.SetAsCrimeScene))]
    internal static class Patch_PreserveSceneStatus
    {
        static bool Prefix(NewGameLocation __instance, bool val)
        {
            try
            {
                if (!MurderSelector.EnableOverride || !MurderSelector.SeparateCasePerKiller || __instance == null) return true;
                if (val) return true;                        // only guard the DECOMMISSION (false); setting true is fine
                if (!IsOurOpenScene(__instance, out string vname)) return true;
                MotivesPlugin.Log.LogInfo($"[SODMotives] preserve-scene-status: blocked SetAsCrimeScene(false) for {vname}'s scene (case still open; keeping crime scene + objective).");
                return false;                                // keep the crime scene (tape + objective) for the open case
            }
            catch { return true; }
        }

        // True if this location is the murder scene of one of OUR still-unsolved cases.
        private static bool IsOurOpenScene(NewGameLocation loc, out string victimName)
        {
            victimName = "?";
            try
            {
                var mc = MurderController.Instance;
                if (mc == null || loc == null) return false;
                foreach (var list in new[] { mc.activeMurders, mc.inactiveMurders })
                {
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        MurderController.Murder m = null; try { m = list[i]; } catch { }
                        if (m == null) continue;
                        Human v = null; try { v = m.victim; } catch { }
                        if (v == null || !MurderSelector.OverriddenVictimIds.Contains(v.humanID)) continue;
                        if (Patch_PreserveCrimeScene.VictimCaseFinished(v.humanID)) continue;   // solved -> allow decommission
                        NewGameLocation ml = null; try { ml = m.location; } catch { }
                        if (ml != null && ml.Pointer == loc.Pointer) { victimName = MotivesPlugin.Name(v); return true; }
                    }
                }
            }
            catch { }
            return false;
        }
    }

    [HarmonyPatch(typeof(Case), nameof(Case.Resolve))]
    internal static class Patch_Diag_CaseResolve
    {
        static void Prefix(Case __instance)
        {
            try
            {
                if (!MurderSelector.EnableOverride || !MurderSelector.SeparateCasePerKiller || __instance == null) return;
                string nm = "?"; try { nm = __instance.name; } catch { }
                string st = "?"; try { st = __instance.caseStatus.ToString(); } catch { }
                string ty = "?"; try { ty = __instance.caseType.ToString(); } catch { }
                int id = -1; try { id = __instance.id; } catch { }
                // Our case only: a vanilla bundle case is never ours (defensive -- it shouldn't be in CaseToVictim).
                int vid = -1;
                bool ours = !DebugTools.VanillaCaseIds.Contains(id) && DebugTools.CaseToVictim.TryGetValue(id, out vid);
                // Latch the victim as "being solved" so the body/scene/objective blocks lift and vanilla's own
                // solve-cleanup runs consistently (fixes the stuck body + inconsistent cleanup).
                if (ours) DebugTools.SolvedVictims.Add(vid);
                MotivesPlugin.Log.LogInfo($"[SODMotives][diag] Case.Resolve() id={id} name='{nm}' status={st} type={ty} ours={ours}.");
            }
            catch { }
        }

        // ===== Clear THE RESOLVED case's own body =====
        // Vanilla's solve-cleanup (MurderController.OnCaseSolved / the rest of Case.Resolve) removes the CURRENT
        // murder's victim (GetCurrentMurder) -- fine in vanilla's one-case-at-a-time model, but with our per-killer
        // split the resolved case is usually NOT the active/current one, so only the case that was active when the
        // forms were collected ever had its body cleared; every other solved case left its body at the scene.
        // The Prefix above latched this victim into SolvedVictims, so Patch_PreserveVictimBody now ALLOWS the removal
        // (and still BLOCKS vanilla's misdirected removal of any still-unsolved current victim). Here we remove THIS
        // resolved case's own victim explicitly, once, so each solved case clears its own body. RemoveFromWorld(true)
        // relocates the citizen to a default street exactly as vanilla does -- no extra teardown (OnCleanCrimeScene
        // strips furniture, which we deliberately avoid).
        static void Postfix(Case __instance)
        {
            try
            {
                if (!MurderSelector.EnableOverride || !MurderSelector.SeparateCasePerKiller || __instance == null) return;
                int id = -1; try { id = __instance.id; } catch { }
                if (DebugTools.VanillaCaseIds.Contains(id)) return;                  // vanilla case -> the base game clears its own bodies
                if (!DebugTools.CaseToVictim.TryGetValue(id, out int vid)) return;   // not one of ours
                if (DebugTools.BodiesCleared.Contains(vid)) return;                  // already cleared this body

                var mc = MurderController.Instance;
                if (mc == null) return;
                var m = DebugTools.FindMurderByVictim(mc, vid);
                Human victim = null; try { if (m != null) victim = m.victim; } catch { }
                if (victim == null) return;

                DebugTools.BodiesCleared.Add(vid);   // guard BEFORE the call so a re-entrant Resolve can't double-fire
                MotivesPlugin.Log.LogInfo($"[SODMotives] solve-cleanup: removing {MotivesPlugin.Name(victim)}'s body for resolved case id={id} (RemoveFromWorld).");
                try { victim.RemoveFromWorld(true); }
                catch (Exception ex) { MotivesPlugin.Log.LogWarning($"[SODMotives] solve-cleanup: RemoveFromWorld failed for vid={vid}: {ex.Message}"); }
            }
            catch { }
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
        // Block the kidnapper's premature KILL (our motivated kidnaps only): our victim-swap leaves the kidnap's
        // kill timers expired, so the game tries to kill the held victim almost immediately. Skip the kill's
        // state transitions until our fair deadline (MurderWatchdog decides); the victim stays held + rescuable.
        static bool Prefix(MurderController.Murder __instance, MurderController.MurderState newState)
        {
            try { if (MurderWatchdog.ShouldBlockKidnapKill(__instance, newState)) return false; }
            catch { }
            return true;
        }

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

                // Track 'executing' entry/exit for the stall watchdog (recovers our overridden cases
                // that soft-lock because the killer never lands a lethal blow — see MurderWatchdog).
                MurderWatchdog.OnState(__instance.victim.humanID, newState);

                // For kidnaps: note once the case has reached the HOLD (post/escaping/unsolved). After that, a
                // return to travellingTo/executing is the KILL (not the abduction) — used to block it until the
                // fair deadline (MurderWatchdog.ShouldBlockKidnapKill).
                try
                {
                    if (__instance.preset != null && __instance.preset.caseType == MurderPreset.CaseType.kidnap
                        && (newState == MurderController.MurderState.post || newState == MurderController.MurderState.escaping || newState == MurderController.MurderState.unsolved))
                        MurderWatchdog.KidnapReachedHold.Add(__instance.victim.humanID);
                }
                catch { }

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

    // NOTE: the former Patch_SniperVantage_ForceLosNest postfix on Toolbox.TryGetSniperVantagePoint was REMOVED
    // (hotfix). Harmony-patching that method installs a native detour at its entry, so every caller -- the game's own
    // Murder.TryPickNewVictimSite candidate scoring and NewAIAction.OnActivate fire-time nest derivation included -- was
    // routed through an Il2CppInterop native->managed marshalling trampoline. On some players' BepInEx/Il2CppInterop
    // builds that trampoline threw a NullReferenceException (the reported "During invoking native->managed trampoline"
    // error); Il2CppInterop swallowed it and returned false + out NewWall=null, so ExCop snipers could never derive a
    // nest and hung. There is no settable nest field, so forcing a specific window REQUIRES patching this solver, which
    // is exactly what reintroduces the hazard -- so we no longer force a nest at all. Motivated snipers now defer the
    // site's nest + the shot entirely to the game (mimic-vanilla), exactly as docs/extensions/sniper-vanilla-excop-decode.md
    // concluded; the mod only chooses the pair + MO and seeds a (varied, ranked) shot SITE via SeedSniperDefault.

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

            // Verbose calibration (acquaintance dumps + motive-engine dry-run + one-time citywide scan) only
            // under [Debug] EnableDebugKeys — a shipped build keeps the concise summary above.
            if (DebugTools.EnableDebugKeys)
            {
                MotivesPlugin.Describe("MURDERER info", newMurderer);
                MotivesPlugin.Describe("VICTIM   info", newVictim);
                MotivesPlugin.LogTopNegative("MURDERER feelings", newMurderer);
                MotivesPlugin.LogTopNegative("VICTIM feelings", newVictim);

                // ---- DRY RUN: what the motive engine WOULD do ----
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
                // One-time citywide calibration dump on the first observed murder.
                CityScan.RunOnce();
            }
            log.LogInfo("[SODMotives] ==================================================");
        }
    }
}

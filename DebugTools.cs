using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;

namespace SODMotives
{
    // Testing aids (no console needed):
    //   F9  = toggle an on-screen overlay with the current case's solution + suspect shortlist
    //   F10 = teleport the player to the crime scene
    internal static class DebugTools
    {
        internal static readonly List<string> Overlay = new List<string>();
        internal static bool Show = false;
        internal static bool Ghost = false;         // NPCs ignore the player (testing aid)
        internal static bool AlwaysAnswer = false;  // NPCs always accept "do you know this person?" (no bribe)

        internal static void Register()
        {
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<DebugHotkey>();
                var go = new GameObject("SODMotives_Debug");
                GameObject.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<DebugHotkey>();
                MotivesPlugin.Log.LogInfo("[SODMotives] Debug keys: F9=case solution, F10=teleport to scene, F11=teleport to victim's work, F12=teleport to nearest affair-knower, F8=toggle ALWAYS-ANSWER (no bribe), F7=toggle GHOST MODE (NPCs ignore you).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] hotkey register failed: {e.Message}"); }
        }

        // Keep the player unnoticed while ghost mode is on (called every frame).
        internal static void ApplyGhost()
        {
            try
            {
                var p = Player.Instance;
                if (p == null) return;
                try { p.heat = 0f; } catch { }
                try { p.isTrespassing = false; } catch { }
                try { p.unreportable = true; } catch { }
                try { p.spottedState = 0f; } catch { }
                // Invincibility: even if NPCs/cameras still aggro, don't let them kill you.
                try { p.currentHealth = p.GetCurrentMaxHealth(); } catch { }
                try { p.isStunned = false; } catch { }
            }
            catch { }
        }

        internal static void ClearGhost()
        {
            try { var p = Player.Instance; if (p != null) { try { p.unreportable = false; } catch { } } } catch { }
        }

        internal static void TeleportToWork()
        {
            var log = MotivesPlugin.Log;
            try
            {
                var mc = MurderController.Instance;
                Human victim = null;
                try { var m = mc != null ? mc.GetCurrentMurder() : null; victim = m != null ? m.victim : null; } catch { }
                if (victim == null && mc != null) victim = mc.currentVictim;
                if (victim == null) { log.LogInfo("[SODMotives][F11] No victim."); return; }
                NewGameLocation work = null;
                try { if (victim.job != null && victim.job.employer != null) work = victim.job.employer.placeOfBusiness; } catch { }
                if (work == null) { log.LogInfo("[SODMotives][F11] Victim has no workplace."); return; }
                var player = Player.Instance;
                NewNode node = player.FindSafeTeleport(work, false, true);
                if (node == null) { log.LogInfo("[SODMotives][F11] No safe spot at the workplace."); return; }
                player.Teleport(node, null, true, false, true);
                log.LogInfo($"[SODMotives][F11] Teleported to victim's workplace: {work.name}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][F11] work teleport error: {e}"); }
        }

        private static bool TryGetCase(out Human killer, out Human victim, out string scene)
        {
            killer = null; victim = null; scene = "?";
            var mc = MurderController.Instance;
            if (mc == null) return false;
            try
            {
                var murder = mc.GetCurrentMurder();
                if (murder != null)
                {
                    killer = murder.murderer;
                    victim = murder.victim;
                    if (murder.location != null) scene = murder.location.name;
                }
            }
            catch { }
            if (killer == null) killer = mc.currentMurderer;
            if (victim == null) victim = mc.currentVictim;
            return killer != null || victim != null;
        }

        internal static void BuildSolution()
        {
            Overlay.Clear();
            var log = MotivesPlugin.Log;
            try
            {
                if (!TryGetCase(out Human killer, out Human victim, out string scene))
                {
                    Overlay.Add("No active murder case right now.");
                    return;
                }

                bool ours = false;
                try { ours = victim != null && MurderSelector.OverriddenVictimIds.Contains(victim.humanID); } catch { }

                Overlay.Add(ours ? "CASE TYPE: motivated (mod)" : "CASE TYPE: vanilla / not overridden");
                try { var mrd = MurderController.Instance != null ? MurderController.Instance.GetCurrentMurder() : null; if (mrd != null) Overlay.Add($"MURDER STATE: {mrd.state}"); } catch { }
                Overlay.Add($"KILLER: {MotivesPlugin.Name(killer)}");
                Overlay.Add($"VICTIM: {MotivesPlugin.Name(victim)}");
                Overlay.Add($"SCENE : {scene}");

                if (killer != null && victim != null)
                {
                    var mot = Motive.Score(killer, victim);
                    Overlay.Add("MOTIVE: " + (mot.HasMotive
                        ? $"[{mot.type}] {mot.detail} (score {MotivesPlugin.F(mot.score)})"
                        : "none detected (unmotivated / vanilla)"));
                }

                if (victim != null)
                {
                    var city = CityData.Instance;
                    if (city != null && city.citizenDirectory != null)
                    {
                        var cits = city.citizenDirectory;
                        var suspects = new List<(float score, string line)>();
                        for (int i = 0; i < cits.Count; i++)
                        {
                            Human a = cits[i];
                            if (a == null || Motive.Same(a, victim)) continue;
                            var m = Motive.Score(a, victim);
                            if (m.HasMotive)
                                suspects.Add((m.score, $"  {MotivesPlugin.F(m.score).PadLeft(6)}  {MotivesPlugin.Name(a)}  [{m.type}] {m.detail}"));
                        }
                        suspects.Sort((x, y) => y.score.CompareTo(x.score));
                        Overlay.Add($"SUSPECT SHORTLIST ({suspects.Count} with a motive toward the victim):");
                        int show = Math.Min(8, suspects.Count);
                        for (int i = 0; i < show; i++) Overlay.Add(suspects[i].line);
                    }

                    // Injected clues for this case.
                    try
                    {
                        if (ClueInjector.CluesByVictim.TryGetValue(victim.humanID, out var clues) && clues.Count > 0)
                        {
                            Overlay.Add($"INJECTED CLUES ({clues.Count}):");
                            foreach (var c in clues) Overlay.Add("  " + c);
                        }
                        else Overlay.Add("INJECTED CLUES: none yet (spawns when the game places case items)");
                    }
                    catch { }

                    // Affair case: who to interview, nearest the scene first (F12 jumps to the closest).
                    try
                    {
                        if (MurderSelector.AffairByVictim.TryGetValue(victim.humanID, out var affair) && affair != null)
                        {
                            Vector3 scenePos = ScenePos(victim);
                            Overlay.Add("NEAREST KNOWERS (interview these; F12 = jump to closest):");
                            foreach (var ln in affair.NearestKnowers(scenePos, 6)) Overlay.Add("  " + ln);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e) { Overlay.Add($"error: {e.Message}"); log.LogWarning($"[SODMotives][F9] {e}"); }

            // Mirror to log too.
            log.LogInfo("[SODMotives][F9] ---- case solution ----");
            foreach (var l in Overlay) log.LogInfo("[SODMotives][F9]   " + l);
        }

        // Scene position: the real crime scene once set, else the victim's home.
        private static Vector3 ScenePos(Human victim)
        {
            try
            {
                var mc = MurderController.Instance;
                var m = mc != null ? mc.GetCurrentMurder() : null;
                if (m != null && m.location != null && m.location.anchorNode != null) return m.location.anchorNode.position;
            }
            catch { }
            try { if (victim != null && victim.home != null && victim.home.anchorNode != null) return victim.home.anchorNode.position; }
            catch { }
            return default;
        }

        // F12: teleport to the home of the affair-knower nearest the scene, so you can
        // interview a gossip without hunting for one.
        internal static void TeleportToNearestKnower()
        {
            var log = MotivesPlugin.Log;
            try
            {
                if (!TryGetCase(out _, out Human victim, out _) || victim == null)
                { log.LogInfo("[SODMotives][F12] No victim."); return; }
                if (!MurderSelector.AffairByVictim.TryGetValue(victim.humanID, out var affair) || affair == null)
                { log.LogInfo("[SODMotives][F12] Not a mod affair case (no seeded gossips)."); return; }

                Human k = affair.NearestKnowerHuman(ScenePos(victim));
                if (k == null || k.home == null) { log.LogInfo("[SODMotives][F12] No knower with a home found."); return; }
                var player = Player.Instance;
                NewNode node = player.FindSafeTeleport(k.home, false, true);
                if (node == null) { log.LogInfo("[SODMotives][F12] No safe spot at the knower's home."); return; }
                player.Teleport(node, null, true, false, true);
                log.LogInfo($"[SODMotives][F12] Teleported to nearest knower {MotivesPlugin.Name(k)} @ {k.home.name}.");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][F12] nearest-knower teleport error: {e}"); }
        }

        internal static void TeleportToScene()
        {
            var log = MotivesPlugin.Log;
            try
            {
                var mc = MurderController.Instance;
                var murder = mc != null ? mc.GetCurrentMurder() : null;
                if (murder == null || murder.location == null) { log.LogInfo("[SODMotives][F10] No crime scene to teleport to."); return; }
                var player = Player.Instance;
                if (player == null) { log.LogInfo("[SODMotives][F10] No player."); return; }
                NewNode node = player.FindSafeTeleport(murder.location, false, true);
                if (node == null) { log.LogInfo("[SODMotives][F10] Could not find a safe spot at the scene."); return; }
                player.Teleport(node, null, true, false, true);
                log.LogInfo($"[SODMotives][F10] Teleported to crime scene: {murder.location.name}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][F10] teleport error: {e}"); }
        }
    }

    // Injected MonoBehaviour: polls hotkeys and draws the overlay.
    public class DebugHotkey : MonoBehaviour
    {
        private static GUIStyle _style;

        public DebugHotkey(IntPtr ptr) : base(ptr) { }

        void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    if (DebugTools.Show) { DebugTools.Show = false; }
                    else { DebugTools.BuildSolution(); DebugTools.Show = true; }
                }
                if (Input.GetKeyDown(KeyCode.F10)) DebugTools.TeleportToScene();
                if (Input.GetKeyDown(KeyCode.F11)) DebugTools.TeleportToWork();
                if (Input.GetKeyDown(KeyCode.F12)) DebugTools.TeleportToNearestKnower();
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    DebugTools.AlwaysAnswer = !DebugTools.AlwaysAnswer;
                    MotivesPlugin.Log.LogInfo($"[SODMotives] ALWAYS-ANSWER (no bribe) {(DebugTools.AlwaysAnswer ? "ON" : "OFF")}");
                }
                if (Input.GetKeyDown(KeyCode.F7))
                {
                    DebugTools.Ghost = !DebugTools.Ghost;
                    if (!DebugTools.Ghost) DebugTools.ClearGhost();
                    MotivesPlugin.Log.LogInfo($"[SODMotives] GHOST MODE {(DebugTools.Ghost ? "ON" : "OFF")}");
                }
                if (DebugTools.Ghost) DebugTools.ApplyGhost();
                Interrogation.TickWatch();   // diagnostics: deferred speech-bubble read
            }
            catch { }
        }

        void OnGUI()
        {
            // Ghost indicator (always visible while on).
            if (DebugTools.Ghost)
            {
                try
                {
                    if (_style == null) _style = new GUIStyle { fontSize = 14, wordWrap = false };
                    _style.normal.textColor = Color.green;
                    GUI.Label(new Rect(8, Screen.height - 26, 500, 20), "GHOST MODE ON (F7) - NPCs ignore you", _style);
                }
                catch { }
            }

            // Always-answer indicator.
            if (DebugTools.AlwaysAnswer)
            {
                try
                {
                    if (_style == null) _style = new GUIStyle { fontSize = 14, wordWrap = false };
                    _style.normal.textColor = Color.cyan;
                    GUI.Label(new Rect(8, Screen.height - 46, 520, 20), "ALWAYS-ANSWER ON (F8) - NPCs never refuse 'do you know this person?'", _style);
                }
                catch { }
            }

            if (!DebugTools.Show) return;
            try
            {
                if (_style == null)
                    _style = new GUIStyle { fontSize = 14, wordWrap = false, richText = false };
                _style.normal.textColor = Color.white;

                int count = DebugTools.Overlay.Count;
                float lineH = 20f;
                float w = 720f;
                float h = 40f + lineH * (count + 1);
                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.Box(new Rect(8, 8, w, h), GUIContent.none);
                GUI.color = Color.white;
                GUI.Label(new Rect(18, 14, w - 20, lineH), "SOD MOTIVES — case solution (F9 to hide, F10 to teleport)", _style);
                for (int i = 0; i < count; i++)
                    GUI.Label(new Rect(18, 14 + lineH * (i + 1.4f), w - 20, lineH), DebugTools.Overlay[i], _style);
            }
            catch { }
        }
    }
}

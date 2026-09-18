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
        // Persistent bottom-left readout of where the current case's clues were injected (so you can
        // find them without hunting). Populated by ClueInjector; cleared when a new case injects.
        internal static readonly List<string> ClueHud = new List<string>();
        internal static void AddClueHud(string line)
        {
            try { if (!string.IsNullOrEmpty(line)) { ClueHud.Add(line); while (ClueHud.Count > 8) ClueHud.RemoveAt(0); } } catch { }
        }
        internal static bool Show = false;
        internal static bool Ghost = false;         // NPCs ignore the player (testing aid)
        internal static bool AlwaysAnswer = false;  // NPCs always accept "do you know this person?" (no bribe)

        // F8 HUD readout: the last NPC who spoke to the player (so you can always identify who you're
        // talking to — the game has no forceable "tell me your name" dialog). Set from Interrogation's
        // speech-bubble hook; ignores the player's own lines.
        internal static string TalkingToLabel = null;
        internal static void NoteTalkingTo(Human h)
        {
            try
            {
                if (h == null) return;
                var p = Player.Instance;
                if (p != null && h.humanID == p.humanID) return;   // ignore the player's own speech
                TalkingToLabel = MotivesPlugin.Name(h);
            }
            catch { }
        }
        internal static MotiveType ForceMotiveType = MotiveType.None;  // F6: restrict the NEXT murder's suspect pool to one motive type (kept in sync with ForceEventType; drives the selector's bucketing skip)
        // F6 (finer, V2.6.3): restrict to a specific SocialEventType (e.g. Layoffs vs Promotion, both Professional).
        // null = don't refine by event type (fall back to the ForceMotiveType filter only). When set, F6 also sets
        // ForceMotiveType to the mapped motive so the selector's existing motive filter + bucketing-skip still fire.
        internal static SocialEventType? ForceEventType = null;

        // Configurable debug hotkeys — bound to the [Debug Keys] config section in Plugin.Load and
        // re-applied live from the in-game overlay (which renders KeyCode as a key-binder). Defaults are
        // the original F-keys (F5 left free for the game's quicksave + the config-menu toggle).
        internal static KeyCode KeyCaseSolution   = KeyCode.F9;
        internal static KeyCode KeyCycleForce     = KeyCode.F6;
        internal static KeyCode KeyGhost          = KeyCode.F7;
        internal static KeyCode KeyAlwaysAnswer   = KeyCode.F8;
        internal static KeyCode KeyTeleportScene  = KeyCode.F10;
        internal static KeyCode KeyTeleportWork   = KeyCode.F11;
        internal static KeyCode KeyTeleportKnower = KeyCode.F12;

        // Each SocialEventType maps to exactly one MotiveType — used to keep ForceMotiveType in sync with F6.
        internal static MotiveType MotiveOf(SocialEventType t)
        {
            switch (t)
            {
                case SocialEventType.Affair: return MotiveType.Infidelity;
                case SocialEventType.Promotion:
                case SocialEventType.Layoffs: return MotiveType.Professional;
                case SocialEventType.Eviction:
                case SocialEventType.RentArrears:
                case SocialEventType.Debt: return MotiveType.Money;
                case SocialEventType.Feud: return MotiveType.PersonalFeud;
                default: return MotiveType.None;
            }
        }

        // Short label for the current F6 force (event type if set, else motive type, else OFF).
        internal static string ForceLabel()
        {
            if (ForceEventType.HasValue) return ForceEventType.Value.ToString();
            if (ForceMotiveType != MotiveType.None) return ForceMotiveType.ToString();
            return "OFF";
        }

        internal static void Register()
        {
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<DebugHotkey>();
                var go = new GameObject("SODMotives_Debug");
                GameObject.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<DebugHotkey>();
                MotivesPlugin.Log.LogInfo("[SODMotives] Debug keys (defaults; rebindable in the config menu -> SOD Motives / Debug Keys): F6=cycle FORCE EVENT (off/affair/promotion/layoffs/eviction/rentarrears/feud/debt), F7=ghost, F8=always-answer, F9=case solution, F10=teleport to scene, F11=to victim's work, F12=to nearest case-knower. (F3/F4/F5 unbound.)");
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

        // F3: inject a test EMAIL (vmail) between two live citizens so inbox rendering + clickable links can
        // be verified without a murder (V2.6 email-clue recon). Read it on either participant's home computer.
        internal static void SpawnTestEmail()
        {
            var log = MotivesPlugin.Log;
            try
            {
                log.LogInfo("[SODMotives][F3] injecting a test email between two housed citizens...");
                bool ok = ClueInjector.SpawnTestEmail();
                log.LogInfo($"[SODMotives][F3] test email {(ok ? "injected — see the [F3] line for whose home computer to read it on (Messenger/vmail app)" : "FAILED")}.");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][F3] error: {e}"); }
        }

        // F4: drop the THREATENING debt note into the player's apartment right now (handwritten, landlord's
        // hand + print, writer connection hidden) — iterate the real rent-arrears clue with no murder needed.
        internal static void SpawnTestNote()
        {
            var log = MotivesPlugin.Log;
            try
            {
                var player = Player.Instance;
                if (player == null) { log.LogInfo("[SODMotives][F4] No player."); return; }
                NewGameLocation loc = null;
                try { loc = player.home; } catch { }
                if (loc == null) { log.LogInfo("[SODMotives][F4] No player home — enable 'start with apartment' in gameplay settings."); return; }

                // Pick any live citizen (not the player) to be the note's "landlord": its handwriting + print.
                Human landlord = null;
                try
                {
                    var dir = CityData.Instance != null ? CityData.Instance.citizenDirectory : null;
                    if (dir != null)
                        for (int i = 0; i < dir.Count; i++)
                        {
                            var c = dir[i]; if (c == null) continue;
                            Human h = null; try { h = c.TryCast<Human>(); } catch { }
                            if (h == null) continue;
                            try { if (h.humanID == player.humanID) continue; } catch { }
                            try { if (h.isDead) continue; } catch { }
                            landlord = h; break;
                        }
                }
                catch (Exception e) { log.LogWarning($"[SODMotives][F4] citizen gather: {e.Message}"); }

                if (landlord == null) { log.LogInfo("[SODMotives][F4] No citizen found to use as the landlord."); return; }
                log.LogInfo($"[SODMotives][F4] spawning THREATENING test note at {loc.name} — handwriting/print = {MotivesPlugin.Name(landlord)}...");
                bool ok = ClueInjector.SpawnTestThreatNote(loc, landlord);
                log.LogInfo($"[SODMotives][F4] test note {(ok ? "placed — read it; check it's handwritten, matches " + MotivesPlugin.Name(landlord) + "'s hand, and shows NO 'from' connection" : "FAILED")} (see [TEST] lines).");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][F4] error: {e}"); }
        }

        // F5: force the game to run its next murder NOW (testing) — combine with F6=Professional to
        // get a workplace case fast instead of waiting for / re-rolling sandboxes. Our override still
        // gates on proc-gen + caseType==murder, so a triggered kidnap/sniper is left to vanilla.
        internal static void TriggerMurder()
        {
            var log = MotivesPlugin.Log;
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) { log.LogInfo("[SODMotives][F5] no MurderController."); return; }
                log.LogInfo($"[SODMotives][F5] triggering next murder (Force={ForceLabel()})...");
                mc.TriggerNextMurder();
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][F5] trigger error: {e.Message}"); }
        }

        // F6: cycle which SPECIFIC event type the NEXT murder is forced to (finer than the old motive-type
        // cycle — Layoffs vs Promotion, Eviction vs RentArrears vs Debt are now separable). The filter is
        // applied in TryPickVictimCentric (which also narrows by the mapped motive + skips case-type
        // balancing while a force is active). Order: OFF -> Affair -> Promotion -> Layoffs -> Eviction ->
        // RentArrears -> Feud -> Debt -> OFF. Setting an event type also sets ForceMotiveType to its motive.
        private static readonly SocialEventType[] _forceCycle =
        {
            SocialEventType.Affair, SocialEventType.Promotion, SocialEventType.Layoffs,
            SocialEventType.Eviction, SocialEventType.RentArrears, SocialEventType.Feud, SocialEventType.Debt,
        };
        internal static void CycleForceMotive()
        {
            // Advance to the next entry; wrap past the end back to OFF (null).
            if (!ForceEventType.HasValue) ForceEventType = _forceCycle[0];
            else
            {
                int idx = Array.IndexOf(_forceCycle, ForceEventType.Value);
                ForceEventType = (idx < 0 || idx + 1 >= _forceCycle.Length) ? (SocialEventType?)null : _forceCycle[idx + 1];
            }
            ForceMotiveType = ForceEventType.HasValue ? MotiveOf(ForceEventType.Value) : MotiveType.None;
            MotivesPlugin.Log.LogInfo($"[SODMotives] FORCE EVENT = {(ForceEventType.HasValue ? ForceEventType.Value.ToString() + " (motive " + ForceMotiveType + ")" : "OFF (any motive)")} — applies to the NEXT new murder.");
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

                // MOTIVE + SUSPECT POOL now read the REAL event-backed pool the killer was drawn
                // from (MurderSelector), not the retired like-based Motive.Score.
                if (victim != null && MurderSelector.MotiveByVictim.TryGetValue(victim.humanID, out var kmot))
                    Overlay.Add($"MOTIVE: [{kmot.type}] {kmot.detail} (score {MotivesPlugin.F(kmot.score)})");
                else if (killer != null && victim != null)
                    Overlay.Add("MOTIVE: (vanilla / not overridden)");

                if (victim != null)
                {
                    if (MurderSelector.PoolByVictim.TryGetValue(victim.humanID, out var pool) && pool != null && pool.Count > 0)
                    {
                        Overlay.Add($"SUSPECT POOL ({pool.Count} real event-backed suspects; killer *):");
                        int show = Math.Min(10, pool.Count);
                        for (int i = 0; i < show; i++)
                        {
                            var ed = pool[i];
                            bool isK = killer != null && ed.suspect != null && ed.suspect.humanID == killer.humanID;
                            Overlay.Add($"  {(isK ? "*" : " ")}{MotivesPlugin.F(ed.score).PadLeft(6)}  {MotivesPlugin.Name(ed.suspect)}  [{ed.type}] {ed.detail}");
                        }
                    }
                    else Overlay.Add("SUSPECT POOL: none recorded (vanilla case / not overridden).");

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

                    // Combined interview list: NPCs who BOTH know the victim personally (can name them
                    // from the face-only body profile) AND know a motive event about them — the only ones
                    // who, shown the victim's photo, will identify them AND volunteer the gossip. Nearest
                    // the scene first (F12 = jump to closest); shows which events each one knows.
                    try { AddVictimKnowers(victim, ScenePos(victim)); } catch { }

                    // City-wide multi-span subjects to interview about (a guaranteed multi-event target).
                    try { AddKnowerBreakdown(victim); } catch { }
                }
            }
            catch (Exception e) { Overlay.Add($"error: {e.Message}"); log.LogWarning($"[SODMotives][F9] {e}"); }

            // Mirror to log too.
            log.LogInfo("[SODMotives][F9] ---- case solution ----");
            foreach (var l in Overlay) log.LogInfo("[SODMotives][F9]   " + l);
        }

        // The combined victim-interview list: every NPC who KNOWS THE VICTIM'S NAME (can identify the
        // face-only body profile) AND knows a motive event involving the victim — so, shown the photo,
        // they'll both name them and volunteer the gossip. Nearest the scene first; shows which events.
        private static void AddVictimKnowers(Human victim, Vector3 scenePos)
        {
            if (victim == null) return;
            int vid = victim.humanID;

            // events about the victim, tallied per knower and per type.
            var perKnower = new Dictionary<int, Dictionary<SocialEventType, int>>();
            foreach (var e in EventStore.All)
            {
                if (!e.InvolvesHuman(vid)) continue;
                foreach (var kid in e.knownBy)
                {
                    // A participant of the event is not a "knower" of it — they lived it, they don't
                    // gossip it (this drops the killer/other party from their own event, matching the
                    // interrogation rule). Subsumes the old kid==vid guard (the victim is a participant).
                    if (e.InvolvesHuman(kid)) continue;
                    if (!perKnower.TryGetValue(kid, out var m)) { m = new Dictionary<SocialEventType, int>(); perKnower[kid] = m; }
                    m.TryGetValue(e.type, out int c); m[e.type] = c + 1;
                }
            }

            var dir = CityData.Instance != null ? CityData.Instance.citizenDirectory : null;
            var rows = new List<(float d, string line)>();
            if (dir != null)
                for (int i = 0; i < dir.Count; i++)
                {
                    var h = dir[i]; if (h == null) continue;
                    if (!perKnower.TryGetValue(h.humanID, out var m)) continue;   // knows no event about the victim
                    if (!Motive.KnowsName(h, victim)) continue;                    // can't name the victim -> skip
                    NewAddress home = null; try { home = h.home; } catch { }
                    float dist = float.MaxValue; bool hasPos = false; string addr = "?"; int floor = 0, bld = -1;
                    if (home != null)
                    {
                        try { var an = home.anchorNode; if (an != null) { dist = Vector3.Distance(scenePos, an.position); hasPos = true; } } catch { }
                        try { addr = home.name; } catch { }
                        try { if (home.floor != null) floor = home.floor.floor; } catch { }
                        try { if (home.building != null) bld = home.building.buildingID; } catch { }
                    }
                    string dtxt = hasPos ? $"{dist:0}m" : "?m";
                    rows.Add((dist, $"{MotivesPlugin.Name(h)} — {addr} (bldg {bld}/floor {floor}) — {dtxt} — [{FmtTypes(m)}]"));
                }
            rows.Sort((x, y) => x.d.CompareTo(y.d));
            Overlay.Add("VICTIM KNOWERS — know the victim + a motive event (F12 = jump to closest):");
            if (rows.Count == 0) Overlay.Add("  (nobody both knows the victim's name AND a motive event about them)");
            for (int i = 0; i < rows.Count && i < 8; i++) Overlay.Add("  " + rows[i].line);
        }

        // A guaranteed multi-span subject to interview about: city-wide subjects who appear in >=2
        // events, so multi-span gossip is always testable even when the victim is in only one event.
        private static void AddKnowerBreakdown(Human victim)
        {
            if (victim == null) return;
            var dir = CityData.Instance != null ? CityData.Instance.citizenDirectory : null;

            // City-wide subjects in >=2 events (any) — a guaranteed multi-span subject to test.
            var subj = new Dictionary<int, Dictionary<SocialEventType, int>>();
            foreach (var e in EventStore.All)
            {
                TallySubj(subj, e.a, e.type);
                TallySubj(subj, e.b, e.type);
                for (int i = 0; i < e.group.Count; i++) TallySubj(subj, e.group[i], e.type);
            }
            if (dir != null)
            {
                var multi = new List<(int total, string line)>();
                for (int i = 0; i < dir.Count; i++)
                {
                    var h = dir[i]; if (h == null) continue;
                    if (!subj.TryGetValue(h.humanID, out var m)) continue;
                    int total = 0; foreach (var kv in m) total += kv.Value;
                    if (total < 2) continue;
                    multi.Add((total, $"{MotivesPlugin.Name(h)} — {FmtTypes(m)} — {BestKnowerLabel(h.humanID)}"));
                }
                if (multi.Count > 0)
                {
                    multi.Sort((x, y) => y.total.CompareTo(x.total));
                    Overlay.Add("MULTI-EVENT SUBJECTS (ask a knower about one of THESE to test multi-span):");
                    for (int i = 0; i < multi.Count && i < 5; i++) Overlay.Add("  " + multi[i].line);
                }
            }
        }

        // The NPC who knows the MOST events involving `subjectId` (>=2), for a one-step multi-span
        // test ("ask <name> about <subject>"). Returns a hint when no single knower knows 2+.
        private static string BestKnowerLabel(int subjectId)
        {
            var perK = new Dictionary<int, int>();
            foreach (var e in EventStore.All)
            {
                if (!e.InvolvesHuman(subjectId)) continue;
                foreach (var kid in e.knownBy)
                {
                    if (e.InvolvesHuman(kid)) continue;   // participants aren't knowers (subsumes kid==subjectId)
                    perK.TryGetValue(kid, out int c); perK[kid] = c + 1;
                }
            }
            int bestId = -1, best = 0;
            foreach (var kv in perK) if (kv.Value > best) { best = kv.Value; bestId = kv.Key; }
            if (bestId < 0 || best < 2) return "(no single knower knows 2+)";
            var dir = CityData.Instance != null ? CityData.Instance.citizenDirectory : null;
            if (dir != null)
                for (int i = 0; i < dir.Count; i++)
                {
                    var h = dir[i];
                    if (h != null && h.humanID == bestId) return $"ask {MotivesPlugin.Name(h)} (knows {best})";
                }
            return $"(knower id {bestId}, knows {best})";
        }

        private static void TallySubj(Dictionary<int, Dictionary<SocialEventType, int>> map, Human h, SocialEventType t)
        {
            if (h == null) return;
            int id; try { id = h.humanID; } catch { return; }
            if (!map.TryGetValue(id, out var m)) { m = new Dictionary<SocialEventType, int>(); map[id] = m; }
            m.TryGetValue(t, out int c); m[t] = c + 1;
        }

        private static string FmtTypes(Dictionary<SocialEventType, int> m)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in m) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{kv.Value} {kv.Key}"); }
            return sb.ToString();
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

        // F12: teleport to the home of the case-knower nearest the scene (affair OR workplace),
        // so you can interview a gossip without hunting for one.
        internal static void TeleportToNearestKnower()
        {
            var log = MotivesPlugin.Log;
            try
            {
                if (!TryGetCase(out _, out Human victim, out _) || victim == null)
                { log.LogInfo("[SODMotives][F12] No victim."); return; }
                if (!MurderSelector.EventByVictim.TryGetValue(victim.humanID, out var evt) || evt == null)
                { log.LogInfo("[SODMotives][F12] Not a mod motive case (no event-backed knowers)."); return; }

                Human k = evt.NearestKnowerHuman(ScenePos(victim), victim);
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
        private static GUIStyle _style;      // F9 case-solution overlay (top-left, left-aligned)
        private static GUIStyle _hudStyle;   // debug-key indicators + clue locations (top-right, right-aligned)

        public DebugHotkey(IntPtr ptr) : base(ptr) { }

        void Update()
        {
            try
            {
                // Debug hotkeys are configurable (DebugTools.Key* — bound to [Debug Keys], live from the overlay).
                if (Input.GetKeyDown(DebugTools.KeyCaseSolution))
                {
                    if (DebugTools.Show) { DebugTools.Show = false; }
                    else { DebugTools.BuildSolution(); DebugTools.Show = true; }
                }
                if (Input.GetKeyDown(DebugTools.KeyCycleForce)) DebugTools.CycleForceMotive();
                if (Input.GetKeyDown(DebugTools.KeyTeleportScene)) DebugTools.TeleportToScene();
                if (Input.GetKeyDown(DebugTools.KeyTeleportWork)) DebugTools.TeleportToWork();
                if (Input.GetKeyDown(DebugTools.KeyTeleportKnower)) DebugTools.TeleportToNearestKnower();
                if (Input.GetKeyDown(DebugTools.KeyAlwaysAnswer))
                {
                    DebugTools.AlwaysAnswer = !DebugTools.AlwaysAnswer;
                    MotivesPlugin.Log.LogInfo($"[SODMotives] ALWAYS-ANSWER (no bribe) {(DebugTools.AlwaysAnswer ? "ON" : "OFF")} — while ON, the top-right HUD shows who you're talking to.");
                }
                if (Input.GetKeyDown(DebugTools.KeyGhost))
                {
                    DebugTools.Ghost = !DebugTools.Ghost;
                    if (!DebugTools.Ghost) DebugTools.ClearGhost();
                    MotivesPlugin.Log.LogInfo($"[SODMotives] GHOST MODE {(DebugTools.Ghost ? "ON" : "OFF")}");
                }
                if (DebugTools.Ghost) DebugTools.ApplyGhost();

                // Recover a mod motive-murder stuck in 'executing' (killer whiffing forever). Scoped to
                // our overridden cases + co-located killer only; no-op otherwise. See MurderWatchdog.
                MurderWatchdog.Tick();
            }
            catch { }
        }

        void OnGUI()
        {
            // Debug-key indicators (F6/F7/F8) + injected-clue locations (F4/F5): TOP-RIGHT,
            // right-aligned, stacked top-down with no gaps when an indicator is off.
            try
            {
                if (_hudStyle == null)
                    _hudStyle = new GUIStyle { fontSize = 14, wordWrap = false, alignment = TextAnchor.UpperRight };

                const float rm = 8f, w = 760f;
                float x = Screen.width - w - rm;
                float y = 8f;

                // Force-motive (F6) — shown ONLY when a force is active (hidden when OFF).
                bool fm = DebugTools.ForceEventType.HasValue || DebugTools.ForceMotiveType != MotiveType.None;
                if (fm)
                {
                    _hudStyle.normal.textColor = Color.yellow;
                    GUI.Label(new Rect(x, y, w, 20), $"FORCE: {DebugTools.ForceLabel()} (F6)", _hudStyle);
                    y += 20f;
                }

                // Always-answer (F8) + who-you're-talking-to readout.
                if (DebugTools.AlwaysAnswer)
                {
                    _hudStyle.normal.textColor = Color.cyan;
                    GUI.Label(new Rect(x, y, w, 20), "ALWAYS-ANSWER ON (F8) - NPCs never refuse 'do you know this person?'", _hudStyle);
                    y += 20f;
                    if (!string.IsNullOrEmpty(DebugTools.TalkingToLabel))
                    {
                        GUI.Label(new Rect(x, y, w, 20), "TALKING TO: " + DebugTools.TalkingToLabel, _hudStyle);
                        y += 20f;
                    }
                }

                // Ghost (F7).
                if (DebugTools.Ghost)
                {
                    _hudStyle.normal.textColor = Color.green;
                    GUI.Label(new Rect(x, y, w, 20), "GHOST MODE ON (F7) - NPCs ignore you", _hudStyle);
                    y += 20f;
                }

            }
            catch { }

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

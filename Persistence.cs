using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;
using HarmonyLib;

namespace SODMotives
{
    // Save/reload persistence for mod-authored custom notes (W-persist, pass 1: clues only).
    //
    // WHY: the note OBJECT survives a save/reload (it's serialized as ordinary evidence, keeping its int
    // id, its `dds` tree-id override, and its custom title). But everything the tree-id POINTS AT is
    // runtime-only, rebuilt from StreamingAssets at boot: the Toolbox DDS tree/block/message, the Strings
    // body text, the clickable link ids, and our custom document->citizen board facts. So after reload the
    // note renders blank with dead name-links and no connections (confirmed in-game via the F4 test note;
    // only the native writer/author fact survives). FIX: write a tiny sidecar next to the .sodb recording,
    // per note, the ids needed to rebuild; on load, re-register the tree under the SAME id + redraw links.
    //
    // Pass 2 (separate) will add EventStore/MurderSelector rehydration for interrogation using this same
    // sidecar. Deliberately not here yet — pass 1 is the F4-testable increment.
    internal static class Persistence
    {
        internal static bool Enable = true;

        internal struct NoteRecord
        {
            public int interactableId;
            public string treeId;
            public string kind;               // "layoffs" | "test" | (future)
            public int authorHumanId;
            public List<int> citizenHumanIds;
            public string title;
        }

        // Every custom note placed this session, or loaded from a sidecar. Serialized on save.
        internal static readonly List<NoteRecord> Records = new List<NoteRecord>();

        // Save-path tracking. CaptureSaveStateAsync hands us the path on save; the load menu hands us the
        // clicked slot's path. On load we prefer the explicit menu choice, else the last save this session
        // (covers save -> quit-to-menu -> reload without needing the menu hook to have fired).
        private static string _currentSavePath;    // last CaptureSaveStateAsync path (this session)
        private static string _selectedSavePath;   // last save slot clicked in the load menu

        // Replay state (armed by LoadSaveState, driven by the PersistenceRunner poll).
        private static bool _replayPending;
        private static string _replayPath;
        private static int _replayAttempts;
        private static readonly HashSet<int> _replayedIds = new HashSet<int>();

        private const string Header = "SODMOTIVES_SIDECAR\tv1";

        // Cleared on a genuinely NEW game (SeedForNewGame runs on OnStartGame = new-game only, NOT load),
        // so a fresh city can't carry a previous city's note records into its first save. We intentionally
        // do NOT touch _replayPending here: a new game never has a pending replay, and leaving it alone is
        // belt-and-suspenders in case OnStartGame ever fires on load.
        internal static void ResetForNewGame()
        {
            Records.Clear();
            _replayedIds.Clear();
        }

        // Called by ClueInjector the moment it finishes placing a custom note.
        internal static void RecordNote(int interactableId, string treeId, string kind, int authorHumanId, List<int> citizenHumanIds, string title)
        {
            if (!Enable) return;
            if (interactableId < 0 || string.IsNullOrEmpty(treeId)) return;
            var rec = new NoteRecord
            {
                interactableId = interactableId,
                treeId = treeId,
                kind = kind ?? "note",
                authorHumanId = authorHumanId,
                citizenHumanIds = citizenHumanIds ?? new List<int>(),
                title = title ?? "",
            };
            // Dedup by interactable id (re-placing the same id replaces its record).
            for (int i = 0; i < Records.Count; i++)
                if (Records[i].interactableId == interactableId) { Records[i] = rec; return; }
            Records.Add(rec);
        }

        // ---- sidecar path: <dir>\<saveNameWithoutExt>.sodmotives.dat, next to the .sodb ----
        private static string SidecarFor(string sodbPath)
        {
            try
            {
                if (string.IsNullOrEmpty(sodbPath)) return null;
                string dir = Path.GetDirectoryName(sodbPath);
                string name = Path.GetFileNameWithoutExtension(sodbPath);
                if (string.IsNullOrEmpty(name)) return null;
                return string.IsNullOrEmpty(dir) ? name + ".sodmotives.dat" : Path.Combine(dir, name + ".sodmotives.dat");
            }
            catch { return null; }
        }

        // ---- SAVE: write the sidecar (line-based, tab-delimited — no JSON dependency / IL2CPP quirks) ----
        internal static void OnSave(string sodbPath)
        {
            if (!Enable) return;
            _currentSavePath = sodbPath;
            string side = SidecarFor(sodbPath);
            if (side == null) { MotivesPlugin.Log.LogWarning("[SODMotives] persist: no sidecar path from save path; skipping write."); return; }
            try
            {
                var sb = new StringBuilder();
                sb.Append(Header).Append('\n');
                for (int i = 0; i < Records.Count; i++)
                {
                    var r = Records[i];
                    var ids = new StringBuilder();
                    if (r.citizenHumanIds != null)
                        for (int j = 0; j < r.citizenHumanIds.Count; j++) { if (j > 0) ids.Append(','); ids.Append(r.citizenHumanIds[j]); }
                    string title = (r.title ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                    sb.Append("N\t").Append(r.interactableId).Append('\t').Append(r.treeId ?? "").Append('\t')
                      .Append(r.kind ?? "note").Append('\t').Append(r.authorHumanId).Append('\t')
                      .Append(ids.ToString()).Append('\t').Append(title).Append('\n');
                }
                File.WriteAllText(side, sb.ToString());
                MotivesPlugin.Log.LogInfo($"[SODMotives] persist: wrote sidecar ({Records.Count} note record(s)) -> {side}");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: sidecar write failed: {e.Message}"); }
        }

        // ---- parse ----
        private static List<NoteRecord> ReadSidecar(string sodbPath)
        {
            string side = SidecarFor(sodbPath);
            if (side == null || !File.Exists(side)) return null;
            var outList = new List<NoteRecord>();
            try
            {
                var lines = File.ReadAllLines(side);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line) || !line.StartsWith("N\t")) continue;
                    var p = line.Split('\t');
                    if (p.Length < 7) continue;
                    int id; if (!int.TryParse(p[1], out id)) continue;
                    string treeId = p[2];
                    string kind = p[3];
                    int auth; int.TryParse(p[4], out auth);
                    var ids = new List<int>();
                    if (!string.IsNullOrEmpty(p[5]))
                        foreach (var s in p[5].Split(',')) { int cid; if (int.TryParse(s, out cid)) ids.Add(cid); }
                    // Title was sanitized of tabs on write, so it's a single field; join any extras defensively.
                    string title = p.Length == 7 ? p[6] : string.Join("\t", p, 6, p.Length - 6);
                    outList.Add(new NoteRecord { interactableId = id, treeId = treeId, kind = kind, authorHumanId = auth, citizenHumanIds = ids, title = title });
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: sidecar read failed: {e.Message}"); return null; }
            return outList;
        }

        // ---- LOAD: arm the replay poll (LoadSaveState fires only on a save-load, not new game) ----
        internal static void MarkReplayPending()
        {
            if (!Enable) return;
            _replayPath = !string.IsNullOrEmpty(_selectedSavePath) ? _selectedSavePath : _currentSavePath;
            _replayPending = true;
            _replayAttempts = 0;
            _replayedIds.Clear();
            MotivesPlugin.Log.LogInfo($"[SODMotives] persist: load detected; replay armed (path={_replayPath ?? "<unknown>"}).");
        }

        internal static void OnSaveSlotClicked(string fullPath)
        {
            if (!string.IsNullOrEmpty(fullPath)) _selectedSavePath = fullPath;
        }

        // Polled every frame by PersistenceRunner. Waits until the world is live (citizens + interactables
        // restored), then rebuilds each recorded note ONCE. Retries while some note's interactable hasn't
        // been restored yet; gives up after ~15s so a genuinely-missing note can't spin forever.
        internal static void Tick()
        {
            if (!_replayPending) return;
            _replayAttempts++;
            if (_replayAttempts > 900) { _replayPending = false; MotivesPlugin.Log.LogWarning("[SODMotives] persist: replay timed out (some notes not restored)."); return; }

            var city = CityData.Instance;
            if (city == null) return;
            var citizens = city.citizenDirectory;
            if (citizens == null || citizens.Count == 0) return;   // world not ready yet — retry next frame
            var savable = city.savableInteractableDictionary;
            if (savable == null) return;

            var records = ReadSidecar(_replayPath);
            if (records == null) { _replayPending = false; MotivesPlugin.Log.LogInfo("[SODMotives] persist: no sidecar for this save; nothing to restore."); return; }
            if (records.Count == 0) { _replayPending = false; MotivesPlugin.Log.LogInfo("[SODMotives] persist: sidecar empty; nothing to restore."); return; }

            // Repopulate the live record list so a subsequent save re-persists these notes.
            if (Records.Count == 0) for (int i = 0; i < records.Count; i++) Records.Add(records[i]);

            // id -> Human map (citizenDirectory includes the dead, so laid-off-then-deceased still resolve).
            var map = new Dictionary<int, Human>();
            for (int i = 0; i < citizens.Count; i++)
            {
                var c = citizens[i]; if (c == null) continue;
                Human h = null; try { h = c.TryCast<Human>(); } catch { }
                if (h == null) continue;
                try { map[h.humanID] = h; } catch { }
            }

            int done = 0, missing = 0;
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (_replayedIds.Contains(r.interactableId)) { done++; continue; }

                Interactable inter = null;
                try { savable.TryGetValue(r.interactableId, out inter); } catch { }
                if (inter == null) inter = FindInteractable(city, r.interactableId);
                if (inter == null) { missing++; continue; }   // not restored yet (retry) or destroyed (times out)

                Human author = null; map.TryGetValue(r.authorHumanId, out author);
                var people = new List<Human>();
                if (r.citizenHumanIds != null)
                    for (int j = 0; j < r.citizenHumanIds.Count; j++)
                    { Human h; if (map.TryGetValue(r.citizenHumanIds[j], out h) && h != null) people.Add(h); }

                try { ClueInjector.RebuildCustomNote(inter, people, author, r.treeId); }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: rebuild id={r.interactableId} failed: {e.Message}"); }
                _replayedIds.Add(r.interactableId);   // once, even on failure — no duplicate connections
                done++;
            }

            if (missing == 0)
            {
                _replayPending = false;
                MotivesPlugin.Log.LogInfo($"[SODMotives] persist: replay complete — {done} note(s) restored.");
            }
            // else keep pending: interactables still streaming in — retry next frame (until timeout).
        }

        private static Interactable FindInteractable(CityData city, int id)
        {
            try
            {
                var dir = city.interactableDirectory;
                if (dir != null) for (int i = 0; i < dir.Count; i++) { var it = dir[i]; if (it != null && it.id == id) return it; }
            }
            catch { }
            return null;
        }

        // ---- MonoBehaviour host: drives the replay poll (DontDestroyOnLoad so it's alive across loads) ----
        internal static void Register()
        {
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<PersistenceRunner>();
                var go = new GameObject("SODMotives_Persistence");
                GameObject.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<PersistenceRunner>();
                MotivesPlugin.Log.LogInfo("[SODMotives] persist: runner registered.");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: runner register failed: {e.Message}"); }
        }
    }

    // SAVE hook: write the sidecar with the path the game hands us.
    [HarmonyPatch(typeof(SaveStateController), nameof(SaveStateController.CaptureSaveStateAsync))]
    internal static class Patch_Persist_Save
    {
        static void Postfix(string path)
        {
            try { Persistence.OnSave(path); }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: save hook: {e.Message}"); }
        }
    }

    // LOAD hook: fires only when loading a save (new-game generation doesn't call it). Arm the replay poll.
    [HarmonyPatch(typeof(SaveStateController), nameof(SaveStateController.LoadSaveState))]
    internal static class Patch_Persist_Load
    {
        static void Postfix()
        {
            try { Persistence.MarkReplayPending(); }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: load hook: {e.Message}"); }
        }
    }

    // Load menu: remember which save slot was clicked, so on load we know which sidecar to read.
    [HarmonyPatch(typeof(SaveGameEntryController), nameof(SaveGameEntryController.OnLeftClick))]
    internal static class Patch_Persist_SlotClick
    {
        static void Postfix(SaveGameEntryController __instance)
        {
            try
            {
                var fi = __instance.info;
                if (fi == null) return;
                string full = null;
                try { var fn = fi.FullName; full = fn != null ? fn.ToString() : null; } catch { }
                Persistence.OnSaveSlotClicked(full);
            }
            catch { }
        }
    }

    public class PersistenceRunner : MonoBehaviour
    {
        public PersistenceRunner(IntPtr ptr) : base(ptr) { }
        void Update() { try { Persistence.Tick(); } catch { } }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;
using HarmonyLib;

namespace SODMotives
{
    // Save/reload persistence for the mod's runtime-only state, on a sidecar next to the .sodb.
    //
    // WHY A SIDECAR: the game serializes its OWN objects (the note interactable + its int id/dds
    // override/title, the murder, the citizens/paramour graph) but knows nothing about the mod's
    // runtime tables — the custom DDS body text + links (pass 1), and the whole social-event web +
    // per-case selection maps that drive interrogation gossip and the F9/F12 aids (pass 2). All of
    // that is rebuilt-from-scratch at boot and never saved, so after a reload a mod note renders
    // blank and NPC testimony / F9 forget the case. FIX: mirror what we need into a tiny sidecar on
    // save, and replay it on load.
    //
    //   PASS 1 (notes)  — record each custom note's ids; on load, re-register its DDS tree under the
    //                     SAME id + redraw citizen links.  [committed dd0e046, verified in-game]
    //   PASS 2 (gossip) — dump the ENTIRE EventStore (all affairs + materialized workplace events,
    //                     ids preserved) + all six MurderSelector maps; import them verbatim in the
    //                     same replay tick so testimony + F9/F12 survive a reload.
    //
    // THE LOAD GATE (empirically grounded, 2026-09-08): MurderController.OnStartGame FIRES ON LOAD
    // as well as on a new game, and would re-seed affairs + reset the maps right over our import.
    // The game's own `generateNew` flag reads False even on a new game by the time OnStartGame runs,
    // so it can't discriminate. Instead we set `_loadInProgress` in the LoadSaveState hook (which
    // fires only on load, and BEFORE OnStartGame). OnStartGame consumes that flag to SKIP its
    // re-seed on a load; the flag is never touched by the replay tick, so the import survives no
    // matter which of the two runs first. (See docs/persistence-handover.md.)
    internal static class Persistence
    {
        internal static bool Enable = true;

        // ---------- pass 1: note records ----------
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

        // ---------- save-path tracking ----------
        // CaptureSaveStateAsync hands us the path on save; the load menu hands us the clicked slot's
        // path. On load we prefer the explicit menu choice, else the last save this session (covers
        // save -> load-from-pause without the menu hook having fired).
        private static string _currentSavePath;    // last CaptureSaveStateAsync path (this session)
        private static string _selectedSavePath;   // last save slot clicked in the load menu

        // ---------- replay state (armed by LoadSaveState, driven by PersistenceRunner) ----------
        private static bool _replayPending;
        private static string _replayPath;
        private static int _replayAttempts;
        private static readonly HashSet<int> _replayedIds = new HashSet<int>();

        // Pass-2 gate: true from the LoadSaveState hook until OnStartGame consumes it. Lets the
        // OnStartGame seed skip itself on a load. NOT cleared by the replay tick, so it's still set
        // whichever order (tick-import vs OnStartGame) happens to run first.
        private static bool _loadInProgress;

        // Pass-2 import bookkeeping (reset per load).
        private static bool _eventsImported;                 // events + maps imported (or fallback-seeded) once
        private static bool _sideRead;                       // sidecar parsed from disk once
        private static Sidecar _loadedSide;                  // parsed sidecar (null = no file)

        private const string Header = "SODMOTIVES_SIDECAR\tv2";

        // Cleared on a genuinely NEW game (SeedForNewGame, gated to new-game only), so a fresh city
        // can't carry a previous city's note records into its first save. We intentionally do NOT
        // touch the load/replay flags here: a new game never has a pending replay.
        internal static void ResetForNewGame()
        {
            Records.Clear();
            _replayedIds.Clear();
        }

        // ---------- pass-2 gate API ----------
        // Consumed by the OnStartGame postfix: returns true (and clears the flag) when this
        // OnStartGame is part of a LOAD, meaning the caller should SKIP its new-game re-seed and let
        // the replay tick's import/fallback populate instead. Returns false on a genuine new game.
        internal static bool ConsumeLoadSeedSkip()
        {
            bool was = _loadInProgress;
            _loadInProgress = false;   // consume once (OnStartGame fires exactly once per game start)
            if (!Enable) return false;
            if (was)
                MotivesPlugin.Log.LogInfo("[SODMotives] persist2: OnStartGame during a load — skipping new-game re-seed; the replay tick will restore events+maps (or fallback-seed).");
            return was;
        }

        // Consulted by the Interrogation lazy re-seed fallback: while a load's import is still
        // pending, an empty EventStore is expected (OnStartGame skipped its seed) and must be left
        // for the tick to fill — never re-seeded, which would clobber the incoming maps.
        internal static bool IsImportPending() => _replayPending && !_eventsImported;

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

        // ================= SAVE =================
        // Line-based, tab-delimited (no JSON dependency / IL2CPP reflection quirks). Line kinds:
        //   N  note:  id treeId kind authorId citizenIdsCsv title
        //   E  event: id type aId bId placeName time companyId groupCsv knownByCsv
        //   OV overridden victim ids (csv) | UW used workplace company ids (csv)
        //   MB motive-by-victim: victimId type targetId score detail
        //   PB pool edge:        victimId suspectId score type detail eventId   (many per victim)
        //   AB affair-by-victim: victimId eventId | EB event-by-victim: victimId eventId
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

                // --- notes (pass 1) ---
                for (int i = 0; i < Records.Count; i++)
                {
                    var r = Records[i];
                    sb.Append("N\t").Append(r.interactableId).Append('\t').Append(r.treeId ?? "").Append('\t')
                      .Append(r.kind ?? "note").Append('\t').Append(r.authorHumanId).Append('\t')
                      .Append(IntsCsv(r.citizenHumanIds)).Append('\t').Append(San(r.title)).Append('\n');
                }

                // --- events (pass 2) ---
                int evCount = 0;
                var evs = EventStore.All;
                if (evs != null)
                    for (int i = 0; i < evs.Count; i++)
                    {
                        var e = evs[i];
                        if (e == null) continue;
                        sb.Append("E\t").Append(e.id).Append('\t').Append(e.type.ToString()).Append('\t')
                          .Append(SafeId(e.a)).Append('\t').Append(SafeId(e.b)).Append('\t')
                          .Append(San(e.placeName)).Append('\t').Append(e.time.ToString(CultureInfo.InvariantCulture)).Append('\t')
                          .Append(e.companyId).Append('\t').Append(HumansCsv(e.group)).Append('\t').Append(IntSetCsv(e.knownBy)).Append('\n');
                        evCount++;
                    }

                // --- maps (pass 2) ---
                sb.Append("OV\t").Append(IntSetCsv(MurderSelector.OverriddenVictimIds)).Append('\n');
                sb.Append("UW\t").Append(IntSetCsv(MurderSelector.UsedWorkplaceCompanies)).Append('\n');
                sb.Append("UB\t").Append(IntSetCsv(MurderSelector.UsedBuildings)).Append('\n');

                foreach (var kv in MurderSelector.MotiveByVictim)
                {
                    var m = kv.Value;
                    sb.Append("MB\t").Append(kv.Key).Append('\t').Append(m.type.ToString()).Append('\t')
                      .Append(SafeId(m.target)).Append('\t').Append(m.score.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(San(m.detail)).Append('\n');
                }

                foreach (var kv in MurderSelector.PoolByVictim)
                {
                    var list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var ed = list[i];
                        sb.Append("PB\t").Append(kv.Key).Append('\t').Append(SafeId(ed.suspect)).Append('\t')
                          .Append(ed.score.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(ed.type.ToString()).Append('\t')
                          .Append(San(ed.detail)).Append('\t').Append(ed.evt != null ? ed.evt.id : -1).Append('\n');
                    }
                }

                foreach (var kv in MurderSelector.AffairByVictim)
                    sb.Append("AB\t").Append(kv.Key).Append('\t').Append(kv.Value != null ? kv.Value.id : -1).Append('\n');
                foreach (var kv in MurderSelector.EventByVictim)
                    sb.Append("EB\t").Append(kv.Key).Append('\t').Append(kv.Value != null ? kv.Value.id : -1).Append('\n');

                // Which victims' clue-set was already injected (so a reload doesn't inject a duplicate).
                sb.Append("IJ\t").Append(IntsCsv(ClueInjector.GetInjectedVictimIds())).Append('\n');

                File.WriteAllText(side, sb.ToString());
                MotivesPlugin.Log.LogInfo($"[SODMotives] persist: wrote sidecar ({Records.Count} note(s), {evCount} event(s), {MurderSelector.OverriddenVictimIds.Count} overridden victim(s)) -> {side}");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: sidecar write failed: {e.Message}"); }
        }

        // ---- serialization helpers ----
        private static int SafeId(Human h) { if (h == null) return -1; try { return h.humanID; } catch { return -1; } }
        private static string San(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        private static string IntsCsv(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(ids[i]); }
            return sb.ToString();
        }
        private static string IntSetCsv(IEnumerable<int> ids)
        {
            if (ids == null) return "";
            var sb = new StringBuilder();
            bool first = true;
            foreach (int id in ids) { if (!first) sb.Append(','); sb.Append(id); first = false; }
            return sb.ToString();
        }
        private static string HumansCsv(List<Human> people)
        {
            if (people == null || people.Count == 0) return "";
            var sb = new StringBuilder();
            bool first = true;
            for (int i = 0; i < people.Count; i++)
            {
                int id = SafeId(people[i]);
                if (id < 0) continue;
                if (!first) sb.Append(','); sb.Append(id); first = false;
            }
            return sb.ToString();
        }

        // ================= PARSE =================
        private sealed class Sidecar
        {
            public List<NoteRecord> notes = new List<NoteRecord>();
            public List<EventRec> events = new List<EventRec>();
            public List<int> overridden = new List<int>();
            public List<int> usedCompanies = new List<int>();
            public List<int> usedBuildings = new List<int>();
            public List<MotiveRec> motives = new List<MotiveRec>();
            public List<EdgeRec> edges = new List<EdgeRec>();
            public List<int[]> affairBy = new List<int[]>();   // [victimId, eventId]
            public List<int[]> eventBy = new List<int[]>();     // [victimId, eventId]
            public List<int> injected = new List<int>();        // victim ids whose clue-set was injected
        }
        private struct EventRec
        {
            public int id; public string type; public int aId; public int bId;
            public string placeName; public float time; public int companyId;
            public List<int> groupIds; public List<int> knownBy;
        }
        private struct MotiveRec { public int victimId; public string type; public int targetId; public float score; public string detail; }
        private struct EdgeRec { public int victimId; public int suspectId; public float score; public string type; public string detail; public int eventId; }

        private static Sidecar ReadSidecarFull(string sodbPath)
        {
            string side = SidecarFor(sodbPath);
            if (side == null || !File.Exists(side)) return null;
            var s = new Sidecar();
            try
            {
                var lines = File.ReadAllLines(side);
                for (int li = 0; li < lines.Length; li++)
                {
                    string line = lines[li];
                    if (string.IsNullOrEmpty(line)) continue;
                    var p = line.Split('\t');
                    switch (p[0])
                    {
                        case "N":
                            if (p.Length < 7) break;
                            {
                                if (!int.TryParse(p[1], out int id)) break;
                                int.TryParse(p[4], out int auth);
                                s.notes.Add(new NoteRecord
                                {
                                    interactableId = id, treeId = p[2], kind = p[3], authorHumanId = auth,
                                    citizenHumanIds = ParseIntCsv(p[5]),
                                    title = p.Length == 7 ? p[6] : string.Join("\t", p, 6, p.Length - 6),
                                });
                            }
                            break;
                        case "E":
                            if (p.Length < 10) break;
                            {
                                if (!int.TryParse(p[1], out int id)) break;
                                s.events.Add(new EventRec
                                {
                                    id = id, type = p[2],
                                    aId = ParseInt(p[3], -1), bId = ParseInt(p[4], -1),
                                    placeName = p[5], time = ParseFloat(p[6]),
                                    companyId = ParseInt(p[7], -1),
                                    groupIds = ParseIntCsv(p[8]), knownBy = ParseIntCsv(p[9]),
                                });
                            }
                            break;
                        case "OV": if (p.Length >= 2) s.overridden = ParseIntCsv(p[1]); break;
                        case "UW": if (p.Length >= 2) s.usedCompanies = ParseIntCsv(p[1]); break;
                        case "UB": if (p.Length >= 2) s.usedBuildings = ParseIntCsv(p[1]); break;
                        case "MB":
                            if (p.Length < 6) break;
                            if (!int.TryParse(p[1], out int mv)) break;
                            s.motives.Add(new MotiveRec { victimId = mv, type = p[2], targetId = ParseInt(p[3], -1), score = ParseFloat(p[4]), detail = p[5] });
                            break;
                        case "PB":
                            if (p.Length < 7) break;
                            if (!int.TryParse(p[1], out int pv)) break;
                            s.edges.Add(new EdgeRec { victimId = pv, suspectId = ParseInt(p[2], -1), score = ParseFloat(p[3]), type = p[4], detail = p[5], eventId = ParseInt(p[6], -1) });
                            break;
                        case "AB":
                            if (p.Length >= 3 && int.TryParse(p[1], out int av)) s.affairBy.Add(new[] { av, ParseInt(p[2], -1) });
                            break;
                        case "EB":
                            if (p.Length >= 3 && int.TryParse(p[1], out int ev)) s.eventBy.Add(new[] { ev, ParseInt(p[2], -1) });
                            break;
                        case "IJ": if (p.Length >= 2) s.injected = ParseIntCsv(p[1]); break;
                    }
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: sidecar read failed: {e.Message}"); return null; }
            return s;
        }

        private static int ParseInt(string s, int dflt) => int.TryParse(s, out int v) ? v : dflt;
        private static float ParseFloat(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        private static List<int> ParseIntCsv(string s)
        {
            var outList = new List<int>();
            if (string.IsNullOrEmpty(s)) return outList;
            foreach (var part in s.Split(',')) if (int.TryParse(part, out int v)) outList.Add(v);
            return outList;
        }

        // ================= LOAD =================
        internal static void MarkReplayPending()
        {
            if (!Enable) return;
            _replayPath = !string.IsNullOrEmpty(_selectedSavePath) ? _selectedSavePath : _currentSavePath;
            _selectedSavePath = null;   // consume: a stale last-clicked slot must not shadow a later save's path
            _replayPending = true;
            _replayAttempts = 0;
            _replayedIds.Clear();
            _loadInProgress = true;     // gate: OnStartGame will skip its re-seed for this load
            _eventsImported = false;
            _sideRead = false;
            _loadedSide = null;
            MotivesPlugin.Log.LogInfo($"[SODMotives] persist: load detected; replay armed (path={_replayPath ?? "<unknown>"}).");
        }

        internal static void OnSaveSlotClicked(string fullPath)
        {
            if (!string.IsNullOrEmpty(fullPath)) _selectedSavePath = fullPath;
        }

        // Polled every frame by PersistenceRunner. Once citizens are live it (1) imports events+maps
        // ONCE (or fallback-seeds affairs when the save has no pass-2 data), then (2) rebuilds each
        // recorded note, retrying while interactables stream in. Gives up after ~15 s.
        internal static void Tick()
        {
            if (!_replayPending) return;
            _replayAttempts++;
            if (_replayAttempts > 900) { _replayPending = false; MotivesPlugin.Log.LogWarning("[SODMotives] persist: replay timed out (some notes not restored)."); return; }

            var city = CityData.Instance;
            if (city == null) return;
            var citizens = city.citizenDirectory;
            if (citizens == null || citizens.Count == 0) return;   // world not ready yet — retry next frame

            // id -> Human map (fresh each tick; citizenDirectory includes the dead, so laid-off-then-
            // deceased still resolve). Shared by the event/map import and the note replay below.
            var map = new Dictionary<int, Human>();
            for (int i = 0; i < citizens.Count; i++)
            {
                var c = citizens[i]; if (c == null) continue;
                Human h = null; try { h = c.TryCast<Human>(); } catch { }
                if (h == null) continue;
                try { map[h.humanID] = h; } catch { }
            }

            // Parse the sidecar once.
            if (!_sideRead) { _loadedSide = ReadSidecarFull(_replayPath); _sideRead = true; }

            // (1) EVENTS + MAPS — once, as soon as citizens exist (needs no interactables).
            if (!_eventsImported)
            {
                if (_loadedSide != null && _loadedSide.events.Count > 0)
                {
                    ImportEventsAndMaps(_loadedSide, map);
                }
                else
                {
                    // No pass-2 data (pre-pass-2 save / vanilla save): re-seed affairs from the live
                    // graph so a loaded game still has gossip. (OnStartGame skipped its own seed.)
                    AffairSim.SeedForNewGame();
                    MotivesPlugin.Log.LogInfo("[SODMotives] persist2: no pass-2 events in sidecar — re-seeded affairs from the live graph (older save).");
                }
                // Records must reflect THIS loaded save's notes, not the session's accumulation (a
                // process-lifetime static): rebuild it from the sidecar so the next save re-persists
                // exactly the loaded save's note set, never a later timeline's (fixes cross-save
                // contamination when switching/reloading saves in one process).
                Records.Clear();
                if (_loadedSide != null) for (int i = 0; i < _loadedSide.notes.Count; i++) Records.Add(_loadedSide.notes[i]);
                _eventsImported = true;
            }

            // (2) NOTES — nothing to restore if the save has none.
            if (_loadedSide == null || _loadedSide.notes.Count == 0)
            {
                _replayPending = false;
                MotivesPlugin.Log.LogInfo("[SODMotives] persist: replay complete (events+maps; no note records).");
                return;
            }

            var savable = city.savableInteractableDictionary;
            if (savable == null) return;   // interactables not ready yet — retry

            int done = 0, missing = 0;
            for (int i = 0; i < _loadedSide.notes.Count; i++)
            {
                var r = _loadedSide.notes[i];
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

                try { ClueInjector.RebuildCustomNote(inter, people, author, r.treeId, r.kind); }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: rebuild id={r.interactableId} failed: {e.Message}"); }
                _replayedIds.Add(r.interactableId);   // once, even on failure — no duplicate connections
                done++;
            }

            if (missing == 0)
            {
                _replayPending = false;
                MotivesPlugin.Log.LogInfo($"[SODMotives] persist: replay complete — {done} note(s) restored (+ events+maps).");
            }
            // else keep pending: interactables still streaming in — retry next frame (until timeout).
        }

        // Rebuild the EventStore + all MurderSelector maps from a parsed sidecar, resolving Humans
        // via the id->Human map and events via a local id->SocialEvent map. Ids are preserved so the
        // map->event references line up exactly with what was saved.
        private static void ImportEventsAndMaps(Sidecar side, Dictionary<int, Human> map)
        {
            var idToEvent = new Dictionary<int, SocialEvent>();
            var rebuilt = new List<SocialEvent>(side.events.Count);
            for (int i = 0; i < side.events.Count; i++)
            {
                var er = side.events[i];
                var e = new SocialEvent
                {
                    id = er.id,
                    type = ParseEnum(er.type, SocialEventType.Affair),
                    a = ResolveHuman(map, er.aId),
                    b = ResolveHuman(map, er.bId),
                    placeName = er.placeName,
                    time = er.time,
                    companyId = er.companyId,
                };
                if (er.groupIds != null)
                    for (int j = 0; j < er.groupIds.Count; j++) { var h = ResolveHuman(map, er.groupIds[j]); if (h != null) e.group.Add(h); }
                if (er.knownBy != null)
                    for (int j = 0; j < er.knownBy.Count; j++) e.knownBy.Add(er.knownBy[j]);
                rebuilt.Add(e);
                idToEvent[e.id] = e;
            }
            EventStore.RehydrateFrom(rebuilt);

            var ov = new HashSet<int>(side.overridden);
            var uw = new HashSet<int>(side.usedCompanies);
            var ub = new HashSet<int>(side.usedBuildings);

            var mb = new Dictionary<int, MotiveResult>();
            for (int i = 0; i < side.motives.Count; i++)
            {
                var mr = side.motives[i];
                mb[mr.victimId] = new MotiveResult
                {
                    type = ParseEnum(mr.type, MotiveType.None),
                    target = ResolveHuman(map, mr.targetId),
                    score = mr.score,
                    detail = mr.detail,
                };
            }

            var pb = new Dictionary<int, List<SuspectEdge>>();
            for (int i = 0; i < side.edges.Count; i++)
            {
                var er = side.edges[i];
                SocialEvent ev; idToEvent.TryGetValue(er.eventId, out ev);
                if (!pb.TryGetValue(er.victimId, out var list)) { list = new List<SuspectEdge>(); pb[er.victimId] = list; }
                list.Add(new SuspectEdge
                {
                    suspect = ResolveHuman(map, er.suspectId),
                    victim = ResolveHuman(map, er.victimId),
                    score = er.score,
                    type = ParseEnum(er.type, MotiveType.None),
                    detail = er.detail,
                    evt = ev,
                });
            }

            var ab = new Dictionary<int, SocialEvent>();
            for (int i = 0; i < side.affairBy.Count; i++)
            { SocialEvent ev; if (idToEvent.TryGetValue(side.affairBy[i][1], out ev) && ev != null) ab[side.affairBy[i][0]] = ev; }
            var eb = new Dictionary<int, SocialEvent>();
            for (int i = 0; i < side.eventBy.Count; i++)
            { SocialEvent ev; if (idToEvent.TryGetValue(side.eventBy[i][1], out ev) && ev != null) eb[side.eventBy[i][0]] = ev; }

            MurderSelector.RehydrateMaps(ov, mb, pb, ab, eb, uw, ub);
            // Restore which victims were already injected so a SetMurderState(post) that re-fires on
            // load can't inject a duplicate clue-set (a case saved pre-kill stays un-injected -> it
            // will inject normally when it reaches 'post').
            ClueInjector.RestoreInjectedOnLoad(side.injected);
            MotivesPlugin.Log.LogInfo($"[SODMotives] persist2: imported {rebuilt.Count} event(s) + maps ({ov.Count} overridden victim(s), {pb.Count} pool(s), {side.injected.Count} injected).");
        }

        private static Human ResolveHuman(Dictionary<int, Human> map, int id)
        {
            if (id < 0 || map == null) return null;
            return map.TryGetValue(id, out var h) ? h : null;
        }

        private static T ParseEnum<T>(string s, T dflt) where T : struct
            => Enum.TryParse<T>(s, out var v) ? v : dflt;

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

    // LOAD hook: fires only when loading a save (new-game generation doesn't call it), and BEFORE
    // MurderController.OnStartGame. Arms the replay poll and raises the pass-2 load gate.
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

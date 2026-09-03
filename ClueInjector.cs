using System;
using System.Collections.Generic;

namespace SODMotives
{
    // Injects readable love-letter clues that represent the REAL affairs of the victim's
    // apartment residents. Each letter is placed IN the victim's home via the game's own
    // placement primitive (NewGameLocation.PlaceObject), written by one participant and
    // addressed to the other, with a motive-appropriate DDS document tree overridden on.
    internal static class ClueInjector
    {
        internal static bool Enable = true;
        internal static int MaxClues = 3;   // safety cap (a couple = at most ~2 affairs)
        internal static bool ObviousNames = true;  // TESTING: rename injected notes so they're easy to spot
        internal static float FingerprintChance = 0.7f; // chance a note carries the author's prints

        private static readonly Random _rng = new Random();

        private static InteractablePreset _notePreset;
        private static bool _scanned;

        // For F9 overlay.
        internal static readonly Dictionary<int, List<string>> CluesByVictim = new Dictionary<int, List<string>>();
        private static readonly HashSet<int> _injected = new HashSet<int>();

        // Affair-appropriate DDS document trees (verified treeType==document, participants loose,
        // message saidBy:0->saidTo:1 so writer=from, reciever=to). vmail trees render blank.
        private static readonly (string id, string name)[] InfidelityTrees = {
            ("9b6d2119-f1a0-4b47-880d-1ee6fb417716", "Cheaters_Letter"),
            ("e6cfbb79-cc0b-4848-b699-280cbe261c3e", "Cheaters_Love_Poem"),
            ("7b3abe1a-30dd-41b1-b0ec-8fe6e739d2a2", "Flower_Note_Affair"),
        };

        // ---- preset selection: a clean, home-placeable, single-page readable note ----
        // The physical placement is decided by the PRESET, not by any where-hint: retail/
        // game-location/work presets route to shops & workplaces; sub-spawn/folder presets get
        // tucked inside a container ("filing box"). So we exclude those and prefer a standalone
        // note whose readingSource renders a DDS document (evidenceNote / mainEvidenceText).
        private static void EnsureNotePreset()
        {
            if (_scanned) return;
            try
            {
                var tb = Toolbox.Instance;
                var list = tb != null ? tb.placePerOwnerInteractables : null;
                if (list == null) return;   // not ready yet — retry next call (don't mark scanned)
                _scanned = true;

                InteractablePreset best = null; int bestScore = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p == null || HardExclude(p)) continue;
                    int s = Score(p);
                    if (s > bestScore) { bestScore = s; best = p; }
                }
                _notePreset = best;
                MotivesPlugin.Log.LogInfo(best != null
                    ? $"[SODMotives] clue: chosen note preset = '{SafeName(best)}' (src={SafeSrc(best)} score={bestScore})"
                    : "[SODMotives] clue: NO clean home-note preset found.");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: preset scan error: {e}"); }
        }

        // Must-not: not readable, retail leaflet, game-location distributed, work-placed,
        // building-restricted, or a reading source that won't render a document tree.
        private static bool HardExclude(InteractablePreset p)
        {
            try
            {
                if (!p.readingEnabled) return true;
                if (p.retailItem != null) return true;
                if (p.alwaysPlaceAtGameLocation) return true;
                if (p.frequencyPerGamelocationMin > 0) return true;
                if (p.placeAtWork) return true;
                if (p.limitToCertainBuildings) return true;
                var src = p.readingSource;
                if (src != InteractablePreset.ReadingModeSource.evidenceNote &&
                    src != InteractablePreset.ReadingModeSource.mainEvidenceText) return true;
            }
            catch { return true; }
            return false;
        }

        // Prefer standalone (no sub-spawn / no folder), evidenceNote, home-placed, note/letter-named.
        private static int Score(InteractablePreset p)
        {
            int s = 0;
            try { if (p.readingSource == InteractablePreset.ReadingModeSource.evidenceNote) s += 10; else s += 5; } catch { }
            try { if (!p.useSubSpawning) s += 4; } catch { }
            try { if (p.folderPlacementChance <= 0f) s += 4; } catch { }
            try { if (p.placeAtHome) s += 2; } catch { }
            try { if (p.putDownAtHome) s += 1; } catch { }
            string low = SafeName(p).ToLowerInvariant();
            if (low.Contains("note")) s += 4; else if (low.Contains("letter")) s += 3;
            else if (low.Contains("memo") || low.Contains("paper") || low.Contains("postit")) s += 2;
            return s;
        }

        internal static void InjectForCase(MurderController.Murder murder)
        {
            if (!Enable || murder == null) return;
            Human victim = murder.victim;
            if (victim == null) return;
            if (_injected.Contains(victim.humanID)) return;   // once per case
            _injected.Add(victim.humanID);

            try
            {
                EnsureNotePreset();
                if (_notePreset == null) { MotivesPlugin.Log.LogInfo("[SODMotives] clue: no usable note preset; skipping."); return; }

                NewAddress home = null; try { home = victim.home; } catch { }
                if (home == null) { MotivesPlugin.Log.LogInfo("[SODMotives] clue: victim has no home; skipping."); return; }

                MotivesPlugin.Log.LogInfo($"[SODMotives] clue: inject start state={murder.state} victimHome={home.name} notePreset='{SafeName(_notePreset)}'");

                // Affairs of the home's residents (dedup by pair). At most a couple's-worth.
                var affairs = new List<(Human sender, Human recipient)>();
                var seenPairs = new HashSet<long>();
                try
                {
                    var inhab = home.inhabitants;
                    if (inhab != null)
                        for (int i = 0; i < inhab.Count; i++)
                        {
                            Human h = inhab[i];
                            if (h == null) continue;
                            Human lover = null; try { lover = h.paramour; } catch { }
                            if (lover == null) continue;
                            if (!seenPairs.Add(PairKey(h.humanID, lover.humanID))) continue;
                            affairs.Add((h, lover));
                        }
                }
                catch { }

                if (affairs.Count == 0)
                {
                    MotivesPlugin.Log.LogInfo($"[SODMotives] clue: no affairs among residents of {home.name}; no motive notes injected.");
                    return;
                }

                if (!CluesByVictim.TryGetValue(victim.humanID, out var recList)) { recList = new List<string>(); CluesByVictim[victim.humanID] = recList; }

                int idx = 0, placed = 0;
                foreach (var af in affairs)
                {
                    if (placed >= MaxClues) break;
                    var pick = InfidelityTrees[idx % InfidelityTrees.Length];
                    string treeId = pick.id, treeName = pick.name;
                    idx++;

                    Interactable note = PlaceHomeNote(home, victim, af.sender, af.recipient, treeId);
                    if (note == null)
                    {
                        MotivesPlugin.Log.LogInfo($"[SODMotives] clue: PlaceObject failed for affair {MotivesPlugin.Name(af.sender)}->{MotivesPlugin.Name(af.recipient)}.");
                        continue;
                    }

                    bool textSet = false;
                    try
                    {
                        // From sender to recipient (writer->participantA, reciever->participantB).
                        note.SetWriter(af.sender);
                        try { note.SetReciever(af.recipient); } catch { }
                        note.SetDDSOverride(treeId);
                        var ev = note.evidence;
                        if (ev != null) { ev.SetOverrideDDS(treeId); ev.SetWriter(af.sender); }
                        if (_rng.NextDouble() < FingerprintChance)
                        {
                            try { note.AddNewDynamicFingerprint(af.sender, Interactable.PrintLife.manualRemoval); }
                            catch (Exception e4) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: print add: {e4.Message}"); }
                        }
                        if (ObviousNames && ev != null)
                        {
                            try { ev.AddOrSetCustomName(Evidence.DataKey.name, $"MODCLUE affair {MotivesPlugin.Name(af.sender)}->{MotivesPlugin.Name(af.recipient)}"); } catch { }
                            try { note.UpdateName(true, Evidence.DataKey.name); } catch { note.UpdateName(); }
                        }
                        else note.UpdateName();
                        textSet = true;
                    }
                    catch (Exception e2) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: set-text error: {e2.Message}"); }

                    // Location + verification (did it actually land in the victim's home?).
                    string locDesc = "?", posDesc = ""; bool atHome = false;
                    try
                    {
                        var node = note.node;
                        if (node != null)
                        {
                            var gl = node.gameLocation;
                            string loc = gl != null ? gl.name : "?";
                            atHome = gl != null && gl.name == home.name;
                            string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { }
                            locDesc = string.IsNullOrEmpty(room) ? loc : $"{room}, {loc}";
                            try { var p = node.position; posDesc = $" @({p.x:0.0},{p.y:0.0},{p.z:0.0})"; } catch { }
                        }
                    }
                    catch { }

                    int clueId = -1; string ddsNow = "?", presetNm = "?";
                    try { clueId = note.id; } catch { }
                    try { ddsNow = note.dds; } catch { }
                    try { presetNm = note.preset != null ? note.preset.name : "<null>"; } catch { }
                    bool ddsOk = ddsNow == treeId;

                    bool victimInvolved = Motive.Same(af.sender, victim) || Motive.Same(af.recipient, victim);
                    string rec = $"affair {(victimInvolved ? "(victim) " : "")}from {MotivesPlugin.Name(af.sender)} to {MotivesPlugin.Name(af.recipient)} [{locDesc}]{posDesc} atHome={atHome} dds='{treeName}' id={clueId} preset='{presetNm}' [text={textSet} ddsOk={ddsOk}]";
                    recList.Add(rec);
                    MotivesPlugin.Log.LogInfo($"[SODMotives] clue: INJECTED {rec}");
                    placed++;
                }
                MotivesPlugin.Log.LogInfo($"[SODMotives] clue: placed {placed} affair note(s) for victim {MotivesPlugin.Name(victim)} ({affairs.Count} resident affair(s) found).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: inject error: {e}"); }
        }

        // Place a readable note INSIDE the victim's home via NewGameLocation.PlaceObject — the
        // primitive MurderController.SpawnItem wraps. Unlike SpawnItem it returns the actual note
        // (not the container) and is constrained to this location. Retry ownership rules until one
        // yields a placement.
        private static Interactable PlaceHomeNote(NewAddress home, Human victim, Human sender, Human recipient, string treeId)
        {
            // belongsTo = a resident of this home so ownership resolves here.
            Human owner = victim;
            try { if (recipient != null && home.inhabitants != null && home.inhabitants.Contains(recipient)) owner = recipient; } catch { }

            var rules = new[]
            {
                InteractablePreset.OwnedPlacementRule.both,
                InteractablePreset.OwnedPlacementRule.prioritiseNonOwned,
                InteractablePreset.OwnedPlacementRule.nonOwnedOnly,
            };
            foreach (var rule in rules)
            {
                try
                {
                    FurnitureLocation furn;
                    Interactable note = home.PlaceObject(
                        _notePreset, owner, sender, recipient, out furn,
                        passVariable: false,           // selects PlaceObject overload 1
                        forceSecuritySettings: true,
                        forcedSecurity: 0,
                        forcedOwnership: rule,
                        forcedPriority: 5,
                        placeClosestTo: home.anchorNode,
                        ddsOverride: treeId,
                        ignoreLimits: true);
                    if (note != null) return note;
                }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: PlaceObject({rule}) threw: {e.Message}"); }
            }
            return null;
        }

        private static long PairKey(int x, int y)
        {
            int lo = Math.Min(x, y), hi = Math.Max(x, y);
            return ((long)lo << 32) | (uint)hi;
        }

        private static string SafeName(InteractablePreset p)
        {
            try { return p != null ? p.name : "<null>"; } catch { return "?"; }
        }

        private static string SafeSrc(InteractablePreset p)
        {
            try { return p != null ? p.readingSource.ToString() : "?"; } catch { return "?"; }
        }
    }
}

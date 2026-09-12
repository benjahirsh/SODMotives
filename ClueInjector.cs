using System;
using System.Collections.Generic;
using UnityEngine;

namespace SODMotives
{
    // Pool-driven motive clues (W3). For each overridden victim, inject one clue-set PER event in
    // their recorded suspect pool — regardless of motive type — each clue placed at the victim's
    // HOME or WORKPLACE at random. Placement is MOTIVE-AGNOSTIC on purpose: an affair letter can
    // surface at the office, a termination notice at home, so the clue's location never betrays the
    // motive. Every clue carries its author's prints/handwriting (the suspicion layer); vanilla's
    // physical evidence (weapon prints / CCTV / alibi) still convicts the one real killer.
    internal static class ClueInjector
    {
        internal static bool Enable = true;
        internal static int MaxClues = 8;                 // safety cap on total clues per case
        internal static bool ObviousNames = true;         // TESTING: rename injected notes so they're easy to spot
        internal static bool UseMultiPageList = false;     // false = reliable DDS note (names in title + connections tab); true = experimental multipage (placeable multipage items only dump profile tiles — no readable clue, no connections tab)
        internal static float FingerprintChance = 0.7f;   // chance a note carries its author's prints
        internal static float WorkplaceClueShare = 0.5f;  // chance a given clue is placed at the workplace vs home

        private static readonly System.Random _rng = new System.Random();

        private static InteractablePreset _notePreset;
        private static InteractablePreset _listPreset;   // multipage-reading clone: the redundancy list's clickable name list
        private static List<InteractablePreset> _listItemPresets;  // NATIVE multipage-reading placeable items, ranked best-first (scanned once)
        private static bool _listItemResolved;
        private static bool _scanned;

        // For F9 overlay.
        internal static readonly Dictionary<int, List<string>> CluesByVictim = new Dictionary<int, List<string>>();
        private static readonly HashSet<int> _injected = new HashSet<int>();
        private static int _killerId = -1;   // set per case -> guarantee the killer's clue carries their prints
        private static bool _killerLinked;   // set true once the killer AUTHORED an injected clue
        private static readonly HashSet<string> _placedRooms = new HashSet<string>(); // per-case: at most one clue per room (the board crosses two notices in the SAME room)

        // Clear per-case clue state on a new game so reused humanIDs don't skip injection
        // (via _injected) or surface a previous city's clue lines in F9. Called from SeedForNewGame.
        internal static void ResetForNewGame()
        {
            _injected.Clear();
            CluesByVictim.Clear();
        }

        // Persistence (pass 2): the victim ids whose clue-set has already been injected this game.
        // Saved so a reload can restore it — otherwise `_injected` is empty after load and, now that
        // pass 2 restores OverriddenVictimIds + PoolByVictim, a SetMurderState(post) that re-fires on
        // load would pass every gate and inject a DUPLICATE clue-set (+ duplicate note records).
        internal static List<int> GetInjectedVictimIds()
        {
            var outList = new List<int>(_injected.Count);
            foreach (int id in _injected) outList.Add(id);
            return outList;
        }

        // Persistence (pass 2): on LOAD, restore which victims were already injected so re-injection is
        // suppressed for them, while a case saved BEFORE the kill (not yet injected) can still inject
        // when it reaches 'post'. Also drops any prior in-session load's F9 clue-location lines.
        internal static void RestoreInjectedOnLoad(List<int> victimIds)
        {
            _injected.Clear();
            CluesByVictim.Clear();
            if (victimIds != null) for (int i = 0; i < victimIds.Count; i++) _injected.Add(victimIds[i]);
        }

        // Register a clone of srcTreeId under newTreeId — identical rendering, distinct id. Idempotent;
        // returns newTreeId on success, else falls back to srcTreeId. Il2Cpp-safe: explicit field
        // assignment, a NEW list holding the SAME message element refs (so msgIDs/text are shared).
        private static string CloneDDSTree(string srcTreeId, string newTreeId)
        {
            try
            {
                var trees = Toolbox.Instance != null ? Toolbox.Instance.allDDSTrees : null;
                if (trees == null) return srcTreeId;

                DDSSaveClasses.DDSTreeSave existing;
                if (trees.TryGetValue(newTreeId, out existing) && existing != null) return newTreeId;   // already registered

                DDSSaveClasses.DDSTreeSave src;
                if (!trees.TryGetValue(srcTreeId, out src) || src == null) return srcTreeId;             // source not loaded yet

                var clone = new DDSSaveClasses.DDSTreeSave();
                clone.id = newTreeId;                 // the allDDSTrees key the renderer resolves
                clone.name = src.name;
                clone.treeType = src.treeType;        // must stay 'document' or it renders blank
                clone.repeat = src.repeat;
                clone.triggerPoint = src.triggerPoint;
                clone.stopMovement = src.stopMovement;
                clone.ignoreGlobalRepeat = src.ignoreGlobalRepeat;
                clone.startingMessage = src.startingMessage;
                clone.treeChance = src.treeChance;
                clone.priority = src.priority;
                clone.newspaperCategory = src.newspaperCategory;
                clone.newspaperContext = src.newspaperContext;
                clone.interactionCitizenLimitation = src.interactionCitizenLimitation;
                clone.interactionOnePerCity = src.interactionOnePerCity;
                clone.citizenAddCount = src.citizenAddCount;
                clone.participantA = src.participantA;   // loose writer/receiver flags preserved
                clone.participantB = src.participantB;
                clone.participantC = src.participantC;
                clone.participantD = src.participantD;
                clone.document = src.document;
                clone.itemPool = src.itemPool;
                clone.messageRef = src.messageRef;

                var srcMsgs = src.messages;
                if (srcMsgs != null)
                {
                    var list = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSMessageSettings>();
                    for (int i = 0; i < srcMsgs.Count; i++) list.Add(srcMsgs[i]);   // same element refs => identical text
                    clone.messages = list;
                }
                else clone.messages = src.messages;

                trees[newTreeId] = clone;   // indexer registration, BEFORE any PlaceObject/SetDDSOverride
                MotivesPlugin.Log.LogInfo($"[SODMotives] clue: registered DDS clone '{newTreeId}' <- Probation_Notice.");
                return newTreeId;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: CloneDDSTree('{newTreeId}') failed: {e.Message}"); return srcTreeId; }
        }

        // Affair-appropriate DDS document trees (verified treeType==document, participants loose,
        // message saidBy:0->saidTo:1 so writer=from, reciever=to). vmail trees render blank.
        private static readonly (string id, string name)[] InfidelityTrees = {
            ("9b6d2119-f1a0-4b47-880d-1ee6fb417716", "Cheaters_Letter"),
            ("e6cfbb79-cc0b-4848-b699-280cbe261c3e", "Cheaters_Love_Poem"),
            ("7b3abe1a-30dd-41b1-b0ec-8fe6e739d2a2", "Flower_Note_Affair"),
        };
        // Workplace DDS document trees (verified treeType==document, loose, writer/receiver tokens).
        private const string PromotionLetterTreeId = "e8ad7cb2-0671-438e-93de-78af4e405307"; // Employment Contract
        private const string PromotionLetterTreeName = "Employment Contract";
        // Layoffs render EVERY notice as Probation_Notice, but each gets its OWN tree id (the real
        // tree + runtime clones) so no two same-sender notices share a (writer, tree) pair — the pair
        // the case board collapses recipient-links on. Clones reuse Probation_Notice's messages, so
        // they render pixel-identical with zero new message/block/Strings entries.
        private const string ProbationNoticeTreeId = "3a57626e-b359-4c28-b6ae-75135e32a737";
        // Body for the single redundancy list: Ev_PrintedEmployeeDB — a treeType-2 HR/personnel
        // printout, writer-only, deterministic, with NO passcode/address token (the old
        // Employees_Office_Code embedded |writer.passcode|, which rendered the note as a door-code
        // doc and merged the code into its title). Cloned per case so it can't collide with a vanilla
        // copy. (No boss-authored "memo about staff" document exists — those are all vmail/blank.)
        private const string RedundancyMemoTreeId = "10084020-1a7d-4048-8885-28c8985199e1"; // Ev_PrintedEmployeeDB
        // The game connects a note to its reciever via the note's EvidencePreset.factSetup entry whose
        // link==Subject.receiver (a FactPreset OBJECT, not a name). We harvest that preset and reuse it
        // to draw one board line per laid-off employee. Cloned + allowDuplicates=true so N links to N
        // distinct citizens don't collapse to one. Resolved once (same note preset for every clue).
        private static FactPreset _connectFp;
        private static string _connectFpName;   // original preset name (the factPresetDictionary key CreateFact needs)
        private static bool _connectSwap;
        private static bool _connectResolved;
        // The board crosses the connections of ANY two notes sharing a tree id — even across separate
        // murders whose notes still lie in the world — so every layoff notice gets a GLOBALLY-UNIQUE
        // clone id. Monotonic, never reset within a process (allDDSTrees is rebuilt on restart).
        private static int _cloneSeq;
        private const string ThreatTreeId = "0b32451b-a6d3-40a3-ba1d-5b645e251bdd";          // Dodgy_NoteRat (static threat)
        private const string ThreatTreeName = "Dodgy_NoteRat";

        // ---- preset selection: a clean, placeable, single-page readable note ----
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
                    : "[SODMotives] clue: NO clean note preset found.");

                // The game auto-stamps prints on a spawned note per InteractablePreset.printsSource
                // (owners/receivers/etc.) DURING setup — after our injection — which is how the
                // RECIPIENT (the suspect!) ends up printed on their own unsent notice, trivializing
                // the case. Clone the shared preset (don't mutate the vanilla asset) and force
                // printsSource = writers, so ONLY the author (boss / rival) ever leaves a print.
                if (best != null)
                {
                    try
                    {
                        string origSrc = "?"; try { origSrc = best.printsSource.ToString(); } catch { }
                        var clone = UnityEngine.Object.Instantiate(best);
                        clone.hideFlags = HideFlags.HideAndDontSave;
                        clone.printsSource = RoomConfiguration.PrintsSource.writers;
                        _notePreset = clone;
                        MotivesPlugin.Log.LogInfo($"[SODMotives] clue: note preset printsSource {origSrc} -> writers (author-only prints).");
                    }
                    catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: preset clone/printsSource failed ({ce.Message}); using shared preset as-is."); }

                    // Second clone for the redundancy LIST: same base item, but rendered as a multipage
                    // evidence (a clickable, board-pinnable citizen list) instead of DDS body text.
                    try
                    {
                        var lc = UnityEngine.Object.Instantiate(best);
                        lc.hideFlags = HideFlags.HideAndDontSave;
                        lc.printsSource = RoomConfiguration.PrintsSource.writers;
                        lc.readingSource = InteractablePreset.ReadingModeSource.multipageEvidence;
                        _listPreset = lc;
                        MotivesPlugin.Log.LogInfo("[SODMotives] clue: list preset ready (multipageEvidence reading source).");
                    }
                    catch (Exception le) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: list preset clone failed ({le.Message}); redundancy list will use DDS body."); }
                }
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
            // Any mod-overridden case (affair OR workplace) gets clues; vanilla cases get nothing.
            if (!MurderSelector.OverriddenVictimIds.Contains(victim.humanID)) return;
            if (_injected.Contains(victim.humanID)) return;   // once per case
            _injected.Add(victim.humanID);
            try { _killerId = murder.murderer != null ? murder.murderer.humanID : -1; } catch { _killerId = -1; }
            _killerLinked = false;
            _placedRooms.Clear();
            try { DebugTools.ClueHud.Clear(); } catch { }   // reset the on-screen clue-location readout for this case
            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: killer = {MotivesPlugin.Name(murder.murderer)}");

            try
            {
                EnsureNotePreset();
                if (_notePreset == null) { MotivesPlugin.Log.LogInfo("[SODMotives] clue: no usable note preset; skipping."); return; }

                if (!MurderSelector.PoolByVictim.TryGetValue(victim.humanID, out var pool) || pool == null || pool.Count == 0)
                { MotivesPlugin.Log.LogInfo($"[SODMotives] clue: no recorded pool for victim {MotivesPlugin.Name(victim)}; skipping."); return; }

                if (!CluesByVictim.TryGetValue(victim.humanID, out var recList)) { recList = new List<string>(); CluesByVictim[victim.humanID] = recList; }

                // Distinct events behind this victim's suspects (each drops its own motive clues).
                var seenEvt = new HashSet<SocialEvent>();
                var events = new List<SocialEvent>();
                for (int i = 0; i < pool.Count; i++)
                {
                    var ev = pool[i].evt;
                    if (ev != null && seenEvt.Add(ev)) events.Add(ev);
                }

                // Process the KILLER's own event first, so their clue places into empty furniture
                // before any locker/desk capacity limit can drop it.
                if (_killerId >= 0)
                {
                    SocialEvent kEvt = null;
                    for (int i = 0; i < pool.Count; i++)
                        if (pool[i].suspect != null && pool[i].suspect.humanID == _killerId) { kEvt = pool[i].evt; break; }
                    if (kEvt != null && events.Remove(kEvt)) events.Insert(0, kEvt);
                }

                MotivesPlugin.Log.LogInfo($"[SODMotives] clue: inject start state={murder.state} victim={MotivesPlugin.Name(victim)} events={events.Count} preset='{SafeName(_notePreset)}'");

                int placed = 0;
                foreach (var ev in events)
                {
                    if (placed >= MaxClues) break;
                    switch (ev.type)
                    {
                        case SocialEventType.Affair:    placed += InjectAffair(victim, ev, recList, placed); break;
                        case SocialEventType.Promotion: placed += InjectPromotion(victim, ev, recList, placed); break;
                        case SocialEventType.Layoffs:   placed += InjectLayoffs(victim, ev, recList, placed); break;
                        case SocialEventType.Eviction:  placed += InjectEviction(victim, ev, recList, placed); break;
                        case SocialEventType.RentArrears: placed += InjectMoneyThreat(victim, ev, recList, placed); break;
                        case SocialEventType.Debt:      placed += InjectMoneyThreat(victim, ev, recList, placed); break;
                        case SocialEventType.Feud:      placed += InjectFeud(victim, ev, recList, placed); break;
                    }
                }
                // No forced "killer clue": the suspicion layer names/reveals the suspects (termination
                // notices name each laid-off; promotion threats reveal each rival), and vanilla's
                // physical evidence convicts the one. killer-first ordering (above) just ensures the
                // killer's own notice/threat is among those placed, so they're in the shortlist.
                MotivesPlugin.Log.LogInfo($"[SODMotives] clue: placed {placed} clue(s) for victim {MotivesPlugin.Name(victim)} across {events.Count} event(s); killerAuthoredClue={_killerLinked}.");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: inject error: {e}"); }
        }

        // Affair: one shared love letter between the two lovers (writer -> reciever).
        private static int InjectAffair(Human victim, SocialEvent ev, List<string> recList, int placed)
        {
            if (placed >= MaxClues || ev.a == null || ev.b == null) return 0;
            var pick = InfidelityTrees[_rng.Next(InfidelityTrees.Length)];
            var note = PlaceClue(victim, ev.a, ev.b, pick.id);
            if (note == null) { LogFail("affair", ev.a, ev.b); return 0; }
            Finish(note, victim, ev.a, ev.b, pick.id, pick.name, $"affair {MotivesPlugin.Name(ev.a)}->{MotivesPlugin.Name(ev.b)}", recList);
            return 1;
        }

        // Promotion: a promotion letter (boss -> promotee) + one threat note per passed-over rival
        // (rival -> victim, carrying the rival's own prints).
        private static int InjectPromotion(Human victim, SocialEvent ev, List<string> recList, int placed)
        {
            int n = 0;
            // Threat notes (rival -> victim), KILLER FIRST so a placement miss can't drop the killer's.
            var rivals = KillerFirst(ev.group);
            for (int i = 0; i < rivals.Count && placed + n < MaxClues; i++)
            {
                Human rival = rivals[i];
                if (rival == null) continue;
                var note = PlaceClue(victim, rival, victim, ThreatTreeId);
                if (note != null) { Finish(note, victim, rival, victim, ThreatTreeId, ThreatTreeName, $"threat from {MotivesPlugin.Name(rival)}", recList); n++; }
                else LogFail("threat", rival, victim);
            }
            // Promotion letter (context) after the threats: boss -> promotee.
            if (placed + n < MaxClues && ev.a != null && ev.b != null)
            {
                var note = PlaceClue(victim, ev.b, ev.a, PromotionLetterTreeId);
                if (note != null) { Finish(note, victim, ev.b, ev.a, PromotionLetterTreeId, PromotionLetterTreeName, $"promotion letter for {MotivesPlugin.Name(ev.a)}", recList, unsentDoc: true); n++; }
                else LogFail("promotion letter", ev.b, ev.a);
            }
            return n;
        }

        // Eviction (landlord = victim): ONE landlord-authored redevelopment PLAN placed at the landlord's
        // HOME, naming the PROPERTY ADDRESSES being cleared (each aggrieved tenant's unit) as clickable
        // location links + board connections — the trail the player follows to the buildings, then to
        // the residents. Landlord's prints only. (Not a tenant name-list: it's a property schedule.)
        private static int InjectEviction(Human victim, SocialEvent ev, List<string> recList, int placed)
        {
            if (placed >= MaxClues || ev.a == null) return 0;
            Human landlord = ev.a;   // the landlord/victim who wrote the plan

            // The pool suspects for THIS event (tenants), for the optional list + connections.
            var uniq = new List<Human>(); var seen = new HashSet<int>();
            if (MurderSelector.PoolByVictim.TryGetValue(victim.humanID, out var pool) && pool != null)
                for (int i = 0; i < pool.Count; i++)
                { var e = pool[i]; if (e.evt == ev && e.suspect != null && seen.Add(e.suspect.humanID)) uniq.Add(e.suspect); }
            if (uniq.Count == 0)
                for (int i = 0; i < ev.group.Count; i++) { var h = ev.group[i]; if (h != null && seen.Add(h.humanID)) uniq.Add(h); }

            // Always list the tenants: the game exposes NO landlord->tenant trail the player can follow
            // (no per-tenancy agreements in the landlord's filebox/computer, no owner->buildings lookup,
            // no name-reveal API), so this plan is the only reliable way to discover the suspect pool.
            int linkedBody;
            string treeId = BuildEvictionDocTree(landlord, uniq, out linkedBody);
            if (treeId == null) { LogFail("eviction plan", landlord, victim); return 0; }

            // Place at the landlord's HOME (their private plan); fall back to the generic home/work placement.
            Interactable note = null;
            NewGameLocation home = null; try { home = landlord.home; } catch { }
            if (home != null) note = PlaceClueNote(home, landlord, landlord, null, treeId, null);
            if (note == null) note = PlaceClue(victim, landlord, null, treeId);
            if (note == null) { LogFail("eviction plan", landlord, victim); return 0; }

            var docEv = note.evidence;
            try { note.SetWriter(landlord); } catch { }
            try { if (docEv != null) docEv.SetWriter(landlord); } catch { }
            try { note.SetDDSOverride(treeId); } catch { }
            if (docEv != null) { try { docEv.SetOverrideDDS(treeId); } catch { } }
            try { note.AddNewDynamicFingerprint(landlord, Interactable.PrintLife.manualRemoval); } catch { }

            if (docEv != null)
            {
                try
                {
                    docEv.AddOrSetCustomName(Evidence.DataKey.name, ObviousNames ? "MODCLUE Redevelopment Plan" : "Redevelopment Plan");
                    try { var tied = docEv.GetTiedKeys(Evidence.DataKey.name); if (tied != null) for (int i = 0; i < tied.Count; i++) { var k = tied[i]; if (k != Evidence.DataKey.name) { try { docEv.AddOrSetCustomName(k, ""); } catch { } } } } catch { }
                    try { docEv.AddOrSetCustomName(Evidence.DataKey.code, ""); } catch { }
                    try { docEv.UpdateName(); } catch { }
                    try { note.UpdateName(true, Evidence.DataKey.name); } catch { note.UpdateName(); }
                }
                catch { }
            }

            // One board connection line per listed property address (doc -> location).
            int linked = 0;
            var fp = ResolveConnectFactPreset(docEv);
            for (int i = 0; i < uniq.Count; i++) { var hm = SafeHome(uniq[i]); if (hm != null && AddLocationConnection(docEv, hm, fp)) linked++; }

            string where = "?", locDesc = "?";
            try { var node = note.node; if (node != null) { var gl = node.gameLocation; string loc = gl != null ? gl.name : "?"; string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { } locDesc = string.IsNullOrEmpty(room) ? loc : $"{room}, {loc}"; where = ClassifyLocation(victim, loc); } } catch { }
            int itemId = -1; try { itemId = note.id; } catch { }
            try
            {
                var pids = new List<int>();
                for (int i = 0; i < uniq.Count; i++) if (uniq[i] != null) pids.Add(uniq[i].humanID);
                Persistence.RecordNote(itemId, treeId, "eviction", landlord != null ? landlord.humanID : -1, pids,
                    ObviousNames ? "MODCLUE Redevelopment Plan" : "Redevelopment Plan");
            }
            catch (Exception pe) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: record eviction note: {pe.Message}"); }
            string rec = $"eviction plan [{where}: {locDesc}] itemId={itemId} addresses={uniq.Count} bodyLinks={linkedBody} connected={linked}";
            recList.Add(rec);
            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: INJECTED {rec}");
            try { DebugTools.AddClueHud($"redevelopment plan — {where}: {locDesc}"); } catch { }
            return 1;
        }

        // Money-motive threat clue — RentArrears (a=tenant/victim, b=landlord) AND generic Debt
        // (a=debtor/victim, b=creditor). Both drop ONE unsigned, threatening DEBT note at the VICTIM's
        // HOME, HANDWRITTEN in the suspect's own hand + carrying their PRINT (two latent forensic leads,
        // NOT a spelled-out sender). The generic "settle your debt" wording reads the same for rent, a
        // loan, or any money grievance. The culprit is deduced from the trail + confirmed via handwriting
        // + print. (Was InjectRentArrears; generalized in V2.4 to also serve debts — identical clue.)
        private static int InjectMoneyThreat(Human victim, SocialEvent ev, List<string> recList, int placed)
        {
            if (placed >= MaxClues || ev.a == null || ev.b == null) return 0;
            Human creditor = ev.b;   // suspect / author (landlord or lender)

            int linkedBody;
            string treeId = BuildRentDemandDocTree(creditor, out linkedBody);
            if (treeId == null) { LogFail("threatening note", creditor, victim); return 0; }
            return InjectAnonThreatNote(victim, creditor, treeId, linkedBody, "rentdemand", recList);
        }

        // Feud (bidirectional bad blood between ev.a and ev.b): ONE unsigned, HOSTILE threat note at the
        // VICTIM's HOME, handwritten in the AGGRESSOR's hand + their print. The aggressor for THIS case is
        // whichever feuding party ISN'T the victim (the killer, if it's the killer's own feud event; every
        // feud suspect's note is identical, so the killer reads no guiltier than a herring). The threat
        // note is anonymous — the identity lead is the knower's testimony (which names the other party).
        private static int InjectFeud(Human victim, SocialEvent ev, List<string> recList, int placed)
        {
            if (placed >= MaxClues || ev.a == null || ev.b == null) return 0;
            Human aggressor = Motive.Same(ev.a, victim) ? ev.b : ev.a;
            if (aggressor == null) return 0;

            int linkedBody;
            string treeId = BuildFeudThreatDocTree(aggressor, out linkedBody);
            if (treeId == null) { LogFail("threatening note", aggressor, victim); return 0; }
            return InjectAnonThreatNote(victim, aggressor, treeId, linkedBody, "feudthreat", recList);
        }

        // Shared placement for an anonymous, handwritten threat note (money OR feud): place at the
        // victim's HOME with `writer` set via the placement param (=> its handwriting is theirs) but NO
        // explicit Evidence.SetWriter (the raw writer field is a LATENT handwriting lead, not a spelled-out
        // "from"); add the writer's PRINT; give it the generic "Threatening Note" title; and TRACK its id
        // so the Evidence.AutoCreateFacts postfix (Plugin.cs) removes the lazily-created writer "From" fact
        // once it materialises — keeping the handwriting matchable. `kind` ("rentdemand"|"feudthreat") is
        // recorded so the save/reload rebuild re-registers the right body + re-hides the writer connection.
        private static int InjectAnonThreatNote(Human victim, Human writer, string treeId, int linkedBody, string kind, List<string> recList)
        {
            const string label = "Threatening Note";

            Interactable note = null;
            NewGameLocation home = null; try { home = victim.home; } catch { }
            if (home != null) note = PlaceClueNote(home, victim, writer, victim, treeId, null);
            if (note == null) note = PlaceClue(victim, writer, victim, treeId);
            if (note == null) { LogFail("threatening note", writer, victim); return 0; }

            var docEv = note.evidence;
            // Deliberately NO Evidence.SetWriter — that was what surfaced the suspect as a discovered "from".
            // Handwriting (writer field above) + the print (below) are the two latent leads to the suspect.
            try { note.SetDDSOverride(treeId); } catch { }
            if (docEv != null) { try { docEv.SetOverrideDDS(treeId); } catch { } }
            try { note.AddNewDynamicFingerprint(writer, Interactable.PrintLife.manualRemoval); } catch { }

            if (docEv != null)
            {
                try
                {
                    docEv.AddOrSetCustomName(Evidence.DataKey.name, ObviousNames ? "MODCLUE " + label : label);
                    try { var tied = docEv.GetTiedKeys(Evidence.DataKey.name); if (tied != null) for (int i = 0; i < tied.Count; i++) { var k = tied[i]; if (k != Evidence.DataKey.name) { try { docEv.AddOrSetCustomName(k, ""); } catch { } } } } catch { }
                    try { docEv.AddOrSetCustomName(Evidence.DataKey.code, ""); } catch { }
                    try { docEv.UpdateName(); } catch { }
                    try { note.UpdateName(true, Evidence.DataKey.name); } catch { note.UpdateName(); }
                }
                catch { }
            }

            // The writer "from" fact is created LAZILY (on first examine), so hiding it here would be too
            // early (allFacts is empty now). Track this note id; the Harmony postfix on Evidence.AutoCreateFacts
            // (Plugin.cs) hides the writer connection ONCE the fact exists — keeping the handwriting matchable.
            try { int nid = note.id; if (nid >= 0) AnonWriterNoteIds.Add(nid); } catch { }

            string where = "?", locDesc = "?";
            try { var node = note.node; if (node != null) { var gl = node.gameLocation; string loc = gl != null ? gl.name : "?"; string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { } locDesc = string.IsNullOrEmpty(room) ? loc : $"{room}, {loc}"; where = ClassifyLocation(victim, loc); } } catch { }
            int itemId = -1; try { itemId = note.id; } catch { }
            try
            {
                // pids empty (no board connections to rebuild on reload). authorId kept so the reload
                // rebuild can re-register the note in the suspect's handwriting; the rebuild sets NO
                // visible writer (anonymous) and re-hides the writer connection.
                var pids = new List<int>();
                Persistence.RecordNote(itemId, treeId, kind, writer != null ? writer.humanID : -1, pids,
                    ObviousNames ? "MODCLUE " + label : label);
            }
            catch (Exception pe) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: record threat note ({kind}): {pe.Message}"); }
            string rec = $"threatening note ({kind}) [{where}: {locDesc}] itemId={itemId} writer={MotivesPlugin.Name(writer)} bodyLinks={linkedBody}";
            recList.Add(rec);
            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: INJECTED {rec}");
            try { DebugTools.AddClueHud($"threatening note — {where}: {locDesc}"); } catch { }
            return 1;
        }

        // Anonymous-writer notes (rent-arrears threat notes): their writer "from" fact is created LAZILY on
        // first examine, so it can only be hidden after the fact exists. Track their interactable ids here;
        // a Harmony postfix on Evidence.AutoCreateFacts (Plugin.cs) calls HideWriterConnection once per note.
        internal static readonly HashSet<int> AnonWriterNoteIds = new HashSet<int>();
        internal static readonly HashSet<int> WriterHiddenIds = new HashSet<int>();   // hidden-once guard

        // REMOVE a placed note's WRITER "From" connection to `person`. isSeen=false does NOT hide it from the
        // Connections tab (proven: the From fact rendered with isSeen already false), so we drop the FactLink
        // entirely via Evidence.RemoveFactLink. The note keeps its writer FIELD (=> the handwriting sample /
        // DataKey stays, matchable) and its fingerprint — only the auto-created writer→name connection goes.
        // Matches the fact by GetOther==person.evidenceEntry (the verified matchWriter test) or preset "From".
        // Runs on every AutoCreateFacts pass (no hide-once) so a re-materialised From is re-removed; a genuine
        // later handwriting match creates the connection through a different path and is left intact.
        internal static int HideWriterConnection(Evidence docEv, Human person)
        {
            if (docEv == null || person == null) return 0;
            int removed = 0;
            try
            {
                Evidence pev = null;
                try { pev = person.evidenceEntry; } catch { }
                if (pev == null) { try { person.CreateEvidence(); pev = person.evidenceEntry; } catch { } }
                var facts = docEv.allFacts;
                int total = facts != null ? facts.Count : 0;
                var toRemove = new List<Fact>();
                if (facts != null)
                    for (int i = 0; i < total; i++)
                    {
                        try
                        {
                            var fl = facts[i];
                            if (fl == null) continue;
                            var f = fl.fact;
                            if (f == null) continue;
                            bool isWriter = false;
                            try { var other = f.GetOther(docEv); if (other != null && pev != null && other.Pointer == pev.Pointer) isWriter = true; } catch { }
                            if (!isWriter) { try { if (f.preset != null && f.preset.name == "From") isWriter = true; } catch { } }
                            if (isWriter) toRemove.Add(f);
                        }
                        catch { }
                    }
                for (int i = 0; i < toRemove.Count; i++) { try { docEv.RemoveFactLink(toRemove[i]); removed++; } catch (Exception re) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: RemoveFactLink: {re.Message}"); } }
                // Belt-and-braces: drop the postedBy discovery too, in case the connection is discovery-gated.
                try { var dp = docEv.discoveryProgress; if (dp != null) for (int i = dp.Count - 1; i >= 0; i--) { try { if (dp[i] == Evidence.Discovery.postedByDiscovery) dp.RemoveAt(i); } catch { } } } catch { }
                if (removed > 0) MotivesPlugin.Log.LogInfo($"[SODMotives] clue: removed {removed} writer 'From' fact(s) of {total} on note (handwriting/print kept).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: hide writer connection failed: {e.Message}"); }
            return removed;
        }

        // Layoffs: ONE boss-authored "redundancy list". Preferred form = an EvidenceMultiPage whose
        // body is the list of laid-off employees rendered as CLICKABLE, board-pinnable names (the same
        // mechanism as the vanilla employee roster / calendar). Falls back to a DDS HR-record note with
        // the names in its title if the multipage path is unavailable. Either way it draws one case-board
        // connection line to each suspect, and carries only the boss's print.
        private static int InjectLayoffs(Human victim, SocialEvent ev, List<string> recList, int placed)
        {
            if (placed >= MaxClues || ev.a == null) return 0;
            Human boss = ev.a;   // the boss/victim who wrote the list

            // Name the list from the actual SUSPECT POOL for THIS event (a victim's pool can mix
            // motives), NOT the raw company roster — so we never list a non-suspect employee and the
            // board connections match exactly the people the case treats as suspects.
            var uniq = new List<Human>(); var seen = new HashSet<int>();
            if (MurderSelector.PoolByVictim.TryGetValue(victim.humanID, out var pool) && pool != null)
                for (int i = 0; i < pool.Count; i++)
                { var e = pool[i]; if (e.evt == ev && e.suspect != null && seen.Add(e.suspect.humanID)) uniq.Add(e.suspect); }
            if (uniq.Count == 0)   // fallback: pool lookup empty -> fall back to the event group
                for (int i = 0; i < ev.group.Count; i++) { var h = ev.group[i]; if (h != null && seen.Add(h.humanID)) uniq.Add(h); }
            if (uniq.Count == 0) return 0;

            // PRIMARY: a custom DDS document whose BODY is a readable list of the laid-off staff as
            // clickable citizen links (built by registering our own tree/message/block + text). The
            // multipage route is a dead end (placeable multipage items just dump tiles); the DDS
            // employee-record note is the last-resort fallback (names in title + connections tab).
            if (InjectLayoffsCustomText(victim, ev, boss, uniq, recList)) return 1;
            if (UseMultiPageList && TryMultiPageList(victim, ev, boss, uniq, recList)) return 1;
            return InjectLayoffsDDS(victim, ev, boss, uniq, recList);
        }

        // Build a per-note custom DDS *document* tree whose single always-display block renders OUR
        // text — a readable, clickable list of the laid-off staff. Registers block/message/tree in
        // Toolbox's DDS dictionaries and writes the block string into the "dds.blocks" table (the same
        // pipeline the game + DDSLoader mods use). Names are emitted as <link=id> markup minted from
        // each citizen's evidence, so they're clickable/board-pinnable. Returns the tree id, or null.
        // NOTE: these registrations + link ids are in-memory/session-only; a save+reload would need
        // them re-registered (persistence is a follow-up).
        // Emit a clickable citizen link (<link=id>Name</link>) minted from the citizen's evidence node,
        // or the plain name if none. Increments `linked` on a successful link.
        private static string CitizenLink(Human h, ref int linked)
        {
            string disp = SocialEvent.SafeName(h);
            if (h == null) return disp;
            try
            {
                Evidence cev = null;
                try { cev = h.evidenceEntry; } catch { }
                if (cev == null) { try { h.CreateEvidence(); cev = h.evidenceEntry; } catch { } }
                if (cev != null)
                {
                    var ld = Strings.AddOrGetLink(cev, null);
                    if (ld != null) { linked++; return "<link=\"" + ld.id + "\">" + disp + "</link>"; }
                }
            }
            catch { }
            return disp;
        }

        // Emit a clickable ADDRESS link (<link=id>address</link>) for a tenant's home unit, minted from
        // the location's evidence (EvidenceLocation : Evidence), or plain address text if none. Used by
        // the eviction plan to name the PROPERTIES being cleared instead of the tenants. Increments
        // `linked` on a successful link.
        private static string AddressLink(Human tenant, ref int linked)
        {
            NewAddress home = SafeHome(tenant);
            if (home == null) return "a property";
            string disp = "a property";
            try { if (!string.IsNullOrEmpty(home.name)) disp = home.name; } catch { }
            try
            {
                Evidence lev = null;
                try { lev = home.evidenceEntry; } catch { }
                if (lev == null) { try { home.CreateEvidence(); lev = home.evidenceEntry; } catch { } }
                if (lev != null)
                {
                    var ld = Strings.AddOrGetLink(lev, null);
                    if (ld != null) { linked++; return "<link=\"" + ld.id + "\">" + disp + "</link>"; }
                }
            }
            catch { }
            return disp;
        }

        private static NewAddress SafeHome(Human h) { try { return h != null ? h.home : null; } catch { return null; } }

        // Register a custom DDS *document* tree whose single always-display block renders `body`
        // (arbitrary readable text; TMP <link> markup preserved). Registers block/message/tree into
        // Toolbox's DDS dictionaries + writes the block text into the "dds.blocks" table (the same
        // pipeline the game + DDSLoader mods use). Returns the tree id, or null. On save/reload replay,
        // pass the surviving note's tree id as `fixedTreeId` (only the tree id must match; block/message
        // keys can be freshly minted). NOTE: these registrations + link ids are session-only.
        private static string RegisterCustomDocTree(string body, string fixedTreeId = null, string font = "TruetypewriterPolyglott SDF", float fontSize = 14f, bool isHandwriting = false)
        {
            try
            {
                var tb = Toolbox.Instance;
                if (tb == null) return null;
                string uid = System.Guid.NewGuid().ToString("N");
                string blockKey = "sodmotives.doc.block." + uid;
                string messageKey = "sodmotives.doc.msg." + uid;
                string treeId = !string.IsNullOrEmpty(fixedTreeId) ? fixedTreeId : ("sodmotives.doc.tree." + uid);

                // 1) Register the block's TEXT into the "dds.blocks" string table (proven DDSLoader path).
                int lineNo = 1;
                try { var st = Strings.stringTable; if (st != null && st.ContainsKey("dds.blocks")) lineNo = st["dds.blocks"].Count + 1; } catch { }
                Strings.LoadIntoDictionary("dds.blocks", lineNo, blockKey, body, "", 0, false, true);

                // 2) Block object (holds no text — the text lives in the string table above).
                var block = new DDSSaveClasses.DDSBlockSave();
                block.name = blockKey; block.id = blockKey;
                try { block.replacements = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSReplacement>(); } catch { }
                tb.allDDSBlocks[blockKey] = block;

                // 3) Message referencing that block, always shown.
                var msg = new DDSSaveClasses.DDSMessageSave();
                msg.name = messageKey; msg.id = messageKey;
                msg.blocks = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSBlockCondition>();
                msg.AddBlock(blockKey);
                try
                {
                    if (msg.blocks.Count > 0)
                    {
                        var c0 = msg.blocks[0];
                        c0.alwaysDisplay = true; c0.group = 0;
                        try { c0.useTraits = false; } catch { }
                        try { c0.traits = new Il2CppSystem.Collections.Generic.List<string>(); } catch { }
                    }
                }
                catch { }
                tb.allDDSMessages[messageKey] = msg;

                // 4) Document tree pointing at that message.
                var tree = new DDSSaveClasses.DDSTreeSave();
                tree.name = treeId; tree.id = treeId;
                tree.treeType = DDSSaveClasses.TreeType.document;
                tree.messages = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSMessageSettings>();
                tree.messageRef = new Il2CppSystem.Collections.Generic.Dictionary<string, DDSSaveClasses.DDSMessageSettings>();
                try { tree.document = new DDSSaveClasses.DDSDocument(); } catch { }
                string inst = tree.AddMessage(messageKey);
                tree.startingMessage = inst;

                // Layout: a freshly-added message defaults to fontSize 0 (renders huge and overflows).
                // Copy the game's own document body layout (Probation_Notice): small typewriter font in a
                // bounded, paged text box on a paper page.
                try
                {
                    DDSSaveClasses.DDSMessageSettings ms = null;
                    try { if (tree.messages != null && tree.messages.Count > 0) ms = tree.messages[0]; } catch { }
                    if (ms != null)
                    {
                        try { ms.font = font; } catch { }         // caller's font (typewriter for formal docs; the writer's own hand for the threat note)
                        try { ms.fontSize = fontSize; } catch { } // caller's size (default was 0 -> huge)
                        try { ms.isHandwriting = isHandwriting; } catch { }   // mark as handwriting so the game treats the sample as a handwriting lead
                        try { ms.pos = new Vector2(1f, -15.5f); } catch { }
                        try { ms.size = new Vector2(316f, 397f); } catch { }
                        try { ms.lineSpace = 4f; } catch { }
                        try { ms.alignH = 0; } catch { }
                        try { ms.alignV = 0; } catch { }
                        try { ms.usePages = true; } catch { }   // paginate long bodies instead of overflowing
                    }
                }
                catch { }
                try
                {
                    if (tree.document != null)
                    {
                        try { tree.document.background = "CrumpledPaper"; } catch { }
                        try { tree.document.size = new Vector2(342f, 482f); } catch { }
                    }
                }
                catch { }

                tb.allDDSTrees[treeId] = tree;
                return treeId;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: RegisterCustomDocTree: {e.Message}"); return null; }
        }

        // Layoffs: a boss-authored redundancy list — heading + one clickable citizen link per laid-off
        // suspect, signed by the boss. (Body building split from tree registration in V2.3; the rendered
        // document is identical to before.)
        private static string BuildRedundancyDocTree(List<Human> uniq, Human author, out int linked, string fixedTreeId = null)
        {
            linked = 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("REDUNDANCY NOTICE\n\n");
            sb.Append("The following staff are scheduled for termination:\n\n");
            if (uniq != null)
                for (int i = 0; i < uniq.Count; i++) { sb.Append("- "); sb.Append(CitizenLink(uniq[i], ref linked)); sb.Append('\n'); }
            if (author != null) { sb.Append("\n\nIssued by: "); sb.Append(CitizenLink(author, ref linked)); }
            string treeId = RegisterCustomDocTree(sb.ToString(), fixedTreeId);
            if (treeId != null) MotivesPlugin.Log.LogInfo($"[SODMotives] clue: built custom redundancy tree '{treeId}' ({(uniq != null ? uniq.Count : 0)} names, {linked} clickable).");
            return treeId;
        }

        // Eviction: a landlord's confidential redevelopment plan naming the PROPERTY ADDRESSES being
        // cleared (each aggrieved tenant's unit) as clickable location links — the trail the player
        // follows to the buildings, then to the residents. Signed by the landlord. `tenants` supplies
        // the affected units (via each tenant's home).
        private static string BuildEvictionDocTree(Human landlord, List<Human> tenants, out int linked, string fixedTreeId = null)
        {
            linked = 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("REDEVELOPMENT PLAN — CONFIDENTIAL\n\n");
            sb.Append("The following properties are to be cleared for redevelopment. All current tenancies are to be terminated and the residents moved out ahead of the works. Proceed quietly; expect complaints.\n\n");
            if (tenants != null && tenants.Count > 0)
            {
                sb.Append("Properties to be vacated:\n\n");
                for (int i = 0; i < tenants.Count; i++) { sb.Append("- "); sb.Append(AddressLink(tenants[i], ref linked)); sb.Append('\n'); }
            }
            if (landlord != null) { sb.Append("\n\nIssued by: "); sb.Append(CitizenLink(landlord, ref linked)); }
            string treeId = RegisterCustomDocTree(sb.ToString(), fixedTreeId);
            if (treeId != null) MotivesPlugin.Log.LogInfo($"[SODMotives] clue: built eviction plan tree '{treeId}' (addresses={(tenants != null ? tenants.Count : 0)}, {linked} clickable).");
            return treeId;
        }

        // RentArrears: an unsigned, HANDWRITTEN, threatening DEBT note — deliberately generic (no "rent"
        // wording) so a rent-arrears case reads identically to a future loan / money-feud case. The body
        // names nobody; the culprit is carried only by the note's handwriting (rendered in the landlord's
        // OWN handwriting font so it matches their profile sample) + fingerprint.
        private static string BuildRentDemandDocTree(Human landlord, out int linked, string fixedTreeId = null)
        {
            linked = 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("Settle your debt. Consider this your final warning.");
            // Render in the WRITER'S OWN handwriting font (Human.handwriting.fontAsset) so the note's hand
            // genuinely matches the killer's profile sample — fall back to a generic script if unavailable.
            string hwFont = "Pacific Beach Script Font SDF";
            try { if (landlord != null && landlord.handwriting != null && landlord.handwriting.fontAsset != null) { var fn = landlord.handwriting.fontAsset.name; if (!string.IsNullOrEmpty(fn)) hwFont = fn; } } catch { }
            string treeId = RegisterCustomDocTree(sb.ToString(), fixedTreeId, hwFont, 30f, true);
            if (treeId != null) MotivesPlugin.Log.LogInfo($"[SODMotives] clue: built rent demand tree '{treeId}' ({linked} clickable).");
            return treeId;
        }

        // Feud: an unsigned, HANDWRITTEN, HOSTILE threat note — a personal grudge, no money wording. The
        // body names nobody; the culprit is carried only by the note's handwriting (rendered in the
        // aggressor's OWN handwriting font so it matches their profile sample) + fingerprint. The
        // identity lead comes from a knower's testimony (which names the other feuding party).
        private static string BuildFeudThreatDocTree(Human aggressor, out int linked, string fixedTreeId = null)
        {
            linked = 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("You've had your last warning. Stay out of my way — or you'll regret it.");
            string hwFont = "Pacific Beach Script Font SDF";
            try { if (aggressor != null && aggressor.handwriting != null && aggressor.handwriting.fontAsset != null) { var fn = aggressor.handwriting.fontAsset.name; if (!string.IsNullOrEmpty(fn)) hwFont = fn; } } catch { }
            string treeId = RegisterCustomDocTree(sb.ToString(), fixedTreeId, hwFont, 30f, true);
            if (treeId != null) MotivesPlugin.Log.LogInfo($"[SODMotives] clue: built feud threat tree '{treeId}'.");
            return treeId;
        }

        // Save/reload replay (Persistence): the note OBJECT survived (its int id, its `dds` tree-id override,
        // and its title), but the custom DDS body, clickable name-links, and board connections were
        // runtime-only and are gone. Re-register the tree under the SAME id, re-point the live note at it,
        // and redraw the document->citizen connections. Does NOT place anything — the note already exists.
        internal static void RebuildCustomNote(Interactable note, List<Human> citizens, Human author, string treeId, string kind = null)
        {
            if (note == null || string.IsNullOrEmpty(treeId)) return;
            int linked;
            // Rebuild the SAME body the note was placed with — a kind-agnostic rebuild would re-render an
            // eviction plan as a redundancy list, or a rent demand as "...termination: <landlord>".
            string built =
                kind == "eviction"   ? BuildEvictionDocTree(author, citizens, out linked, treeId) :
                kind == "rentdemand" ? BuildRentDemandDocTree(author, out linked, treeId) :
                kind == "feudthreat" ? BuildFeudThreatDocTree(author, out linked, treeId) :
                                       BuildRedundancyDocTree(citizens, author, out linked, treeId);
            if (built == null) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: rebuild tree failed for '{treeId}'."); return; }

            var docEv = note.evidence;
            // Eviction/layoffs notes are SIGNED, so restore their visible writer. The anonymous threat notes
            // (rentdemand/debt + feudthreat) keep their native writer (handwriting) but the writer CONNECTION
            // must stay hidden after reload.
            bool anonThreat = kind == "rentdemand" || kind == "feudthreat";
            bool setWriter = author != null && !anonThreat;
            try { if (setWriter) note.SetWriter(author); } catch { }
            try { note.SetDDSOverride(treeId); } catch { }
            if (docEv != null)
            {
                try { docEv.SetOverrideDDS(treeId); } catch { }
                try { if (setWriter) docEv.SetWriter(author); } catch { }
            }
            // Anonymous threat notes: re-track so the AutoCreateFacts postfix re-hides the writer connection
            // when the facts re-materialise after reload (WriterHiddenIds is cleared so it fires again).
            if (anonThreat) { try { int nid = note.id; if (nid >= 0) { AnonWriterNoteIds.Add(nid); WriterHiddenIds.Remove(nid); } } catch { } }

            int reconnected = 0;
            var fp = ResolveConnectFactPreset(docEv);
            if (citizens != null)
                for (int i = 0; i < citizens.Count; i++)
                {
                    // Eviction connects to the property ADDRESSES (each tenant's home); others to the citizen.
                    bool ok = (kind == "eviction") ? AddLocationConnection(docEv, SafeHome(citizens[i]), fp)
                                                   : AddCitizenConnection(docEv, citizens[i], fp);
                    if (ok) reconnected++;
                }

            int id = -1; try { id = note.id; } catch { }
            MotivesPlugin.Log.LogInfo($"[SODMotives] persist: rebuilt note id={id} tree='{treeId}' names={(citizens != null ? citizens.Count : 0)} bodyLinks={linked} reconnected={reconnected}.");
            try { DebugTools.AddClueHud($"restored list (reload) — id={id}"); } catch { }
        }

        // Preferred layoffs clue: a placed note whose body is our custom readable/clickable list, plus
        // a clean title and the connections tab. Returns false (nothing placed) so the caller can fall
        // back if the custom tree can't be built or placed.
        private static bool InjectLayoffsCustomText(Human victim, SocialEvent ev, Human boss, List<Human> uniq, List<string> recList)
        {
            int linkedBody;
            string treeId = BuildRedundancyDocTree(uniq, boss, out linkedBody);
            if (treeId == null) return false;

            var note = PlaceClue(victim, boss, null, treeId);   // normal readable note + our custom document tree
            if (note == null) { LogFail("redundancy list (custom)", boss, victim); return false; }

            var docEv = note.evidence;
            try { note.SetWriter(boss); } catch { }
            try { if (docEv != null && boss != null) docEv.SetWriter(boss); } catch { }
            try { note.SetDDSOverride(treeId); } catch { }
            if (docEv != null) { try { docEv.SetOverrideDDS(treeId); } catch { } }
            try { note.AddNewDynamicFingerprint(boss, Interactable.PrintLife.manualRemoval); } catch { }

            // Clean title (names live in the body now, so just "Redundancy List"); blank tied keys.
            if (docEv != null)
            {
                try
                {
                    docEv.AddOrSetCustomName(Evidence.DataKey.name, ObviousNames ? "MODCLUE Redundancy List" : "Redundancy List");
                    try { var tied = docEv.GetTiedKeys(Evidence.DataKey.name); if (tied != null) for (int i = 0; i < tied.Count; i++) { var k = tied[i]; if (k != Evidence.DataKey.name) { try { docEv.AddOrSetCustomName(k, ""); } catch { } } } } catch { }
                    try { docEv.AddOrSetCustomName(Evidence.DataKey.code, ""); } catch { }
                    try { docEv.UpdateName(); } catch { }
                    try { note.UpdateName(true, Evidence.DataKey.name); } catch { note.UpdateName(); }
                }
                catch { }
            }

            // Connections tab: one line per suspect.
            int linked = 0;
            var fp = ResolveConnectFactPreset(docEv);
            for (int i = 0; i < uniq.Count; i++) if (AddCitizenConnection(docEv, uniq[i], fp)) linked++;

            string where = "?", locDesc = "?";
            try { var node = note.node; if (node != null) { var gl = node.gameLocation; string loc = gl != null ? gl.name : "?"; string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { } locDesc = string.IsNullOrEmpty(room) ? loc : $"{room}, {loc}"; where = ClassifyLocation(victim, loc); } } catch { }
            int itemId = -1; try { itemId = note.id; } catch { }
            // Persist for save/reload: record the ids needed to re-register the custom tree + reconnect.
            try
            {
                var pids = new List<int>();
                for (int i = 0; i < uniq.Count; i++) if (uniq[i] != null) pids.Add(uniq[i].humanID);
                Persistence.RecordNote(itemId, treeId, "layoffs", boss != null ? boss.humanID : -1, pids,
                    ObviousNames ? "MODCLUE Redundancy List" : "Redundancy List");
            }
            catch (Exception pe) { MotivesPlugin.Log.LogWarning($"[SODMotives] persist: record layoffs note: {pe.Message}"); }
            string rec = $"redundancy list (custom text) [{where}: {locDesc}] itemId={itemId} names={uniq.Count} bodyLinks={linkedBody} connected={linked}";
            recList.Add(rec);
            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: INJECTED {rec}");
            try { DebugTools.AddClueHud($"redundancy list — {where}: {locDesc}"); } catch { }
            return true;
        }

        // DEBUG (F4): drop a custom-text note into `loc` right now — same BuildRedundancyDocTree path as
        // the real clue — so custom-text rendering can be verified without waiting for a layoffs case.
        internal static bool SpawnTestCustomNote(NewGameLocation loc, List<Human> people, Human writer)
        {
            try
            {
                if (loc == null || people == null || people.Count == 0) { MotivesPlugin.Log.LogWarning("[SODMotives][TEST] need a location + people."); return false; }
                EnsureNotePreset();
                if (_notePreset == null) { MotivesPlugin.Log.LogWarning("[SODMotives][TEST] no note preset."); return false; }
                _placedRooms.Clear();   // allow repeated test placements in the same room

                int linkedBody;
                string treeId = BuildRedundancyDocTree(people, writer, out linkedBody);
                if (treeId == null) { MotivesPlugin.Log.LogWarning("[SODMotives][TEST] tree build failed."); return false; }

                var note = PlaceClueNote(loc, writer, writer, null, treeId, null);
                if (note == null) { MotivesPlugin.Log.LogWarning("[SODMotives][TEST] placement failed."); return false; }

                var docEv = note.evidence;
                try { note.SetWriter(writer); } catch { }
                try { note.SetDDSOverride(treeId); } catch { }
                if (docEv != null) { try { docEv.SetOverrideDDS(treeId); } catch { } try { if (writer != null) docEv.SetWriter(writer); } catch { } }
                try { if (writer != null) note.AddNewDynamicFingerprint(writer, Interactable.PrintLife.manualRemoval); } catch { }
                if (docEv != null)
                {
                    try
                    {
                        docEv.AddOrSetCustomName(Evidence.DataKey.name, "MODCLUE Test Redundancy List");
                        try { var tied = docEv.GetTiedKeys(Evidence.DataKey.name); if (tied != null) for (int i = 0; i < tied.Count; i++) { var k = tied[i]; if (k != Evidence.DataKey.name) { try { docEv.AddOrSetCustomName(k, ""); } catch { } } } } catch { }
                        try { docEv.AddOrSetCustomName(Evidence.DataKey.code, ""); } catch { }
                        try { docEv.UpdateName(); } catch { }
                        try { note.UpdateName(true, Evidence.DataKey.name); } catch { note.UpdateName(); }
                    }
                    catch { }
                }
                var fp = ResolveConnectFactPreset(docEv);
                int linked = 0; for (int i = 0; i < people.Count; i++) if (AddCitizenConnection(docEv, people[i], fp)) linked++;

                string locDesc = "?";
                try { var node = note.node; if (node != null) { var gl = node.gameLocation; string ln = gl != null ? gl.name : "?"; string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { } locDesc = string.IsNullOrEmpty(room) ? ln : $"{room}, {ln}"; } } catch { }
                int itemId = -1; try { itemId = note.id; } catch { }
                // Persist for save/reload — makes the F4 test note itself the persistence test harness.
                try
                {
                    var pids = new List<int>();
                    for (int i = 0; i < people.Count; i++) if (people[i] != null) pids.Add(people[i].humanID);
                    Persistence.RecordNote(itemId, treeId, "test", writer != null ? writer.humanID : -1, pids, "MODCLUE Test Redundancy List");
                }
                catch (Exception pe) { MotivesPlugin.Log.LogWarning($"[SODMotives][TEST] persist: record note: {pe.Message}"); }
                MotivesPlugin.Log.LogInfo($"[SODMotives][TEST] placed custom-text note [{locDesc}] itemId={itemId} people={people.Count} bodyLinks={linkedBody} connected={linked} tree={treeId}");
                try { DebugTools.AddClueHud($"TEST list — {locDesc}"); } catch { }
                return true;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][TEST] SpawnTestCustomNote: {e}"); return false; }
        }

        // DEBUG (F4): drop the THREATENING debt note into `loc` right now — the exact InjectRentArrears
        // path (handwritten script; landlord = writer/handwriting + print; writer CONNECTION hidden) — so
        // it can be iterated without waiting for a rent-arrears murder. `landlord` supplies handwriting+print.
        internal static bool SpawnTestThreatNote(NewGameLocation loc, Human landlord)
        {
            try
            {
                if (loc == null || landlord == null) { MotivesPlugin.Log.LogWarning("[SODMotives][TEST] need a location + a landlord citizen."); return false; }
                EnsureNotePreset();
                if (_notePreset == null) { MotivesPlugin.Log.LogWarning("[SODMotives][TEST] no note preset."); return false; }
                _placedRooms.Clear();   // allow repeated test placements in the same room

                int linkedBody;
                string treeId = BuildRentDemandDocTree(landlord, out linkedBody);
                if (treeId == null) { MotivesPlugin.Log.LogWarning("[SODMotives][TEST] tree build failed."); return false; }

                var note = PlaceClueNote(loc, landlord, landlord, null, treeId, null);
                if (note == null) { MotivesPlugin.Log.LogWarning("[SODMotives][TEST] placement failed."); return false; }

                var docEv = note.evidence;
                // NO explicit Evidence.SetWriter (mirrors InjectRentArrears); the writer field is set via placement.
                try { note.SetDDSOverride(treeId); } catch { }
                if (docEv != null) { try { docEv.SetOverrideDDS(treeId); } catch { } }
                try { note.AddNewDynamicFingerprint(landlord, Interactable.PrintLife.manualRemoval); } catch { }
                if (docEv != null)
                {
                    try
                    {
                        docEv.AddOrSetCustomName(Evidence.DataKey.name, "MODCLUE Threatening Note");
                        try { var tied = docEv.GetTiedKeys(Evidence.DataKey.name); if (tied != null) for (int i = 0; i < tied.Count; i++) { var k = tied[i]; if (k != Evidence.DataKey.name) { try { docEv.AddOrSetCustomName(k, ""); } catch { } } } } catch { }
                        try { docEv.AddOrSetCustomName(Evidence.DataKey.code, ""); } catch { }
                        try { docEv.UpdateName(); } catch { }
                        try { note.UpdateName(true, Evidence.DataKey.name); } catch { note.UpdateName(); }
                    }
                    catch { }
                }
                try { int nid = note.id; if (nid >= 0) AnonWriterNoteIds.Add(nid); } catch { }   // AutoCreateFacts postfix hides the writer connection once its fact exists

                string locDesc = "?";
                try { var node = note.node; if (node != null) { var gl = node.gameLocation; string ln = gl != null ? gl.name : "?"; string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { } locDesc = string.IsNullOrEmpty(room) ? ln : $"{room}, {ln}"; } } catch { }
                int itemId = -1; try { itemId = note.id; } catch { }
                try
                {
                    var pids = new List<int>();
                    Persistence.RecordNote(itemId, treeId, "rentdemand", landlord != null ? landlord.humanID : -1, pids, "MODCLUE Threatening Note");
                }
                catch (Exception pe) { MotivesPlugin.Log.LogWarning($"[SODMotives][TEST] persist: record note: {pe.Message}"); }
                MotivesPlugin.Log.LogInfo($"[SODMotives][TEST] placed THREATENING note [{locDesc}] itemId={itemId} landlord={MotivesPlugin.Name(landlord)} (handwriting+print, writer connection hidden) tree={treeId}");
                try { DebugTools.AddClueHud($"TEST threat note — {locDesc}"); } catch { }
                return true;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][TEST] SpawnTestThreatNote: {e}"); return false; }
        }

        // Preferred: place a NATIVE multipage item (whose read interaction is wired for multipage at
        // setup — the reading UI decides mode at placement, so a post-placement preset swap is inert),
        // then populate ITS OWN EvidenceMultiPage in place with one clickable citizen row per suspect.
        // Returns false (leaving nothing placed) if any prerequisite is missing, so the caller can fall
        // back to the DDS-body note.
        private static bool TryMultiPageList(Human victim, SocialEvent ev, Human boss, List<Human> uniq, List<string> recList)
        {
            var candidates = ResolveMultiPageItemPresets();
            if (candidates == null || candidates.Count == 0) { MotivesPlugin.Log.LogInfo("[SODMotives] clue: no native multipage item preset; using DDS list."); return false; }

            // PlaceObject NREs when given a NULL DDS override (independent of the preset), so seed a real
            // tree — a multipage item ignores it at read-time. Try candidates best-first until one places.
            string seedTree = CloneDDSTree(RedundancyMemoTreeId, "SODMotives_RList_" + (++_cloneSeq));
            Interactable note = null; InteractablePreset itemPreset = null;
            for (int c = 0; c < candidates.Count && note == null; c++)
            {
                itemPreset = candidates[c];
                note = PlaceClue(victim, boss, null, seedTree, itemPreset);
                if (note == null) MotivesPlugin.Log.LogInfo($"[SODMotives] clue: multipage item '{SafeName(itemPreset)}' failed to place; trying next.");
            }
            if (note == null) { LogFail("redundancy list (multipage)", boss, victim); return false; }

            // Use the item's OWN evidence (mutating the reader's cached object avoids rebind uncertainty).
            EvidenceMultiPage mp = null;
            try { var ev0 = note.evidence; mp = ev0 != null ? ev0.TryCast<EvidenceMultiPage>() : null; } catch { }
            if (mp == null)
            {
                // The native item didn't yield a multipage evidence — create one and bind it.
                EvidencePreset rosterPreset = ResolveMultiPagePreset(victim);
                if (rosterPreset != null)
                {
                    try
                    {
                        var controller = note.evidence != null ? note.evidence.controller : null;
                        var created = EvidenceCreator.Instance.CreateEvidence(rosterPreset, System.Guid.NewGuid().ToString(), controller, boss, boss, null, null, false, null);
                        mp = created != null ? created.TryCast<EvidenceMultiPage>() : null;
                        if (mp != null) { try { note.evidence = mp; } catch { } try { mp.interactable = note; } catch { } }
                    }
                    catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: CreateEvidence(multipage) failed: {e.Message}"); }
                }
            }
            if (mp == null) { MotivesPlugin.Log.LogWarning("[SODMotives] clue: no multipage evidence; using DDS list."); try { note.SafeDelete(); } catch { } return false; }

            // Replace any native auto-content (e.g. calendar dates / real staff) with OUR suspect list.
            try { var pc = mp.pageContent; if (pc != null) pc.Clear(); } catch { }
            int pageIdx = -1, rows = 0;
            for (int i = 0; i < uniq.Count; i++)
            {
                Human h = uniq[i]; Evidence cev = null;
                try { cev = h.evidenceEntry; } catch { }
                if (cev == null) { try { h.CreateEvidence(); cev = h.evidenceEntry; } catch { } }
                if (cev == null) continue;
                try { if (pageIdx < 0) pageIdx = mp.AddEvidenceToNewPage(cev); else mp.AddEvidenceToPage(pageIdx, cev); rows++; }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: AddEvidenceToPage({MotivesPlugin.Name(h)}): {e.Message}"); }
            }
            if (rows == 0) { MotivesPlugin.Log.LogWarning("[SODMotives] clue: multipage got 0 rows; using DDS list."); try { note.SafeDelete(); } catch { } return false; }

            var fp = ResolveConnectFactPreset(mp);   // citizen-link preset from the roster/multipage preset

            try { mp.SetWriter(boss); } catch { }
            try { note.SetWriter(boss); } catch { }
            try { note.AddNewDynamicFingerprint(boss, Interactable.PrintLife.manualRemoval); } catch { }
            try { mp.AddOrSetCustomName(Evidence.DataKey.name, ObviousNames ? "MODCLUE Redundancy List" : "Redundancy List"); } catch { }
            try { mp.UpdateName(); } catch { }
            try { note.UpdateName(true, Evidence.DataKey.name); } catch { try { note.UpdateName(); } catch { } }

            // One case-board connection line per suspect (document -> citizen).
            int linked = 0;
            for (int i = 0; i < uniq.Count; i++) if (AddCitizenConnection(mp, uniq[i], fp)) linked++;

            string where = "?", locDesc = "?";
            try { var node = note.node; if (node != null) { var gl = node.gameLocation; string loc = gl != null ? gl.name : "?"; string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { } locDesc = string.IsNullOrEmpty(room) ? loc : $"{room}, {loc}"; where = ClassifyLocation(victim, loc); } } catch { }
            int itemId = -1; try { itemId = note.id; } catch { }
            string rec = $"redundancy list (multipage) [{where}: {locDesc}] itemId={itemId} rows={rows}/{uniq.Count} linked={linked} item='{SafeName(itemPreset)}'";
            recList.Add(rec);
            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: INJECTED {rec}");
            return true;
        }

        // Find placeable items whose reading source is NATIVELY multipageEvidence (so the reading UI
        // renders them as a paged, clickable list). Scanned once; ranked best-first, penalising the
        // context-bound auto-populated types (ResidentRoster/Calendar/…) that need a building/company
        // and NRE when placed in arbitrary furniture. Cloned with author-only prints. Null if the
        // dictionary isn't ready yet (retried next case).
        private static List<InteractablePreset> ResolveMultiPageItemPresets()
        {
            if (_listItemResolved) return _listItemPresets;
            var dict = Toolbox.Instance != null ? Toolbox.Instance.objectPresetDictionary : null;
            if (dict == null) return null;   // not ready yet — retry next case
            _listItemResolved = true;
            var ranked = new List<KeyValuePair<int, InteractablePreset>>();
            try
            {
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    var p = en.Current.Value;
                    if (p == null) continue;
                    InteractablePreset.ReadingModeSource rs;
                    try { rs = p.readingSource; } catch { continue; }
                    if (rs != InteractablePreset.ReadingModeSource.multipageEvidence) continue;
                    string nm = SafeName(p);
                    bool ok = true;
                    try { if (!p.readingEnabled) ok = false; } catch { }
                    try { if (p.retailItem != null) ok = false; } catch { }
                    try { if (p.alwaysPlaceAtGameLocation) ok = false; } catch { }
                    try { if (p.limitToCertainBuildings) ok = false; } catch { }
                    string low = nm.ToLowerInvariant();
                    int s = 0;
                    // Readable rosters render as an actual name LIST (the target); a container like
                    // PaperStack just dumps anonymous tiles. Prefer rosters, then paper/document items.
                    if (low.Contains("roster") || low.Contains("resident")) s += 30;
                    else if (low.Contains("paper") || low.Contains("stack") || low.Contains("note") || low.Contains("memo") || low.Contains("document") || low.Contains("letter")) s += 20;
                    // Non-document / wrong meshes — deprioritise.
                    if (low.Contains("calendar") || low.Contains("phone") || low.Contains("clock") || low.Contains("disk") || low.Contains("wound") || low.Contains("box")) s -= 50;
                    MotivesPlugin.Log.LogInfo($"[SODMotives] clue: multipage item candidate '{nm}' placeable={ok} score={s}");
                    if (ok) ranked.Add(new KeyValuePair<int, InteractablePreset>(s, p));
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: ResolveMultiPageItemPresets: {e.Message}"); }
            ranked.Sort((a, b) => b.Key.CompareTo(a.Key));
            var outList = new List<InteractablePreset>();
            for (int i = 0; i < ranked.Count; i++)
            {
                var p = ranked[i].Value;
                try
                {
                    var c = UnityEngine.Object.Instantiate(p);
                    c.hideFlags = HideFlags.HideAndDontSave;
                    try { c.printsSource = RoomConfiguration.PrintsSource.writers; } catch { }
                    try { c.findEvidence = InteractablePreset.FindEvidence.none; } catch { }   // don't auto-fill the building's real residents
                    outList.Add(c);
                }
                catch { outList.Add(p); }
            }
            _listItemPresets = outList;
            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: multipage item candidates ranked: {outList.Count} (best='{(outList.Count > 0 ? SafeName(outList[0]) : "<none>")}').");
            return _listItemPresets;
        }

        // The victim IS the layoffs boss, so their company's employee roster is a live EvidenceMultiPage
        // whose preset we reuse to spin up our own list. Null if unavailable (-> DDS fallback).
        private static EvidencePreset ResolveMultiPagePreset(Human victim)
        {
            try
            {
                var comp = victim != null && victim.job != null ? victim.job.employer : null;
                var roster = comp != null ? comp.employeeRoster : null;
                if (roster != null && roster.preset != null) return roster.preset;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: ResolveMultiPagePreset: {e.Message}"); }
            return null;
        }

        private static string SafeEvPresetName(EvidencePreset p) { try { return p != null ? p.name : "<null>"; } catch { return "?"; } }

        // Fallback: a DDS HR-record note with the names in its TITLE (no clickable body list).
        private static int InjectLayoffsDDS(Human victim, SocialEvent ev, Human boss, List<Human> uniq, List<string> recList)
        {
            // One document; HR-record body (cloned so it can't collide with a vanilla copy); no receiver.
            string bodyTree = CloneDDSTree(RedundancyMemoTreeId, "SODMotives_RedundancyList_" + (++_cloneSeq));
            var note = PlaceClue(victim, boss, null, bodyTree);
            if (note == null) { LogFail("redundancy list", boss, victim); return 0; }
            Finish(note, victim, boss, null, bodyTree, "Redundancy_List", "redundancy list", recList, unsentDoc: true);

            var docEv = note.evidence;
            if (docEv == null) { MotivesPlugin.Log.LogWarning("[SODMotives] clue: redundancy list has no evidence."); return 1; }

            // Title = EXACTLY our list. The note preset ties extra DataKeys into the displayed title
            // (that's how a passcode leaked in before), so after setting 'name' we blank every OTHER
            // tied key or its generated value concatenates onto our string.
            try
            {
                var sb = new System.Text.StringBuilder(ObviousNames ? "MODCLUE Redundancy List: " : "Redundancy List: ");
                for (int i = 0; i < uniq.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(SocialEvent.SafeName(uniq[i])); }
                docEv.AddOrSetCustomName(Evidence.DataKey.name, sb.ToString());
                try
                {
                    var tied = docEv.GetTiedKeys(Evidence.DataKey.name);
                    if (tied != null)
                        for (int i = 0; i < tied.Count; i++)
                        { var k = tied[i]; if (k != Evidence.DataKey.name) { try { docEv.AddOrSetCustomName(k, ""); } catch { } } }
                }
                catch { }
                try { docEv.AddOrSetCustomName(Evidence.DataKey.code, ""); } catch { }   // never surface a passcode
                try { docEv.UpdateName(); } catch { }
                try { note.UpdateName(true, Evidence.DataKey.name); } catch { note.UpdateName(); }
            }
            catch { }

            // One case-board connection line per laid-off employee.
            int linked = 0;
            var fp = ResolveConnectFactPreset(docEv);
            for (int i = 0; i < uniq.Count; i++) if (AddCitizenConnection(docEv, uniq[i], fp)) linked++;
            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: redundancy list (DDS) for {MotivesPlugin.Name(boss)} — {uniq.Count} on list, {linked} board-connected.");
            return 1;
        }

        // The FactPreset the game itself uses to link a note to its reciever lives in the note's
        // EvidencePreset.factSetup (the entry whose link==Subject.receiver holds a FactPreset OBJECT).
        // We harvest it, then CLONE it with allowDuplicates=true so our N document->citizen links don't
        // collapse to one (a receiver preset is 1:1 by design). Resolved once — same note preset always.
        private static FactPreset ResolveConnectFactPreset(Evidence docEv)
        {
            if (_connectResolved) return _connectFp;
            _connectResolved = true;
            try
            {
                var pre = docEv != null ? docEv.preset : null;
                var fs = pre != null ? pre.factSetup : null;
                FactPreset chosen = null; bool swap = false;
                if (fs != null)
                {
                    for (int i = 0; i < fs.Count && chosen == null; i++)
                    { var e = fs[i]; if (e != null && e.preset != null && e.link == EvidencePreset.Subject.receiver) { chosen = e.preset; swap = e.switchFindingFactToFrom; } }
                    if (chosen == null) for (int i = 0; i < fs.Count && chosen == null; i++)
                    { var e = fs[i]; if (e != null && e.preset != null && e.link == EvidencePreset.Subject.writer) { chosen = e.preset; swap = e.switchFindingFactToFrom; } }
                    if (chosen == null) for (int i = 0; i < fs.Count && chosen == null; i++)
                    { var e = fs[i]; if (e != null && e.preset != null) { chosen = e.preset; swap = e.switchFindingFactToFrom; } }
                }
                if (chosen != null)
                {
                    _connectSwap = swap;
                    try { _connectFpName = chosen.name; } catch { }   // dictionary key for the CreateFact path
                    try
                    {
                        var fp = UnityEngine.Object.Instantiate(chosen);
                        fp.hideFlags = HideFlags.HideAndDontSave;
                        try { fp.allowDuplicates = true; } catch { }
                        try { fp.allowReverseDuplicates = true; } catch { }
                        _connectFp = fp;
                    }
                    catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: connect-fact clone failed ({ce.Message}); using shared preset."); _connectFp = chosen; }
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: ResolveConnectFactPreset: {e.Message}"); }

            // Fallback for evidence presets with no citizen fact in factSetup (e.g. a roster): use the
            // known document->citizen preset straight from the fact dictionary ('To' is the letter
            // recipient link that already works for the DDS note).
            if (_connectFp == null)
            {
                try
                {
                    var fpd = Toolbox.Instance != null ? Toolbox.Instance.factPresetDictionary : null;
                    if (fpd != null)
                    {
                        var cands = new[] { "To", "SentTo", "Recipient", "Reciever", "AddressedTo", "Writer", "From" };
                        for (int i = 0; i < cands.Length && _connectFp == null; i++)
                        {
                            FactPreset cand;
                            if (fpd.TryGetValue(cands[i], out cand) && cand != null) { _connectFp = cand; _connectFpName = cands[i]; _connectSwap = false; }
                        }
                    }
                }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: connect-fact dict fallback: {e.Message}"); }
            }

            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: connect-fact preset {(_connectFp != null ? "resolved" : "<none>")} name='{_connectFpName ?? "?"}' (swap={_connectSwap}).");
            return _connectFp;
        }

        // Draw ONE case-board connection: document -> the named citizen (their evidenceEntry node).
        private static bool AddCitizenConnection(Evidence docEv, Human emp, FactPreset fp)
        {
            if (docEv == null || emp == null) return false;
            Evidence empEv = null;
            try { empEv = emp.evidenceEntry; } catch { }
            if (empEv == null) { try { emp.CreateEvidence(); empEv = emp.evidenceEntry; } catch { } }
            return AddEvidenceConnection(docEv, empEv, fp);
        }

        // Draw ONE case-board connection: document -> a property address (its location evidence node).
        private static bool AddLocationConnection(Evidence docEv, NewAddress loc, FactPreset fp)
        {
            if (docEv == null || loc == null) return false;
            Evidence locEv = null;
            try { locEv = loc.evidenceEntry; } catch { }
            if (locEv == null) { try { loc.CreateEvidence(); locEv = loc.evidenceEntry; } catch { } }
            return AddEvidenceConnection(docEv, locEv, fp);
        }

        // Shared connection core: document -> target evidence node (a citizen OR a location).
        private static bool AddEvidenceConnection(Evidence docEv, Evidence targetEv, FactPreset fp)
        {
            if (docEv == null || targetEv == null) return false;
            Evidence from = _connectSwap ? targetEv : docEv;   // preset may want finding-fact reversed
            Evidence to = _connectSwap ? docEv : targetEv;

            // PRIMARY: the game's own fact pipeline with forced discovery. A raw `new Fact().ConnectFact()`
            // creates the link UNDISCOVERED, so it never draws on the board even after the note is read.
            // CreateFact(..., forceDiscoveryOnCreate:true) marks it discovered so the line renders.
            if (!string.IsNullOrEmpty(_connectFpName))
            {
                try
                {
                    var f = EvidenceCreator.Instance.CreateFact(_connectFpName, from, to, null, null, true, null, null, null, false);
                    if (f != null) return true;
                }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: CreateFact(connection): {e.Message}"); }
            }

            // FALLBACK: raw ctor with the cloned preset (allowDuplicates=true).
            if (fp == null) return false;
            try
            {
                var fromL = new Il2CppSystem.Collections.Generic.List<Evidence>(); fromL.Add(from);
                var toL = new Il2CppSystem.Collections.Generic.List<Evidence>(); toL.Add(to);
                var fact = new Fact(fp, fromL, toL, null, null, null, false);
                fact.ConnectFact();
                return true;
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: AddEvidenceConnection: {e.Message}"); return false; }
        }

        // Reorder a suspect group so the killer is first — their clue then places into empty
        // furniture before any locker/desk capacity limit can drop it.
        private static List<Human> KillerFirst(List<Human> group)
        {
            if (_killerId < 0 || group == null) return group;
            var outList = new List<Human>();
            for (int i = 0; i < group.Count; i++) { var h = group[i]; if (h != null && h.humanID == _killerId) outList.Add(h); }
            if (outList.Count == 0) return group;   // killer not in this group
            for (int i = 0; i < group.Count; i++) { var h = group[i]; if (h != null && h.humanID != _killerId) outList.Add(h); }
            return outList;
        }

        // Place one readable note at the victim's HOME or WORKPLACE (random order, fall back to the
        // other) via NewGameLocation.PlaceObject — the same primitive that returns the real note.
        private static Interactable PlaceClue(Human victim, Human writer, Human receiver, string treeId, InteractablePreset presetOverride = null)
        {
            foreach (var loc in ClueLocations(victim))
            {
                if (loc == null) continue;
                var note = PlaceClueNote(loc, victim, writer, receiver, treeId, presetOverride);
                if (note != null) return note;
            }
            return null;
        }

        // Home and workplace, in a randomized order so location never betrays motive.
        private static List<NewGameLocation> ClueLocations(Human victim)
        {
            NewGameLocation home = null, work = null;
            try { home = victim.home; } catch { }
            try { if (victim.job != null && victim.job.employer != null) work = victim.job.employer.placeOfBusiness; } catch { }
            var list = new List<NewGameLocation>();
            if (_rng.NextDouble() < WorkplaceClueShare) { if (work != null) list.Add(work); if (home != null) list.Add(home); }
            else { if (home != null) list.Add(home); if (work != null) list.Add(work); }
            return list;
        }

        private static Interactable PlaceClueNote(NewGameLocation loc, Human owner, Human writer, Human receiver, string treeId, InteractablePreset presetOverride = null)
        {
            var preset = presetOverride ?? _notePreset;
            // Prefer the OWNER's (victim's) OWN furniture first — at a workplace full of employee
            // lockers this keeps the clue in the victim's locker/desk (reachable while investigating
            // them) instead of a random coworker's locked locker. Degrade to shared/non-owned only
            // if the victim owns nowhere suitable here.
            var rules = new[]
            {
                InteractablePreset.OwnedPlacementRule.ownedOnly,
                InteractablePreset.OwnedPlacementRule.prioritiseOwned,
                InteractablePreset.OwnedPlacementRule.both,
                InteractablePreset.OwnedPlacementRule.prioritiseNonOwned,
            };
            foreach (var rule in rules)
            {
                try
                {
                    FurnitureLocation furn;
                    Interactable note = loc.PlaceObject(
                        preset, owner, writer, receiver, out furn,
                        passVariable: false,
                        forceSecuritySettings: true,
                        forcedSecurity: 0,
                        forcedOwnership: rule,
                        forcedPriority: 5,
                        placeClosestTo: null,   // don't pile every clue onto the one node nearest the entrance
                        ddsOverride: treeId,
                        ignoreLimits: true);
                    if (note == null) continue;
                    // At most ONE clue per room — the board crosses the recipient connections of two
                    // notices sharing a room. If this placement's room already holds a clue, DELETE it
                    // (PlaceObject already spawned it; abandoning leaves an orphan "vanilla" note) and
                    // try the next rule/location so the clue lands in a fresh room.
                    try
                    {
                        string roomKey = RoomKey(note);
                        if (roomKey != null && _placedRooms.Contains(roomKey))
                        {
                            try { note.SafeDelete(); } catch (Exception de) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: SafeDelete of same-room note failed: {de.Message}"); }
                            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: rejected+deleted note — room '{roomKey}' already has a clue ({rule}); trying next.");
                            continue;
                        }
                        if (roomKey != null) _placedRooms.Add(roomKey);
                    }
                    catch { }
                    return note;
                }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: PlaceObject({rule}) threw: {e.Message}"); }
            }
            return null;
        }

        // Set writer/reciever, override the DDS tree, add the author's prints, name + verify + record.
        private static void Finish(Interactable note, Human victim, Human writer, Human receiver, string treeId, string treeName, string label, List<string> recList, bool unsentDoc = false)
        {
            // "Linked" = the killer AUTHORED this clue, so their prints are on it (weapon-matchable).
            // Being merely the addressee of an UNSENT letter is not a forensic link (they never touched it).
            bool killerClue = _killerId >= 0 && writer != null && writer.humanID == _killerId;
            if (killerClue) _killerLinked = true;
            bool textSet = false;
            try
            {
                if (writer != null) note.SetWriter(writer);
                try { if (receiver != null) note.SetReciever(receiver); } catch { }
                note.SetDDSOverride(treeId);
                var ev = note.evidence;
                if (ev != null) { ev.SetOverrideDDS(treeId); if (writer != null) ev.SetWriter(writer); }
                // Only the AUTHOR's prints belong on a note — an UNSENT letter was touched only by its
                // writer, never the addressee. The killer's OWN authored clue ALWAYS carries their
                // prints (so it matches the murder weapon); other notes carry the author's at chance.
                if (writer != null)
                {
                    bool killerWrote = _killerId >= 0 && writer.humanID == _killerId;
                    if (killerWrote || _rng.NextDouble() < FingerprintChance)
                    {
                        try { note.AddNewDynamicFingerprint(writer, Interactable.PrintLife.manualRemoval); }
                        catch (Exception e4) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: print add: {e4.Message}"); }
                    }
                }
                // An UNSENT official document (termination notice / promotion letter) was never handled
                // by its addressee — strip any recipient print the game auto-stamped, keep the author's.
                if (unsentDoc && receiver != null)
                {
                    try
                    {
                        var df = note.df;
                        if (df != null)
                        {
                            int removed = 0;
                            for (int i = df.Count - 1; i >= 0; i--)
                            {
                                var p = df[i];
                                if (p != null && p.id == receiver.humanID) { note.RemoveDynamicPrint(p); removed++; }
                            }
                            if (removed > 0) MotivesPlugin.Log.LogInfo($"[SODMotives] clue: stripped {removed} recipient print(s) from unsent doc for {MotivesPlugin.Name(receiver)}.");
                        }
                    }
                    catch (Exception e7) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: strip recipient print: {e7.Message}"); }
                }
                if (ObviousNames && ev != null)
                {
                    try { ev.AddOrSetCustomName(Evidence.DataKey.name, $"MODCLUE {label}"); } catch { }
                    try { note.UpdateName(true, Evidence.DataKey.name); } catch { note.UpdateName(); }
                }
                else note.UpdateName();
                textSet = true;
            }
            catch (Exception e2) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: set-text error: {e2.Message}"); }

            // Where did it land? (home / work / elsewhere) + DDS-override verification.
            string locDesc = "?", posDesc = "", where = "?";
            try
            {
                var node = note.node;
                if (node != null)
                {
                    var gl = node.gameLocation;
                    string loc = gl != null ? gl.name : "?";
                    string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { }
                    locDesc = string.IsNullOrEmpty(room) ? loc : $"{room}, {loc}";
                    try { var p = node.position; posDesc = $" @({p.x:0.0},{p.y:0.0},{p.z:0.0})"; } catch { }
                    where = ClassifyLocation(victim, loc);
                }
            }
            catch { }

            int clueId = -1; string ddsNow = "?", presetNm = "?";
            try { clueId = note.id; } catch { }
            try { ddsNow = note.dds; } catch { }
            try { presetNm = note.preset != null ? note.preset.name : "<null>"; } catch { }
            bool ddsOk = ddsNow == treeId;

            string wr = writer != null ? MotivesPlugin.Name(writer) : "-";
            string rc = receiver != null ? MotivesPlugin.Name(receiver) : "-";
            string rbW = "?", rbR = "?";
            try { rbW = note.writer != null ? MotivesPlugin.Name(note.writer) : "-"; } catch { }
            try { rbR = note.reciever != null ? MotivesPlugin.Name(note.reciever) : "-"; } catch { }
            string dfIds = "?";
            try
            {
                var df = note.df;
                if (df != null)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < df.Count; i++) { var p = df[i]; if (p != null) { if (sb.Length > 0) sb.Append(','); sb.Append(p.id); } }
                    dfIds = sb.ToString();
                }
            }
            catch { }
            string rec = $"{label} [{where}: {locDesc}]{posDesc} intended(w={wr} r={rc}) readback(w={rbW} r={rbR}) dds='{treeName}' itemId={clueId} preset='{presetNm}' prints[df={dfIds}] [text={textSet} ddsOk={ddsOk} killer={killerClue}]";
            recList.Add(rec);
            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: INJECTED {rec}");
            try { DebugTools.AddClueHud($"{label} — {where}: {locDesc}"); } catch { }
        }

        private static string ClassifyLocation(Human victim, string locName)
        {
            try
            {
                string hn = null; try { if (victim.home != null) hn = victim.home.name; } catch { }
                string wn = null; try { if (victim.job != null && victim.job.employer != null && victim.job.employer.placeOfBusiness != null) wn = victim.job.employer.placeOfBusiness.name; } catch { }
                if (hn != null && locName == hn) return "HOME";
                if (wn != null && locName == wn) return "WORK";
            }
            catch { }
            return "elsewhere";
        }

        // A per-case key identifying the room a note landed in (location name + room name), so we
        // place at most one clue per room. Null if the room can't be resolved (then we don't dedup).
        private static string RoomKey(Interactable note)
        {
            try
            {
                var node = note.node;
                if (node == null) return null;
                string gl = null; try { gl = node.gameLocation != null ? node.gameLocation.name : null; } catch { }
                string rn = null; try { rn = node.room != null ? node.room.GetName() : null; } catch { }
                if (string.IsNullOrEmpty(rn)) return null;
                return (gl ?? "?") + "|" + rn;
            }
            catch { return null; }
        }

        private static void LogFail(string what, Human writer, Human receiver)
            => MotivesPlugin.Log.LogInfo($"[SODMotives] clue: PlaceObject failed for {what} {MotivesPlugin.Name(writer)}->{MotivesPlugin.Name(receiver)}.");

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

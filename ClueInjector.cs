using System;
using System.Collections.Generic;

namespace SODMotives
{
    // Injects ONE discoverable, deliberately-ambiguous physical note per motivated
    // case, wired (writer/owner) to the killer & victim so it appears in the
    // evidence UI. Email/phone-log injection has no clean API in SoD, so we use
    // readable notes/letters (the same item type the player already finds).
    internal static class ClueInjector
    {
        internal static bool Enable = true;
        internal static int MaxClues = 3;   // notes injected per case (killer + red herrings)
        internal static bool ObviousNames = true;  // TESTING: rename injected notes so they're easy to spot
        internal static float FingerprintChance = 0.7f; // chance a note carries the author's prints (else handwriting-only)

        private static readonly Random _rng = new Random();

        private static InteractablePreset _notePreset;
        private static bool _scanned;

        // For F9 overlay + de-dup.
        internal static readonly Dictionary<int, List<string>> CluesByVictim = new Dictionary<int, List<string>>();
        private static readonly HashSet<int> _injected = new HashSet<int>();

        private static void EnsureNotePreset()
        {
            if (_scanned) return;
            _scanned = true;
            try
            {
                var tb = Toolbox.Instance;
                if (tb == null) return;

                var candidates = new List<InteractablePreset>();
                AddReadable(tb.placePerOwnerInteractables, candidates);
                AddReadable(tb.placeAtGameLocationInteractables, candidates);

                MotivesPlugin.Log.LogInfo($"[SODMotives] clue: {candidates.Count} readable placeable presets found:");
                InteractablePreset best = null; int bestScore = -1;
                foreach (var p in candidates)
                {
                    string nm = SafeName(p);
                    string src = "?";
                    try { src = p.readingSource.ToString(); } catch { }
                    string low = nm.ToLowerInvariant();
                    int s = 0;
                    if (src == "evidenceNote") s += 4; else if (src == "mainEvidenceText") s += 3; else if (src == "multipageEvidence") s += 2;
                    if (low.Contains("note")) s += 4; else if (low.Contains("letter")) s += 3; else if (low.Contains("memo") || low.Contains("paper") || low.Contains("postit")) s += 2;
                    MotivesPlugin.Log.LogInfo($"[SODMotives] clue:    '{nm}' (readingSource={src}) score={s}");
                    if (s > bestScore) { bestScore = s; best = p; }
                }
                _notePreset = best;
                MotivesPlugin.Log.LogInfo(_notePreset != null
                    ? $"[SODMotives] clue: chosen note preset = '{SafeName(_notePreset)}'"
                    : "[SODMotives] clue: NO readable note preset found (injection disabled).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: preset scan error: {e}"); }
        }

        private static void AddReadable(Il2CppSystem.Collections.Generic.List<InteractablePreset> list, List<InteractablePreset> outList)
        {
            if (list == null) return;
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p == null) continue;
                    bool reading = false;
                    try { reading = p.readingEnabled; } catch { }
                    if (reading) outList.Add(p);
                }
            }
            catch { }
        }

        internal static void InjectForCase(MurderController.Murder murder)
        {
            if (!Enable || murder == null) return;
            Human victim = murder.victim, killer = murder.murderer;
            if (victim == null || killer == null) return;
            if (_injected.Contains(victim.humanID)) return;   // once per case
            _injected.Add(victim.humanID);

            try
            {
                // Prefer a REAL vanilla lead's spawn config (proven-valid item + itemTag +
                // security + ownership rule for this murder); fall back to a scanned preset.
                SpawnCfg cfg = GetLeadTemplate(murder);
                if (!cfg.valid)
                {
                    EnsureNotePreset();
                    if (_notePreset == null) { MotivesPlugin.Log.LogInfo("[SODMotives] clue: no usable note preset; skipping."); return; }
                    cfg = new SpawnCfg { preset = _notePreset, security = 0, rule = InteractablePreset.OwnedPlacementRule.both, priority = 5, tag = default(JobPreset.JobTag), templateWhere = MurderPreset.LeadSpawnWhere.victimHome, valid = true };
                }

                try
                {
                    string vHome = victim.home != null ? victim.home.name : "<null>";
                    string kHome = killer.home != null ? killer.home.name : "<null>";
                    MotivesPlugin.Log.LogInfo($"[SODMotives] clue: inject start state={murder.state} victimHome={vHome} killerHome={kHome} cfgItem='{SafeName(cfg.preset)}' tag={cfg.tag} where={cfg.templateWhere}");
                }
                catch { }

                // Build the victim's motivated-suspect shortlist, killer always included.
                var suspects = new List<(Human who, MotiveResult mot)>();
                var seen = new HashSet<int>();
                MotiveResult killerMot;
                if (!MurderSelector.MotiveByVictim.TryGetValue(victim.humanID, out killerMot))
                    killerMot = Motive.Score(killer, victim);
                suspects.Add((killer, killerMot));
                seen.Add(killer.humanID);

                var city = CityData.Instance;
                if (city != null && city.citizenDirectory != null)
                {
                    var cits = city.citizenDirectory;
                    var others = new List<(Human who, MotiveResult mot)>();
                    for (int i = 0; i < cits.Count; i++)
                    {
                        Human a = cits[i];
                        if (a == null || seen.Contains(a.humanID) || Motive.Same(a, victim)) continue;
                        var m = Motive.Score(a, victim);
                        if (m.HasMotive) others.Add((a, m));
                    }
                    others.Sort((x, y) => y.mot.score.CompareTo(x.mot.score));
                    for (int i = 0; i < others.Count && suspects.Count < MaxClues; i++)
                    {
                        if (seen.Add(others[i].who.humanID)) suspects.Add(others[i]);
                    }
                }

                // Inject one note per selected suspect. Killer's note is
                // indistinguishable from the red herrings by content.
                if (!CluesByVictim.TryGetValue(victim.humanID, out var recList)) { recList = new List<string>(); CluesByVictim[victim.humanID] = recList; }

                // The game keys murder items by JobTag (Murder.activeMurderItems), so
                // spawning several with the SAME tag collapses them onto ONE carrier
                // (last writer wins -> killer clue overwritten, decoy text landing on a
                // pre-existing vanilla multipage doc). Give each clue a DISTINCT, unused
                // tag and verify each spawn is a fresh, distinct interactable.
                var usedTags = CollectUsedTags(murder);
                var usedIds = new HashSet<int>();
                int idx = 0, placed = 0;
                foreach (var s in suspects)
                {
                    GetClue(s.mot.type, idx, out string treeId, out string treeName, out MurderPreset.LeadSpawnWhere where);

                    if (!AllocTag(usedTags, out JobPreset.JobTag clueTag))
                    {
                        MotivesPlugin.Log.LogInfo("[SODMotives] clue: no free JobTag left; stopping clue injection for this case.");
                        break;
                    }

                    // Try the preferred location, then fall back; reject any carrier we've
                    // already used this case (identical-item collision) and try the next.
                    Interactable clue = null;
                    MurderPreset.LeadSpawnWhere usedWhere = where;
                    foreach (var w in PlacementOrder(where, cfg.templateWhere))
                    {
                        var cand = TrySpawn(murder, cfg, w, clueTag);
                        if (cand == null) continue;
                        int cid = -1; try { cid = cand.id; } catch { }
                        if (cid >= 0 && usedIds.Contains(cid))
                        {
                            MotivesPlugin.Log.LogInfo($"[SODMotives] clue: tag={clueTag} at {w} returned already-used item id={cid}; trying next placement.");
                            continue;
                        }
                        clue = cand; usedWhere = w; break;
                    }
                    where = usedWhere;

                    if (clue == null)
                    {
                        MotivesPlugin.Log.LogInfo($"[SODMotives] clue: SpawnItem null/duplicate (all placements) for {MotivesPlugin.Name(s.who)}.");
                        idx++;
                        continue;
                    }
                    try { usedIds.Add(clue.id); } catch { }

                    bool textSet = false;
                    try
                    {
                        // The reading page is rendered LIVE from the tree each open (no cache),
                        // so overriding to a document-type tree is all that's needed.
                        clue.SetWriter(s.who);           // attribute to THIS suspect
                        clue.SetDDSOverride(treeId);      // sets Interactable.dds -> page + tooltip
                        var ev = clue.evidence;
                        if (ev != null)
                        {
                            ev.SetOverrideDDS(treeId);    // case-file summary text
                            ev.SetWriter(s.who);
                        }
                        // Sometimes stamp the author's fingerprints (traceable by prints as
                        // well as handwriting); sometimes not, so it isn't formulaic.
                        if (_rng.NextDouble() < FingerprintChance)
                        {
                            try { clue.AddNewDynamicFingerprint(s.who, Interactable.PrintLife.manualRemoval); }
                            catch (Exception e4) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: print add: {e4.Message}"); }
                        }
                        // TESTING: make the note obvious in the evidence UI.
                        if (ObviousNames && ev != null)
                        {
                            try { ev.AddOrSetCustomName(Evidence.DataKey.name, $"MODCLUE {MotivesPlugin.Name(s.who)} ({s.mot.type})"); } catch { }
                            try { clue.UpdateName(true, Evidence.DataKey.name); } catch { clue.UpdateName(); }
                        }
                        else clue.UpdateName();
                        textSet = true;
                    }
                    catch (Exception e2) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: set-text error: {e2.Message}"); }

                    // Where exactly did it land (room + address + precise spot) so overlaps
                    // are visible and it's findable via F9.
                    string locDesc = "?", posDesc = "";
                    try
                    {
                        var node = clue.node;
                        if (node != null)
                        {
                            string loc = node.gameLocation != null ? node.gameLocation.name : "?";
                            string room = ""; try { if (node.room != null) room = node.room.GetName(); } catch { }
                            locDesc = string.IsNullOrEmpty(room) ? loc : $"{room}, {loc}";
                            try { var p = node.position; posDesc = $" @({p.x:0.0},{p.y:0.0},{p.z:0.0})"; } catch { }
                        }
                    }
                    catch { }

                    // Verify the override actually landed on OUR fresh item (guards the
                    // "decoy text on a vanilla multipage doc" collision that started this).
                    int clueId = -1; string presetNm = "?", ddsNow = "?";
                    try { clueId = clue.id; } catch { }
                    try { presetNm = clue.preset != null ? clue.preset.name : "<null>"; } catch { }
                    try { ddsNow = clue.dds; } catch { }
                    bool ddsOk = ddsNow == treeId;

                    bool isKiller = s.who.humanID == killer.humanID;
                    string rec = $"{(isKiller ? "[KILLER] " : "[decoy]  ")}from {MotivesPlugin.Name(s.who)} @ {where} [{locDesc}]{posDesc} ({s.mot.type}) dds='{treeName}' tag={clueTag} id={clueId} preset='{presetNm}' [text={textSet} ddsOk={ddsOk}]";
                    recList.Add(rec);
                    MotivesPlugin.Log.LogInfo($"[SODMotives] clue: INJECTED {rec}");
                    idx++; placed++;
                }
                MotivesPlugin.Log.LogInfo($"[SODMotives] clue: placed {placed} note(s) for victim {MotivesPlugin.Name(victim)} ({suspects.Count} suspects considered).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: inject error: {e}"); }
        }

        // Map each motive to EXISTING game DDS trees whose letter text fits (id, name).
        // Applied via SetDDSOverride so the note reads as real, motive-appropriate mail.
        // ALL verified treeType==document with loose participants (render reliably on
        // a physical note). vmail-type trees (Personal_Warning, Work_Stealing, ...) show
        // blank pages, so they are deliberately NOT used here.
        private static readonly (string id, string name)[] InfidelityTrees = {
            ("9b6d2119-f1a0-4b47-880d-1ee6fb417716", "Cheaters_Letter"),
            ("e6cfbb79-cc0b-4848-b699-280cbe261c3e", "Cheaters_Love_Poem"),
            ("7b3abe1a-30dd-41b1-b0ec-8fe6e739d2a2", "Flower_Note_Affair"),
        };
        private static readonly (string id, string name)[] ProfessionalTrees = {
            ("0eaf0e5e-87a7-4c32-b6aa-333e1a3f8157", "Job_Humiliation"),
            ("0b32451b-a6d3-40a3-ba1d-5b645e251bdd", "Dodgy_NoteRat"),
            ("0a2dc736-69bd-46f3-b17c-75714e47c680", "Murder_Half_writen_letter"),
        };
        private static readonly (string id, string name)[] MoneyTrees = {
            ("de8d1d15-25a6-4f24-ae06-172f631fa589", "DebtCollectionLetter"),
            ("ea869950-7590-4ca2-b472-9811dd583e57", "BillFinalNotice"),
            ("0b32451b-a6d3-40a3-ba1d-5b645e251bdd", "Dodgy_NoteRat"),
        };
        private static readonly (string id, string name)[] FeudTrees = {
            ("0b32451b-a6d3-40a3-ba1d-5b645e251bdd", "Dodgy_NoteRat"),
            ("0a2dc736-69bd-46f3-b17c-75714e47c680", "Murder_Half_writen_letter"),
            ("250ed862-9f19-4ed4-83fb-3f1f61209887", "Ev_StickyNote"),
        };

        private static void GetClue(MotiveType type, int idx, out string treeId, out string treeName, out MurderPreset.LeadSpawnWhere where)
        {
            (string id, string name)[] pool;
            switch (type)
            {
                case MotiveType.Infidelity: pool = InfidelityTrees; where = MurderPreset.LeadSpawnWhere.victimHome; break;
                case MotiveType.Professional: pool = ProfessionalTrees; where = MurderPreset.LeadSpawnWhere.victimWork; break;
                case MotiveType.Money: pool = MoneyTrees; where = MurderPreset.LeadSpawnWhere.victimHome; break;
                default: pool = FeudTrees; where = MurderPreset.LeadSpawnWhere.victimHome; break;
            }
            var pick = pool[idx % pool.Length];
            treeId = pick.id; treeName = pick.name;
        }

        private struct SpawnCfg
        {
            public InteractablePreset preset;
            public int security;
            public InteractablePreset.OwnedPlacementRule rule;
            public int priority;
            public JobPreset.JobTag tag;
            public MurderPreset.LeadSpawnWhere templateWhere;
            public bool valid;
        }

        // Find a readable-note lead in THIS murder's preset and reuse its proven-valid
        // spawn parameters (item preset, itemTag, security, ownership rule). Also logs
        // every vanilla lead so we can see valid combinations.
        private static SpawnCfg GetLeadTemplate(MurderController.Murder murder)
        {
            var result = new SpawnCfg { valid = false };
            try
            {
                var preset = murder.preset;
                if (preset == null || preset.leads == null) return result;
                var leads = preset.leads;
                int bestScore = -1;
                for (int i = 0; i < leads.Count; i++)
                {
                    var lead = leads[i];
                    if (lead == null) continue;
                    InteractablePreset sp = null; try { sp = lead.spawnItem; } catch { }
                    bool reading = false; string src = "?";
                    if (sp != null) { try { reading = sp.readingEnabled; src = sp.readingSource.ToString(); } catch { } }
                    string nm = sp != null ? SafeName(sp) : "<null>";
                    string wh = "?", tg = "?";
                    try { wh = lead.where.ToString(); } catch { }
                    try { tg = lead.itemTag.ToString(); } catch { }
                    MotivesPlugin.Log.LogInfo($"[SODMotives] clue: vanilla lead item='{nm}' reading={reading} src={src} where={wh} tag={tg}");
                    if (sp == null || !reading) continue;
                    int s = 0; string low = nm.ToLowerInvariant();
                    if (src == "evidenceNote") s += 4; else if (src == "mainEvidenceText") s += 3; else if (src == "multipageEvidence") s += 1;
                    if (low.Contains("note")) s += 3; else if (low.Contains("letter")) s += 2;
                    if (s > bestScore)
                    {
                        bestScore = s;
                        result = new SpawnCfg
                        {
                            preset = sp,
                            security = SafeInt(() => lead.security, 0),
                            rule = lead.ownershipRule,
                            priority = SafeInt(() => lead.priority, 5),
                            tag = lead.itemTag,
                            templateWhere = lead.where,
                            valid = true
                        };
                    }
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: lead template error: {e.Message}"); }
            return result;
        }

        private static int SafeInt(Func<int> f, int dflt) { try { return f(); } catch { return dflt; } }

        private static Interactable TrySpawn(MurderController.Murder murder, SpawnCfg cfg, MurderPreset.LeadSpawnWhere where, JobPreset.JobTag tag)
        {
            try
            {
                return MurderController.Instance.SpawnItem(
                    murder, cfg.preset, where,
                    MurderPreset.LeadCitizen.victim,   // belongsTo
                    MurderPreset.LeadCitizen.victim,   // writer placeholder (overridden later)
                    MurderPreset.LeadCitizen.victim,   // receiver
                    cfg.security, cfg.rule, cfg.priority, tag);
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] clue: SpawnItem threw at {where} tag={tag}: {e.Message}"); return null; }
        }

        // JobTags already claimed by this murder's real leads — so our clues never collide
        // with a vanilla item's tag slot (Murder.activeMurderItems is tag-keyed).
        private static HashSet<int> CollectUsedTags(MurderController.Murder murder)
        {
            var used = new HashSet<int>();
            try
            {
                var preset = murder.preset;
                if (preset != null && preset.leads != null)
                {
                    var leads = preset.leads;
                    for (int i = 0; i < leads.Count; i++)
                    {
                        var lead = leads[i];
                        if (lead == null) continue;
                        try { used.Add((int)lead.itemTag); } catch { }
                    }
                }
            }
            catch { }
            return used;
        }

        // Claim the next unused JobTag (A..Z = 0..25). Marks it used so each clue is distinct.
        private static bool AllocTag(HashSet<int> used, out JobPreset.JobTag tag)
        {
            for (int i = 0; i < 26; i++)
            {
                if (used.Add(i)) { tag = (JobPreset.JobTag)i; return true; }
            }
            tag = default(JobPreset.JobTag);
            return false;
        }

        // Preferred location first, then the vanilla template's location, then fallbacks.
        private static List<MurderPreset.LeadSpawnWhere> PlacementOrder(MurderPreset.LeadSpawnWhere first, MurderPreset.LeadSpawnWhere template)
        {
            var order = new List<MurderPreset.LeadSpawnWhere>();
            void Add(MurderPreset.LeadSpawnWhere w) { if (!order.Contains(w)) order.Add(w); }
            Add(first);
            Add(template);
            Add(MurderPreset.LeadSpawnWhere.victimHome);
            Add(MurderPreset.LeadSpawnWhere.killerHome);
            Add(MurderPreset.LeadSpawnWhere.victimWork);
            return order;
        }

        private static string SafeName(InteractablePreset p)
        {
            try { return p != null ? p.name : "<null>"; } catch { return "?"; }
        }
    }
}

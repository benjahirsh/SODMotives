using System;
using System.Collections.Generic;
using UnityEngine;

namespace SODMotives
{
    // Recovery for the rare case where one of OUR overridden motive-murders soft-locks in the
    // 'executing' state: the killer reaches the victim but never lands a lethal blow. Observed in
    // testing as the killer swinging/whiffing with no damage (razor blade held wrong, "punching"
    // rather than stabbing), the victim fleeing room to room, indefinitely — the murder state
    // machine has no 'failed' state, so it never resolves on its own (confirmed stuck ~5 in-game
    // days). Root cause is a vanilla enactment weakness that our pair-forcing exposes (we bypass
    // vanilla's own capability-weighted killer selection); we can't repair vanilla's enact, so we
    // recover from it.
    //
    // SCOPE — deliberately narrow, so we NEVER fabricate an incoherent murder:
    //   * ONLY our overridden cases (MurderSelector.OverriddenVictimIds) — vanilla murders never
    //     enter this code (they aren't in that set). We add NO Harmony patches to vanilla's
    //     enactment / combat / selection; this is a poll that reads state and, only when safe,
    //     lands the blow the AI failed to.
    //   * ONLY when the killer is CO-LOCATED with the victim (same game-location / room) — so the
    //     forensic trail is real: the killer genuinely travelled there (CCTV en route, proximity,
    //     the alibi break all already happened) and landing the blow drops spatter at a scene the
    //     killer is standing in. If the killer can't reach the victim (a DIFFERENT stall), we do
    //     nothing but LOG it — never an evidence-less body across town.
    //   * ONLY when the victim's health isn't dropping (a genuine whiff-stall, not a slow-but-working
    //     kill we'd be cutting short).
    // On intervention we land the killing blow via Actor.RecieveDamage(enableKill:true) so vanilla's
    // own death processing runs, then force the case to 'post' (which triggers vanilla's evidence
    // spawn + our motive-clue injection) if it doesn't advance itself. EVERY intervention is logged.
    internal static class MurderWatchdog
    {
        internal static bool Enable = true;
        // In-game hours (SessionData.gameTime units) stuck in 'executing' before we step in. The
        // co-location + full-health gates already guarantee it's a true stall, so this is just a
        // generous margin so a legitimately slow enact (which completes in game-minutes) is never cut.
        internal static float StallGameHours = 24f;
        // In-game hours stuck in 'waitForLocation' before we cancel an overridden case. Unlike 'executing',
        // waitForLocation has NO natural resolution when the game can't seat a location (a kidnap with no
        // valid holding den, a sniper with no vantage site), so such a case hangs forever. We cancel it so
        // the player is never soft-locked. Kept modest since the stall is permanent once it happens.
        internal static float WaitLocationStallHours = 12f;

        private static int _watchVictimId = -1;        // the overridden victim currently in 'executing'
        private static float _executingSince = 0f;     // gameTime it entered 'executing'
        private static readonly HashSet<int> _intervened = new HashSet<int>();     // victims we've already force-finished (once-guard)
        private static readonly HashSet<int> _loggedNoReach = new HashSet<int>();  // stalls we logged but chose not to act on (once each)
        private static int _awaitingPost = -1;         // a victim we've killed, waiting to flip the case to 'post'

        private static int _waitLocVictimId = -1;      // the overridden victim currently in 'waitForLocation'
        private static float _waitLocSince = 0f;       // gameTime it entered 'waitForLocation'
        private static readonly HashSet<int> _probed = new HashSet<int>();         // cases we've run the location probe on (once each)
        private static readonly HashSet<int> _waitRecovered = new HashSet<int>();  // cases we've cancelled out of a waitForLocation hang
        private static readonly HashSet<int> _observed = new HashSet<int>();       // kidnaps (any, incl. vanilla) we've logged an observation for

        // Live position sampler (diagnostic, dev-only): throttled per-victim while a kidnap is active.
        private static int _liveVid = -1;               // victim currently being sampled
        private static float _liveLastLogH = -999f;     // gameTime (hours) of the last live sample
        private static int _liveCount = 0;              // samples logged for this victim (capped so it can't spam forever)

        // Sniper diagnostic sampler (dev-only): observe a sniper case (forced or VANILLA) to learn the intended
        // vantage / target-site / shot flow, and to diagnose why a motivated sniper loops in travellingTo instead
        // of firing. Separate throttle so it doesn't fight the kidnap sampler. All gated behind EnableDebugKeys.
        private static readonly HashSet<int> _sniperObserved = new HashSet<int>();  // sniper cases we've logged the one-shot observation for
        private static int _sniperLiveVid = -1;
        private static float _sniperLiveLastLogH = -999f;
        private static int _sniperLiveCount = 0;

        // SNIPER SEEDING: our pair-swap enters the state machine with sniperVictimSite == null, which strands the
        // case (the game locks the victim's home, finds no vantage, and never re-targets). Vanilla always enters
        // with a solver-chosen site (PickNewVictim). So the watchdog seeds one once, via the game's OWN picker
        // (Murder.TryPickNewVictimSite), then defers the shot AND the force-kill fallback to the game exactly like
        // vanilla. No patience/timeout: vanilla has none for an un-executed sniper.
        private static readonly HashSet<int> _sniperSeeded = new HashSet<int>();   // our sniper cases we've made the initial site decision for (once each)
        // LOCAL-SITE PIN (ExCop variety): a believable local site (victim home/work) we pinned instead of the city's
        // single best rooftop, so the kill lands somewhere personal to the pair (the "Daffodil Ward" pattern). Local
        // sites score far below the global best, so the game would never pick them on its own; a set sniperVictimSite
        // is honoured, so pinning holds. If a (solver false-positive) pin never fires, we RELEASE to the game's own
        // default after SniperPinStallHours so it can't hang.
        internal static float SniperPinStallHours = 8f;
        // If a pinned case is still in waitForLocation after this many in-game hours, the VICTIM never reached the
        // pinned site (e.g. a workplace they can't enter off-shift, or a node the herd can't path into) -- the case
        // would otherwise sit frozen until SniperPinStallHours. Release to the game default EARLY instead. Much shorter
        // than SniperPinStallHours because a reachable site is reached in minutes; hours of waitForLocation = unreachable.
        internal static float SniperVictimReachHours = 3f;
        // Max distance (metres) a pinned local nest may be from its site. The vantage solver over-reports: it will
        // return the city's dominant rooftop as a "vantage" over a site two blocks away (a nonsensical cross-city
        // shot that never lines up). A real overlooking nest is across a street, so we reject nests farther than this.
        internal static float SniperMaxNestMeters = 55f;
        // How far (metres, building centre to the site) to look for candidate nest buildings. Wider than SniperMaxNestMeters
        // because a large building's centre can be far while its near-side window is close; the per-nest distance is still
        // gated by SniperMaxNestMeters. Lets us catch an overlook on ANY side of the site, not just a directly-faced one.
        internal static float SniperNestSearchMeters = 110f;
        private static readonly Dictionary<int, NewGameLocation> _sniperPin = new Dictionary<int, NewGameLocation>();   // victimId -> pinned local site
        private static readonly Dictionary<int, float> _sniperPinSince = new Dictionary<int, float>();                  // victimId -> gameTime the pin was set
        private static readonly HashSet<int> _sniperHerdLogged = new HashSet<int>();                                    // victims we've logged the herd for (once each)
        private static readonly Dictionary<int, List<NewNode>> _sniperWander = new Dictionary<int, List<NewNode>>();      // victimId -> the site nodes the pinned nest can see (victim wanders among them, in the sightline)
        private static readonly Dictionary<int, System.IntPtr> _sniperHerdNode = new Dictionary<int, System.IntPtr>();   // victimId -> the wander node currently herded to (to detect when to re-issue the walk goal)
        private static readonly Dictionary<int, KeyValuePair<int, NewNode>> _sniperWanderPick = new Dictionary<int, KeyValuePair<int, NewNode>>();   // victimId -> (time bucket, randomly-chosen wander node for that bucket)
        private static readonly System.Random _sniperRng = new System.Random();

        // FORCE-LOS-NEST (window fix). Decoded from IL2CPP (docs/extensions/sniper-nest-decode.md): the killer's nest
        // is NOT a settable field — it is re-derived every time his sniper AI action activates by calling
        // Toolbox.TryGetSniperVantagePoint, whose out NewWall becomes the node he walks to and fires from. The solver
        // scores windows with a RANDOM term, so it can hand the killer a wrong-facing window with no real line of
        // sight (the reported "killer at the Lovelace 1st-floor window, victim across the street, never fires"). We fix
        // that at the one choke point: a Harmony postfix on that solver (Plugin.cs) replaces its pick, for OUR active
        // motivated sniper only, with a window WE verified has clear node-graph LOS to where the victim will stand,
        // preferring the closest such window. Everything else (site, herd, shot) is unchanged.
        internal static bool SniperForceLosNest = true;    // [Troubleshooting] toggle so the fix can be A/B'd
        internal static bool _inNestScan = false;          // reentrancy guard: our own solver calls must not re-enter the postfix
        private static Human _moSniperKiller, _moSniperVictim;   // the active motivated sniper pair (scopes the postfix to ours only)
        private static NewWall _moNestWall;                 // cached chosen nest for the current site (null = "checked, nothing better than the game's pick")
        private static System.IntPtr _moNestSitePtr = System.IntPtr.Zero;   // site the cache is for; recompute when the site changes (re-target/clobber)

        // PHYSICS-LOS NEST RESCUE (docs/extensions/sniper-nest-decode.md pt.4). The node-graph vantage check above
        // (DataRaycastController.NodeRaycast) only registers windows a site DIRECTLY faces; it is BLIND to a real
        // overlook across a park / on another building side / a few floors up (the Lovelace-8th-over-Laster's case:
        // every node-graph candidate scored cover=0, so the case fell to Mingo). The live fire gate is a
        // UnityEngine.Physics ray, so when the node-graph pass finds NO nest we fall back to a physics raycast
        // (window-opening to window-opening, mirroring the gate) over nearby self-enumerated ACCESSIBLE windows, and
        // pin the one that physically overlooks the MOST of the site's own windows (broadest overlook, e.g. a Plaza
        // Orchid landing that sees many ward windows beats a single-window Lovelace landing). Additive +
        // false-negative-biased: runs only when Stage 1 found nothing, so working cases are byte-for-byte unchanged
        // and cast zero rays. Behind a toggle so it can be A/B'd or shipped off. All interop members are probe-verified
        // against BepInEx/interop (scratch reflection probe), and every call is try/catch'd.
        internal static bool SniperPhysicsLosNest = true;   // [Troubleshooting] toggle
        private const float PhysEyeUp = 1.4f;               // gun/eye height above a nest node (~killer.transform.position + aim)
        private const float PhysBodyUp = 1.1f;              // victim torso height above a stand node (~GetBodyAnchor)
        private const float PhysEdge = 0.35f;               // pull ray ends in from each window opening so we don't hit the opening's own frame
        private const int   PhysMaxNestWindows = 90;        // cap on self-enumerated candidate nest windows per (failing) case
        private const int   PhysMaxSiteWindows = 24;        // cap on the site windows we aim at
        private static int  _rainGlassLayer = int.MinValue; // RainWindowGlass layer index (int.MinValue = unresolved, -1 = absent)
        private static bool _physLayersDumped = false;      // one-time layer/mask diagnostic guard
        private static bool _lastNestWasPhysics = false;    // FindBestPublicNest: did the LAST pick come from the physics rescue?
        private static bool _moNestPhys = false;            // the active pinned nest came from the physics rescue (enables arrival re-validation)
        private static bool _moNestRevalidated = false;     // one-shot: have we re-validated the physics pin once the killer reached it?

        // Seal the den behind the fleeing killer. Vanilla (confirmed in-game across 2 sandboxes: frontDoors
        // locked=0 the whole hold, AND a web-search confirmed vacant-address kidnap dens stay UNLOCKED) only
        // CLOSES the doors (default NPC flee behaviour), never locks the unowned den. So we CLOSE to match that
        // reliably (our swapped killer sometimes left them open) and DON'T lock — default OFF. The optional lock
        // (a "vanilla+" sealed hold) is left as a toggle but off by default, matching vanilla.
        internal static bool KidnapLockDen = false;
        private static readonly HashSet<int> _denSealed = new HashSet<int>();
        private static readonly HashSet<int> _denGoalLogged = new HashSet<int>();  // victims we've logged rebuilding the GoTo-den goal for (save/load fix, once each)

        // Kidnap RANSOM-LEAD assist. Our victim-swap bypasses the vanilla kidnap Case/objective chain, so the
        // ransom note is never delivered + no "Examine the ransom note found at <home>" objective ever appears
        // (that objective IS the "investigate the missing person's home" lead). We spawn the note ourselves and
        // register it so the game's own objective chain proceeds (see SpawnRansomNote).
        internal static bool KidnapRansomAssist = true;
        private static readonly HashSet<int> _ransomTried = new HashSet<int>();  // victims we've spawned the ransom note for (once each)

        // Kidnap KILL-DEADLINE fix. Our victim-swap leaves the kidnap's kill timers expired, so the game flips
        // kidnapKillPhase on immediately and tries to kill the held victim before any fair deadline. We keep the
        // kill gated on the game's OWN Murder.killTime (the value the ransom note counts down to), clearing the
        // premature kidnapKillPhase and blocking the kill's state transitions until then. See ShouldBlockKidnapKill.
        private static readonly HashSet<int> _killTimeReasserted = new HashSet<int>();  // victims where we've logged holding the kidnapper off
        private static readonly HashSet<int> _killBlockLogged = new HashSet<int>();  // victims where we've logged blocking the premature kill
        internal static readonly HashSet<int> KidnapReachedHold = new HashSet<int>();  // our kidnap victims whose case has gone to the hold (post/escaping/unsolved) — a later travellingTo/executing is the KILL

        internal static void ResetForNewGame()
        {
            _watchVictimId = -1; _executingSince = 0f;
            _intervened.Clear(); _loggedNoReach.Clear(); _awaitingPost = -1;
            _waitLocVictimId = -1; _waitLocSince = 0f; _probed.Clear(); _waitRecovered.Clear();
            _observed.Clear();
            _liveVid = -1; _liveLastLogH = -999f; _liveCount = 0;
            _sniperObserved.Clear(); _sniperLiveVid = -1; _sniperLiveLastLogH = -999f; _sniperLiveCount = 0;
            _sniperSeeded.Clear(); _sniperPin.Clear(); _sniperPinSince.Clear(); _sniperHerdLogged.Clear(); _sniperWander.Clear(); _sniperHerdNode.Clear(); _sniperWanderPick.Clear();
            _physLayersDumped = false; _rainGlassLayer = int.MinValue; _lastNestWasPhysics = false; _moNestPhys = false; _moNestRevalidated = false;
            ClearActiveMotivatedSniper();
            _ransomTried.Clear(); _killTimeReasserted.Clear();
            _killBlockLogged.Clear(); KidnapReachedHold.Clear(); _denSealed.Clear(); _denGoalLogged.Clear();
        }

        // BLOCK THE PREMATURE KILL. Decoded (ISIL) + confirmed in-game: our victim-swap leaves the kidnap's kill
        // timers expired, so the game enters the KILL sequence (kidnapKillPhase set true, then SetMurderState
        // travellingTo -> executing to have the killer travel to the victim and deliver the lethal blow) almost
        // immediately when the case goes live — long before any fair deadline (pinning killTime did NOT stop it;
        // the kill fires via other expired timers). Rather than chase each timer, we block the kill's state
        // transitions while kidnapKillPhase is active AND we're before our deadline; the victim stays held +
        // rescuable until then. Once our deadline passes we stop blocking, so a real (missable) deadline remains.
        // Called from the SetMurderState PREFIX. Returns true = block this transition.
        internal static bool ShouldBlockKidnapKill(MurderController.Murder m, MurderController.MurderState newState)
        {
            try
            {
                if (newState != MurderController.MurderState.travellingTo && newState != MurderController.MurderState.executing) return false;
                if (m == null || m.preset == null || m.preset.caseType != MurderPreset.CaseType.kidnap) return false;
                var v = m.victim; if (v == null) return false;
                int vid = v.humanID;
                if (!MurderSelector.OverriddenVictimIds.Contains(vid)) return false;
                // Only the KILL re-enters travellingTo/executing AFTER the case has gone to the hold; the ABDUCTION's
                // own travellingTo/executing happen before that (KidnapReachedHold not yet set) and must run.
                if (!KidnapReachedHold.Contains(vid)) return false;
                // Deadline = the game's OWN killTime (what the ransom note shows), so the kill matches the note.
                // Block until it's reached; if killTime isn't set yet (~0), keep blocking so the game can't kill
                // before the deadline is even established.
                float kt = 0f; try { kt = m.killTime; } catch { }
                if (kt > 0.5f && NowHours() >= kt) return false;   // deadline reached — let the kidnapper kill
                if (_killBlockLogged.Add(vid))
                    MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap-kill] BLOCKING the kidnapper's kill (state=>{newState}) until the game's killTime={kt:0.0} (now={NowHours():0.0}); victim held + rescuable until then.");
                return true;
            }
            catch { return false; }
        }

        // A believable LOCAL sniper site for an ExCop case: a STATIC location the victim reliably occupies -- their
        // HOME first, then their WORKPLACE -- with a PUBLIC, accessible nest (a rooftop/window in the building opposite)
        // that has a genuine, raycast-verified line of sight to where the victim actually stands there. This mirrors
        // vanilla's "pin the victim to a location, then send the shooter to solve it" (reliable, because the victim is
        // stationary), rather than a hard-to-line-up shot at a moving commuter. We compute the nest OURSELVES
        // (FindBestPublicNest) by enumerating the buildings facing the location and scoring their windows by real LOS to
        // the victim's node, because the game's own solver only ever returns its single global-best rooftop -- which is
        // exactly why ExCop cases kept funnelling to the one dominant street. Returns the site to pin AND the chosen
        // nest wall; null if no local public nest overlooks the home or workplace (-> caller falls to the game default).
        private static NewGameLocation FirstViableLocalSite(Human killer, Human victim, out NewWall nestWall, out List<NewNode> herdNodes)
        {
            nestWall = null; herdNodes = null;
            try
            {
                if (killer == null || victim == null) return null;
                NewGameLocation work = null; try { var j = victim.job; var e = j != null ? j.employer : null; if (e != null) work = e.placeOfBusiness; } catch { }

                // WORKPLACE only. We deliberately do NOT pin the HOME: it is an enclosed residence (poor sniper LOS) and
                // pinning it strands the victim inside their flat while the case falls back to the game default rooftop
                // (the reported "victim stuck at home, killer waiting at Mingo"). An exposed workplace (a ward, an open
                // office) is the believable, snipeable static spot; if it has no nest we fall to the game default.
                var order = new List<NewGameLocation>();
                if (work != null) order.Add(work);

                foreach (var loc in order)
                {
                    NewNode target = TargetNodeAt(victim, loc);
                    if (target == null) continue;
                    if (FindBestPublicNest(killer, loc, target, out var wall, out var dist, out var isPublic, out var seen) && wall != null)
                    {
                        nestWall = wall; herdNodes = seen;
                        NewGameLocation nestLoc = null; try { var bn = wall.node; nestLoc = bn != null ? bn.gameLocation : null; } catch { }
                        MotivesPlugin.Log.LogInfo($"[SODMotives][sniper] LOCAL nest for {LName(loc)}: {LName(nestLoc)} ({dist:0}m, {(isPublic ? "public/accessible" : "killer-home window")}, verified line of sight) -- herd the victim to the window the nest sees, killer solves it.");
                        return loc;
                    }
                    MotivesPlugin.Log.LogInfo($"[SODMotives][sniper] no public nest with a clear shot overlooks {LName(loc)}; trying next candidate.");
                }
                return null;
            }
            catch { return null; }
        }

        // A node representing where the victim will be at 'loc' (for LOS scoring): their real node if already there,
        // else the location's anchor, else its first node. Read-only; never throws.
        private static NewNode TargetNodeAt(Human victim, NewGameLocation loc)
        {
            try
            {
                if (victim != null && loc != null)
                {
                    NewGameLocation vl = null; try { vl = victim.currentGameLocation; } catch { }
                    if (vl != null && vl.Pointer == loc.Pointer) { var vn = victim.currentNode; if (vn != null) return vn; }
                }
                var a = SafeAnchor(loc); if (a != null) return a;
                var ns = loc != null ? loc.nodes : null;
                if (ns != null && ns.Count > 0) return ns[0];
            }
            catch { }
            return null;
        }

        // THE BETTER NEST CALCULATION (the user's request). Enumerate candidate nests overlooking 'targetLoc' and pick
        // the best one with a genuine, raycast-verified line of sight to 'targetNode' (where the victim will stand),
        // preferring a PUBLIC/accessible nest (not the killer's own home) then the closest, within SniperMaxNestMeters.
        // Candidates come from TWO sources: (a) the buildings FACING targetLoc's windows (Toolbox.GetFacingBuildingFromWindow),
        // each scored by the game's per-building window scorer (ScanBuildingForSniperVantagePoints) -- these are the close
        // local windows/rooftops the global solver hides; and (b) the game's global-best vantage
        // (TryGetSniperVantagePoint) so a genuine overlooking rooftop is still considered. Each candidate's node is
        // LOS-checked with DataRaycastController.NodeRaycast (which treats window glass as transparent, matching the
        // game's own scorer). This is what makes ExCop land local nests instead of always the one dominant rooftop.
        internal static bool FindBestPublicNest(Human killer, NewGameLocation targetLoc, NewNode targetNode, out NewWall bestWall, out float bestDist, out bool bestPublic, out List<NewNode> seenNodes)
        {
            bestWall = null; bestDist = float.MaxValue; bestPublic = false; seenNodes = null;
            bool diag = false; try { diag = DebugTools.EnableDebugKeys; } catch { }
            try
            {
                if (killer == null || targetLoc == null) return false;
                var tb = Toolbox.Instance; var drc = DataRaycastController.Instance;
                if (tb == null || drc == null) return false;
                NewGameLocation kh = null; try { kh = killer.home; } catch { }

                // Target set = the site's nodes (where the victim may stand). targets[0] is the PRIMARY node we most want
                // covered (the victim's actual node when known, else the anchor). Sample MANY of the site's nodes so a
                // nest's COVERAGE (how much of the site it can see) is meaningful -- a broad overlook that sees most of
                // the ward beats a window that only sees one corner.
                var targets = new List<NewNode>();
                if (targetNode != null) targets.Add(targetNode);
                NewNode anchor = null; try { anchor = targetLoc.anchorNode; } catch { }
                if (anchor != null && !targets.Contains(anchor)) targets.Add(anchor);
                try { var ns = targetLoc.nodes; if (ns != null) { int c = ns.Count; for (int i = 0; i < c && targets.Count < 30; i++) { var n = ns[i]; if (n != null && !targets.Contains(n)) targets.Add(n); } } }
                catch (Exception e) { if (diag) MotivesPlugin.Log.LogWarning($"[SODMotives][nest] {LName(targetLoc)} nodes err: {e.Message}"); }
                if (targets.Count == 0) { if (diag) MotivesPlugin.Log.LogInfo($"[SODMotives][nest] {LName(targetLoc)}: no target nodes."); return false; }
                Vector3 refPos = default; bool haveRef = false;
                try { var rn = anchor ?? targets[0]; if (rn != null) { refPos = rn.position; haveRef = true; } } catch { }

                var candidates = new List<NewWall>();
                int scanned = 0;
                // Enumerate NEARBY buildings (centre within SniperNestSearchMeters of the site) and scan each for its best
                // window onto the site. This is the key fix: the game's own GetFacingBuildingFromWindow only finds the
                // building a ward window DIRECTLY faces on ONE side, so an overlook on a DIFFERENT side (or a few floors up)
                // -- e.g. Plaza Orchid across from the ward -- is never even considered. Scanning all nearby buildings
                // catches them. The game's ScanBuilding validates each window's LOS to the site internally.
                // These call the patched solver overload, so guard against re-entering our own postfix.
                _inNestScan = true;
                try
                {
                    // Collect DISTINCT buildings that have a location (room/landing) near the site, via the game-location
                    // directory (CityData has no building directory in interop). Each such building is a candidate nest,
                    // regardless of which SIDE of the site it faces -- so an overlook like Plaza Orchid across the ward on a
                    // different side than Lovelace is included.
                    var cd = CityData.Instance;
                    var locs = cd != null ? cd.gameLocationDirectory : null;
                    if (locs != null && haveRef)
                    {
                        var nearB = new Dictionary<System.IntPtr, KeyValuePair<float, NewBuilding>>();
                        int lc = 0; try { lc = locs.Count; } catch { }
                        for (int i = 0; i < lc; i++)
                        {
                            NewGameLocation loc = null; try { loc = locs[i]; } catch { }
                            if (loc == null) continue;
                            NewNode an = null; try { an = loc.anchorNode; } catch { }
                            if (an == null) continue;
                            float ld; try { ld = Vector3.Distance(an.position, refPos); } catch { continue; }
                            if (ld > SniperNestSearchMeters) continue;
                            NewBuilding b = null; try { b = loc.building; } catch { }
                            if (b == null) continue;
                            System.IntPtr bp; try { bp = b.Pointer; } catch { continue; }
                            if (!nearB.TryGetValue(bp, out var ex) || ld < ex.Key) nearB[bp] = new KeyValuePair<float, NewBuilding>(ld, b);
                        }
                        var near = new List<KeyValuePair<float, NewBuilding>>(nearB.Values);
                        near.Sort((x, y) => x.Key.CompareTo(y.Key));
                        int cap = Math.Min(near.Count, 14);   // nearest N distinct buildings -- bounds the cost of the per-building scans
                        if (diag)
                        {
                            var sb = new System.Text.StringBuilder();
                            for (int i = 0; i < cap; i++) { try { var b = near[i].Value; sb.Append(b != null ? b.name : "?").Append('(').Append(near[i].Key.ToString("0")).Append("m) "); } catch { } }
                            MotivesPlugin.Log.LogInfo($"[SODMotives][nest] near buildings for {LName(targetLoc)} ({near.Count} found, scanning {cap}): {sb}");
                        }
                        for (int i = 0; i < cap; i++)
                        {
                            var b = near[i].Value; scanned++;
                            for (int s = 0; s < 3; s++)   // a few samples past the scorer's random term
                            {
                                NewWall w = null; float sc = 0f; bool ok = false;
                                var acc = new Il2CppSystem.Collections.Generic.List<NewNode.NodeAccess>();
                                try { ok = tb.ScanBuildingForSniperVantagePoints(killer, b, targetLoc, out w, out sc, ref acc); } catch { ok = false; }
                                if (ok && w != null) candidates.Add(w);
                            }
                        }
                    }
                    // global-best backstop (a genuine overlooking rooftop, if no nearby-building window wins).
                    try { NewWall w = null; float sc = 0f; if (tb.TryGetSniperVantagePoint(killer, targetLoc, out w, out sc) && w != null) candidates.Add(w); } catch (Exception e) { if (diag) MotivesPlugin.Log.LogWarning($"[SODMotives][nest] global solver err: {e.Message}"); }
                }
                catch (Exception e) { if (diag) MotivesPlugin.Log.LogWarning($"[SODMotives][nest] enumerate err: {e.Message}"); }
                finally { _inNestScan = false; }

                // Rank by COVERAGE: the nest that can SEE THE MOST of the site wins -- a broad overlook (like Plaza Orchid
                // seeing most of the ward) catches the victim wherever they settle, which a single-window nest can't. Then
                // prefer public/accessible, then a nest that sees the primary node, then closest. LOS is the game's own
                // node-graph raycast (windows transparent), the same one its scorer uses. Distance gate rejects a genuinely
                // far cross-city rooftop (dominant-street false positive).
                int tooFar = 0, bestCover = -1;
                bool bestPrimary = false;
                var seenN = new HashSet<System.IntPtr>();
                foreach (var w in candidates)
                {
                    if (w == null) continue;
                    NewNode wn = null; try { wn = w.node; } catch { }
                    if (wn == null) continue;
                    try { if (!seenN.Add(wn.Pointer)) continue; } catch { }   // score each distinct nest node once
                    float d = float.MaxValue; int cover = 0; bool seesPrimary = false; var thisSeen = new List<NewNode>();
                    for (int i = 0; i < targets.Count; i++)
                    {
                        var t = targets[i]; if (t == null) continue;
                        float dd; try { dd = Vector3.Distance(wn.position, t.position); } catch { dd = float.MaxValue; }
                        if (dd < d) d = dd;
                        bool c = false; try { c = drc.NodeRaycast(wn, t, out _, (NewDoor)null, false); } catch { }
                        if (c) { cover++; if (i == 0) seesPrimary = true; thisSeen.Add(t); }
                    }
                    bool pub = true; try { var nl = wn.gameLocation; pub = kh == null || nl == null || nl.Pointer != kh.Pointer; } catch { }
                    bool far = d > SniperMaxNestMeters;   // beyond the believable-overlook cap
                    if (diag)
                        MotivesPlugin.Log.LogInfo($"[SODMotives][nest]   candidate {LName(NestLoc(w))} {d:0}m {(pub ? "public" : "killer-home")} cover={cover}/{targets.Count} primary={seesPrimary}{(far ? " TOOFAR" : "")}{(cover < 1 ? " NOLOS" : "")}");
                    if (far) { tooFar++; continue; }
                    // A nest that sees NONE of the victim's spots (node-graph LOS) is useless: there is no window to herd
                    // the victim to and no physics shot can ever clear, so pinning it just strands the victim at the site
                    // for SniperPinStallHours before falling back. Skip it -- the case then uses the game's default site
                    // (its best-vantage rooftop) from the START, exactly as vanilla would. (This is what happened to the
                    // Laster's Management case: every candidate scored cover=0 because node-graph LOS is blind to the real
                    // overlooking window across the park, so the blind global-solver rooftop got pinned and stalled 8h.)
                    if (cover < 1) continue;
                    bool better = bestWall == null
                        || (pub != bestPublic ? pub
                            : cover != bestCover ? cover > bestCover
                            : seesPrimary != bestPrimary ? seesPrimary
                            : d < bestDist);
                    if (better)
                    {
                        bestWall = w; bestDist = d; bestPublic = pub; bestCover = cover; bestPrimary = seesPrimary;
                        // ALL the nodes this nest actually has a clear shot to. We herd the victim to WANDER among these,
                        // so they move through the killer's sightline -- a specific node can be node-graph-"visible" yet
                        // physics-blocked by furniture (the reported "sniper couldn't fire until the victim moved"), so
                        // giving them a few in-sightline spots to move between lets a clear physics shot happen.
                        seenNodes = thisSeen;
                    }
                }
                // STAGE 2 -- PHYSICS-LOS RESCUE. Only when node-graph (Stage 1) found NO nest: enumerate nearby
                // ACCESSIBLE windows ourselves and pick one with a real physics line to the site's own windows (which
                // the node-graph check is blind to). Runs on the failing case only, so working cases are untouched.
                _lastNestWasPhysics = false;
                bool physicsOn = false; try { physicsOn = SniperPhysicsLosNest; } catch { }
                if (physicsOn && bestWall == null && haveRef)
                {
                    if (PhysicsRescueNest(killer, targetLoc, refPos, kh, out var pWall, out var pDist, out var pPub, out var pSeen) && pWall != null)
                    {
                        bestWall = pWall; bestDist = pDist; bestPublic = pPub;
                        bestCover = pSeen != null ? pSeen.Count : 1; seenNodes = pSeen; _lastNestWasPhysics = true;
                    }
                }
                else if (physicsOn && bestWall != null && haveRef && diag)
                {
                    // LOG-ONLY A/B (debug only): the node-graph pass already found a nest (e.g. the working Daffodil Ward
                    // case), so we do NOT run the rescue for real -- adopting a physics pick over a firing node-graph pick
                    // could regress it. But we run it here purely to LOG what physics WOULD choose, so the tester can see
                    // whether physics finds a broader overlook (Plaza Orchid vs Lovelace) on the very cases that don't
                    // reach the rescue. No behavioural effect -- the node-graph pick stands.
                    if (PhysicsRescueNest(killer, targetLoc, refPos, kh, out var cWall, out var cDist, out var cPub, out var cSeen) && cWall != null)
                        MotivesPlugin.Log.LogInfo($"[SODMotives][phys-los][compare] node-graph PICK {LName(NestLoc(bestWall))} cover={bestCover}/{targets.Count} (kept) vs physics-would-pick {LName(NestLoc(cWall))} {cDist:0}m sees {(cSeen != null ? cSeen.Count : 0)} site-windows -- NOT adopted (node-graph pick already works).");
                }
                // Expand the wander set: with only the ~2 strictly-visible nodes the victim just paces a straight line
                // between them, and if that line is furniture-blocked the shot never clears. Add the ward nodes NEAR the
                // visible ones so the victim wanders a small CLUSTER around the windows and steps into a physics-clear
                // spot (physics LOS differs from the node-graph LOS -- the clear spot is often a nearby node not flagged
                // "visible"). All these nodes are close to the same windows, so the wander stays local.
                if (seenNodes != null && seenNodes.Count > 0)
                {
                    var expanded = new List<NewNode>(seenNodes);
                    for (int i = 0; i < targets.Count && expanded.Count < 14; i++)
                    {
                        var t = targets[i]; if (t == null || expanded.Contains(t)) continue;
                        bool near = false;
                        for (int j = 0; j < seenNodes.Count; j++)
                        { try { if (Vector3.Distance(t.position, seenNodes[j].position) <= 3.5f) { near = true; break; } } catch { } }
                        if (near) expanded.Add(t);
                    }
                    seenNodes = expanded;
                }
                if (diag)
                    MotivesPlugin.Log.LogInfo($"[SODMotives][nest] {LName(targetLoc)}: targets={targets.Count} nearBuildingsScanned={scanned} candidates={candidates.Count} tooFar={tooFar} -> {(bestWall != null ? "PICK " + LName(NestLoc(bestWall)) + " " + bestDist.ToString("0") + "m " + (bestPublic ? "public" : "killer-home") + " cover=" + bestCover + "/" + targets.Count + " wanderNodes=" + (seenNodes != null ? seenNodes.Count : 0) : "NONE")}.");
                return bestWall != null;
            }
            catch (Exception e) { if (diag) MotivesPlugin.Log.LogWarning($"[SODMotives][nest] {LName(targetLoc)} FATAL: {e.Message}"); return false; }
        }

        private static NewGameLocation NestLoc(NewWall w) { try { var n = w != null ? w.node : null; return n != null ? n.gameLocation : null; } catch { return null; } }

        // F9 overlay: describe the mod's OWN sniper nest (the den the killer shoots FROM) and whether he's reached it.
        // The game's sniperKillShotNode stays (0,0,0) in our deferred model, so the old "SNIPE SHOT lining up" line never
        // said anything. This exposes the real nest we forced (via the vantage-solver postfix) + the killer's live
        // distance to it, so the tester can see whether he's travelling to the nest, is in position, or idling.
        internal static bool ActiveSniperNest(Human killer, Human victim, out string nestName, out float killerToNestM, out bool pinnedLocal, out int wanderCount)
        {
            nestName = null; killerToNestM = -1f; pinnedLocal = false; wanderCount = 0;
            try
            {
                if (_moNestWall == null) return false;
                nestName = LName(NestLoc(_moNestWall));
                NewNode nn = null; try { nn = _moNestWall.node; } catch { }
                if (killer != null && nn != null)
                { try { var kn = killer.currentNode; if (kn != null) killerToNestM = Vector3.Distance(kn.position, nn.position); } catch { } }
                if (victim != null)
                {
                    try { pinnedLocal = _sniperPin.ContainsKey(victim.humanID); } catch { }
                    if (_sniperWander.TryGetValue(victim.humanID, out var wl) && wl != null) wanderCount = wl.Count;
                }
                return true;
            }
            catch { return false; }
        }

        // ============================ PHYSICS-LOS NEST RESCUE ======================================================
        // UnityEngine.Physics / Ray / RaycastHit / LayerMask / QueryTriggerInteraction resolve from the interop
        // PhysicsModule + CoreModule DLLs (probe-confirmed). Vector3 operator overloads are NOT exposed by this
        // interop build (probe-confirmed: only Vector3.Distance, the (x,y,z) ctor and Vector3.up), so all vector math
        // here is component-wise. This is the mod's first use of the Physics idiom, so every call is try/catch'd.

        private static int RainGlassLayer()
        {
            if (_rainGlassLayer == int.MinValue)
            { try { _rainGlassLayer = LayerMask.NameToLayer("RainWindowGlass"); } catch { _rainGlassLayer = -1; } }
            return _rainGlassLayer;
        }

        // The ray mask = the game's OWN Toolbox.sniperLOSMask (probe-confirmed Int32 property -- the exact mask the
        // live fire gate uses), with the RainWindowGlass bit STRIPPED so a single closest-hit Raycast passes cleanly
        // through window glass exactly as the gate treats it. Falls back to DefaultRaycastLayers, then ~0.
        private static int SniperPhysMask()
        {
            int m = 0;
            try { var tb = Toolbox.Instance; if (tb != null) m = tb.sniperLOSMask; } catch { m = 0; }
            if (m == 0) { try { m = UnityEngine.Physics.DefaultRaycastLayers; } catch { m = ~0; } }
            if (m == 0) m = ~0;                                   // never mask 0 (hits nothing -> everything looks clear)
            int g = RainGlassLayer();
            if (g >= 0) { try { m &= ~(1 << g); } catch { } }     // window glass must never block the shot
            return m;
        }

        // One-time diagnostic (behind EnableDebugKeys): dump the 32 layer names + the game's raw sniperLOSMask + the
        // resolved RainWindowGlass index + the mask we actually use, so one log line confirms the layer exists and
        // that glass is stripped from the ray before we trust any physics result.
        internal static void DumpLayers()
        {
            try
            {
                var sb = new System.Text.StringBuilder("[SODMotives][phys-los] layers ");
                for (int i = 0; i < 32; i++)
                { string nm = null; try { nm = LayerMask.LayerToName(i); } catch { } if (!string.IsNullOrEmpty(nm)) sb.Append(i).Append('=').Append(nm).Append(' '); }
                int raw = 0; try { var tb = Toolbox.Instance; if (tb != null) raw = tb.sniperLOSMask; } catch { }
                sb.Append("| sniperLOSMask=0x").Append(raw.ToString("X8")).Append(" RainWindowGlass=").Append(RainGlassLayer()).Append(" usedMask=0x").Append(SniperPhysMask().ToString("X8"));
                MotivesPlugin.Log.LogInfo(sb.ToString());
            }
            catch { }
        }

        // True when nothing SOLID (non-glass, non-trigger) blocks the straight line strictly BETWEEN two window
        // openings. Both ends are openings in open air just outside the glass, so we pull each end in by PhysEdge to
        // avoid self-hitting the opening's own frame, and cap the ray just short of the far opening (we only care
        // about what is IN BETWEEN, not the far wall). Component-wise math. Never throws.
        private static bool PhysLosClearBetween(Vector3 a, Vector3 b, int mask)
        {
            try
            {
                float dist = Vector3.Distance(a, b);
                if (dist < 0.2f) return true;                                   // effectively the same opening
                if (dist > SniperMaxNestMeters + 6f) return false;             // never a long cross-city ray
                float dx = (b.x - a.x) / dist, dy = (b.y - a.y) / dist, dz = (b.z - a.z) / dist;
                var origin = new Vector3(a.x + dx * PhysEdge, a.y + dy * PhysEdge, a.z + dz * PhysEdge);
                var dir = new Vector3(dx, dy, dz);
                float maxD = dist - PhysEdge * 2f;                             // stop just before the far opening
                if (maxD <= 0.1f) return true;
                RaycastHit hit; bool got;
                try { got = UnityEngine.Physics.Raycast(origin, dir, out hit, maxD, mask, QueryTriggerInteraction.Ignore); }
                catch { return false; }
                return !got;                                                    // no solid collider in between == clear line
            }
            catch { return false; }
        }

        // Cheap "is this area's geometry actually streamed in?" probe: cast straight DOWN from just above a point and
        // require a floor hit. At case creation a site far from the player may not be loaded (no colliders), which
        // would make every LOS ray falsely read "clear"; if the floor under the site isn't there we DON'T trust
        // physics and let the case use the game default instead. Never throws.
        private static bool SceneLoadedAt(Vector3 p, int mask)
        {
            try
            {
                var origin = new Vector3(p.x, p.y + 2.0f, p.z);
                var down = new Vector3(0f, -1f, 0f);
                RaycastHit hit; bool got;
                try { got = UnityEngine.Physics.Raycast(origin, down, out hit, 10f, mask, QueryTriggerInteraction.Ignore); }
                catch { return false; }
                return got;
            }
            catch { return false; }
        }

        // The user's accessibility rule: a nest must be somewhere the KILLER can legitimately be -- a rooftop / public
        // or common area (lobby, hallway, stairwell landing), OR an address the suspect has access to (their own home,
        // their workplace, or a place they own). NOT a stranger's private flat or a locked business.
        // The RELIABLE access signal is a NON-TRESPASS safe spot: killer.FindSafeTeleport(nl, false, allowTrespass:false)
        // returns a node only where the killer can stand WITHOUT trespassing (public/common/owned) -- our own kidnap
        // decode (docs/extensions/kidnap-abduction-handover.md) established that IsPublicallyOpen is NOT the walk-in
        // signal (a walk-in-able unit read NPC_OPEN=false; access is gated by trespass). So we lead with the
        // FindSafeTeleport test; home/owns/works are kept as definite-yes fast-paths. Out-params expose the reasons for
        // diagnostics. All members probe-confirmed. FindSafeTeleport is the costlier call, so callers gate it behind the
        // window/distance filters (only locations with an in-range window are ever tested).
        private static bool KillerCanAccess(Human killer, NewGameLocation nl, out bool owned, out bool reach)
        {
            owned = false; reach = false;
            try
            {
                if (nl == null) return false;
                NewAddress addr = null; try { addr = nl.thisAsAddress; } catch { }
                if (addr != null)
                {
                    try { var kh = killer != null ? killer.home : null; if (kh != null && kh.Pointer == addr.Pointer) owned = true; } catch { }
                    if (!owned) try { var own = addr.owners; if (own != null) { int c = own.Count; for (int i = 0; i < c; i++) { var o = own[i]; if (o != null && killer != null && o.Pointer == killer.Pointer) { owned = true; break; } } } } catch { }
                }
                if (!owned) try { var j = killer != null ? killer.job : null; var e = j != null ? j.employer : null; var pob = e != null ? e.placeOfBusiness : null; if (pob != null && pob.Pointer == nl.Pointer) owned = true; } catch { }
                if (owned) { reach = true; return true; }
                try { reach = killer != null && killer.FindSafeTeleport(nl, false, false) != null; } catch { reach = false; }
                return reach;
            }
            catch { return false; }
        }

        // Collect a location's WINDOW entrances (NodeAccess.accessType == window). openings = the world opening point
        // (the aim/fire point); stands = the node on the 'inside' side (where the victim is exposed / the killer
        // stands); walls = the window wall (what the killer travels to). Probe-confirmed NodeAccess members.
        private static void CollectWindows(NewGameLocation loc, NewGameLocation inside, List<Vector3> openings, List<NewNode> stands, List<NewWall> walls, int cap)
        {
            try
            {
                var ents = loc != null ? loc.entrances : null;
                if (ents == null) return;
                int c = 0; try { c = ents.Count; } catch { }
                for (int i = 0; i < c && openings.Count < cap; i++)
                {
                    NewNode.NodeAccess e = null; try { e = ents[i]; } catch { }
                    if (e == null) continue;
                    bool isWin = false; try { isWin = e.accessType == NewNode.NodeAccess.AccessType.window; } catch { }
                    if (!isWin) continue;
                    NewWall ww = null; try { ww = e.wall; } catch { }
                    NewNode fn = null, tn = null; try { fn = e.fromNode; } catch { } try { tn = e.toNode; } catch { }
                    NewNode stand = null;
                    try { if (fn != null && inside != null && fn.gameLocation != null && fn.gameLocation.Pointer == inside.Pointer) stand = fn; } catch { }
                    try { if (stand == null && tn != null && inside != null && tn.gameLocation != null && tn.gameLocation.Pointer == inside.Pointer) stand = tn; } catch { }
                    if (stand == null) stand = fn != null ? fn : tn;
                    Vector3 op = default; bool haveOp = false;
                    try { op = e.worldAccessPoint; haveOp = true; } catch { }
                    if (!haveOp && stand != null) { try { op = new Vector3(stand.position.x, stand.position.y + PhysBodyUp, stand.position.z); haveOp = true; } catch { } }
                    if (!haveOp) continue;
                    openings.Add(op); stands.Add(stand); walls.Add(ww);
                }
            }
            catch { }
        }

        // Node-graph found no nest. Enumerate the site's own windows (aim points) and nearby ACCESSIBLE building
        // windows (candidate nests), and pick the nest that physically overlooks the MOST of the site's windows.
        // Window-opening-to-window-opening rays mirror the live fire gate and avoid threading interior furniture.
        // Only pins a reachable, killer-accessible nest; logs the coverage so the success test is measurable.
        private static bool PhysicsRescueNest(Human killer, NewGameLocation targetLoc, Vector3 refPos, NewGameLocation kh,
            out NewWall bestWall, out float bestDist, out bool bestPublic, out List<NewNode> seenNodes)
        {
            bestWall = null; bestDist = float.MaxValue; bestPublic = false; seenNodes = null;
            bool diag = false; try { diag = DebugTools.EnableDebugKeys; } catch { }
            try
            {
                int mask = SniperPhysMask();
                if (diag && !_physLayersDumped) { _physLayersDumped = true; DumpLayers(); }

                // Bail if the site's geometry isn't streamed in (else every ray falsely reads "clear").
                if (!SceneLoadedAt(refPos, mask))
                { if (diag) MotivesPlugin.Log.LogInfo($"[SODMotives][phys-los] {LName(targetLoc)}: site geometry not loaded at case time -> skip physics rescue (game default)."); return false; }

                // 1) The SITE's own windows = the aim points (where the victim is exposed, and where we herd them).
                var siteOpen = new List<Vector3>(); var siteStand = new List<NewNode>(); var siteWalls = new List<NewWall>();
                CollectWindows(targetLoc, targetLoc, siteOpen, siteStand, siteWalls, PhysMaxSiteWindows);
                if (siteOpen.Count == 0)
                {
                    // No enumerable windows (e.g. an open site): fall back to the site's nodes as aim points.
                    try { var ns = targetLoc.nodes; if (ns != null) { int c = ns.Count; for (int i = 0; i < c && siteOpen.Count < PhysMaxSiteWindows; i++) { var n = ns[i]; if (n == null) continue; try { siteOpen.Add(new Vector3(n.position.x, n.position.y + PhysBodyUp, n.position.z)); siteStand.Add(n); } catch { } } } } catch { }
                }
                if (siteOpen.Count == 0) { if (diag) MotivesPlugin.Log.LogInfo($"[SODMotives][phys-los] {LName(targetLoc)}: no site windows/nodes to aim at."); return false; }

                // 2) Candidate NEST windows from nearby ACCESSIBLE addresses.
                var nestWalls = new List<NewWall>(); var nestOpen = new List<Vector3>(); var nestNode = new List<NewNode>(); var nestPub = new List<bool>();
                var nestSeen = new HashSet<System.IntPtr>();
                int scanned = 0, accessible = 0, candLogged = 0;
                _inNestScan = true;
                try
                {
                    var cd = CityData.Instance; var dir = cd != null ? cd.gameLocationDirectory : null;
                    int lc = 0; try { lc = dir != null ? dir.Count : 0; } catch { }
                    for (int li = 0; li < lc && nestWalls.Count < PhysMaxNestWindows; li++)
                    {
                        NewGameLocation loc = null; try { loc = dir[li]; } catch { }
                        if (loc == null) continue;
                        try { if (loc.Pointer == targetLoc.Pointer) continue; } catch { }        // can't nest in the target itself
                        NewNode an = null; try { an = loc.anchorNode; } catch { }
                        if (an == null) continue;
                        float ld; try { ld = Vector3.Distance(an.position, refPos); } catch { continue; }
                        if (ld > SniperNestSearchMeters) continue;
                        // Collect this location's windows FIRST and keep only those whose nest node is in range, so the
                        // costlier access test (FindSafeTeleport) runs ONLY on locations that actually have a candidate
                        // window near the site.
                        var wOpen = new List<Vector3>(); var wStand = new List<NewNode>(); var wWall = new List<NewWall>();
                        CollectWindows(loc, loc, wOpen, wStand, wWall, 16);
                        int windowsInRange = 0;
                        for (int wi = 0; wi < wStand.Count; wi++)
                        { var n0 = wStand[wi]; if (n0 == null) continue; float d0; try { d0 = Vector3.Distance(n0.position, refPos); } catch { continue; } if (d0 <= SniperMaxNestMeters) windowsInRange++; }
                        if (windowsInRange == 0) continue;
                        scanned++;
                        bool owned = false, reach = false, acc = false;
                        try { acc = KillerCanAccess(killer, loc, out owned, out reach); } catch { owned = false; reach = false; acc = false; }
                        if (diag && candLogged < 30)
                        { candLogged++; MotivesPlugin.Log.LogInfo($"[SODMotives][phys-los]   cand {LName(loc)} {ld:0}m windows-in-range={windowsInRange} owned={owned} reach={reach} -> {(acc ? "ACCEPT" : "reject (killer cannot access without trespass)")}"); }
                        if (!acc) continue;
                        accessible++;
                        for (int wi = 0; wi < wWall.Count && nestWalls.Count < PhysMaxNestWindows; wi++)
                        {
                            var nn = wStand[wi]; var ww = wWall[wi]; if (nn == null || ww == null) continue;
                            System.IntPtr np; try { np = nn.Pointer; } catch { continue; }
                            float nd; try { nd = Vector3.Distance(nn.position, refPos); } catch { continue; }
                            if (nd > SniperMaxNestMeters) continue;
                            if (!nestSeen.Add(np)) continue;
                            nestWalls.Add(ww); nestOpen.Add(wOpen[wi]); nestNode.Add(nn);
                            bool pub = true; try { pub = kh == null || loc.Pointer != kh.Pointer; } catch { }
                            nestPub.Add(pub);
                        }
                    }
                }
                catch (Exception ee) { if (diag) MotivesPlugin.Log.LogWarning($"[SODMotives][phys-los] enumerate err: {ee.Message}"); }
                finally { _inNestScan = false; }

                // 3) Score each candidate nest by how many SITE windows it physically overlooks (broadest wins).
                int rays = 0, bestCover = 0; bool bestPrimary = false;
                for (int ci = 0; ci < nestWalls.Count; ci++)
                {
                    var from = nestOpen[ci];
                    int cover = 0; bool seesPrimary = false; var thisSeen = new List<NewNode>();
                    for (int si = 0; si < siteOpen.Count; si++)
                    {
                        rays++;
                        if (PhysLosClearBetween(from, siteOpen[si], mask))
                        { cover++; if (si == 0) seesPrimary = true; var sn = siteStand[si]; if (sn != null && !thisSeen.Contains(sn)) thisSeen.Add(sn); }
                    }
                    if (cover < 1) continue;
                    float d = float.MaxValue; try { d = Vector3.Distance(nestNode[ci].position, refPos); } catch { }
                    bool pub = nestPub[ci];
                    bool better = bestWall == null
                        || (cover != bestCover ? cover > bestCover               // BROADEST overlook first (the user's criterion)
                            : pub != bestPublic ? pub                            // then public/accessible over killer-home
                            : seesPrimary != bestPrimary ? seesPrimary
                            : d < bestDist);
                    if (better)
                    { bestWall = nestWalls[ci]; bestDist = d; bestPublic = pub; bestCover = cover; bestPrimary = seesPrimary; seenNodes = thisSeen; }
                }

                // (Reachability is already guaranteed: every accepted candidate location passed KillerCanAccess, which
                //  requires the killer to own/work/live there OR to have a non-trespass safe spot there -- so the winning
                //  nest's location is walk-reachable by construction. No separate winner re-check needed.)

                if (diag)
                    MotivesPlugin.Log.LogInfo($"[SODMotives][phys-los] {LName(targetLoc)}: siteWindows={siteOpen.Count} nearScanned={scanned} accessibleLocs={accessible} nestWindows={nestWalls.Count} rays={rays} -> {(bestWall != null ? "PHYS-PICK " + LName(NestLoc(bestWall)) + " " + bestDist.ToString("0") + "m " + (bestPublic ? "public" : "killer-home") + " sees " + bestCover + "/" + siteOpen.Count + " site-windows" : "NONE (fall to game default)")}.");
                return bestWall != null;
            }
            catch (Exception e) { if (diag) MotivesPlugin.Log.LogWarning($"[SODMotives][phys-los] {LName(targetLoc)} FATAL: {e.Message}"); return false; }
        }

        // Once the killer is IN POSITION at a physics-rescued nest, geometry is guaranteed loaded, so re-check that
        // the nest still physically overlooks at least ONE of the site's windows. Guards against a selection-time
        // false positive (a phantom un-loaded building between nest and site let the ray read clear); if the nest is
        // actually blind, we release NOW instead of after the full stall. Window-based (not the victim's transient
        // position), so a victim momentarily behind furniture never triggers a release. Never throws.
        private static bool PhysNestStillOverlooksSite(NewGameLocation site)
        {
            try
            {
                if (_moNestWall == null || site == null) return true;   // nothing to check -> don't release
                int mask = SniperPhysMask();
                // Mirror the LIVE fire gate: origin = the killer's actual firing node (the game stamps action node =
                // _moNestWall.node) raised to eye height; aim at the victim's BODY ANCHOR at each of the site's stand
                // nodes (stand.position + torso), NOT the window opening -- an opening can be clear while the line to a
                // body just inside it is blocked (that mismatch was the stall/false-release risk). We keep a second
                // origin (the wall centre) purely so a marginal node-origin ray can't false-release a good pin. Release
                // (return false) ONLY if blind to EVERY body anchor from EVERY origin.
                var origins = new List<Vector3>();
                try { var nn = _moNestWall.node; if (nn != null) origins.Add(new Vector3(nn.position.x, nn.position.y + PhysEyeUp, nn.position.z)); } catch { }
                try { origins.Add(new Vector3(_moNestWall.position.x, _moNestWall.position.y + PhysEyeUp, _moNestWall.position.z)); } catch { }
                if (origins.Count == 0) return true;
                // Body-anchor aim points = the stand nodes just inside the site's windows (where the victim is exposed),
                // else a sample of the site's nodes.
                var siteOpen = new List<Vector3>(); var siteStand = new List<NewNode>(); var siteWalls = new List<NewWall>();
                CollectWindows(site, site, siteOpen, siteStand, siteWalls, PhysMaxSiteWindows);
                var aims = new List<Vector3>();
                for (int i = 0; i < siteStand.Count; i++) { var n = siteStand[i]; if (n != null) { try { aims.Add(new Vector3(n.position.x, n.position.y + PhysBodyUp, n.position.z)); } catch { } } }
                if (aims.Count == 0)
                { try { var ns = site.nodes; if (ns != null) { int c = ns.Count; for (int i = 0; i < c && aims.Count < PhysMaxSiteWindows; i++) { var n = ns[i]; if (n != null) aims.Add(new Vector3(n.position.x, n.position.y + PhysBodyUp, n.position.z)); } } } catch { } }
                if (aims.Count == 0) return true;   // can't enumerate site aim points -> don't release
                for (int o = 0; o < origins.Count; o++)
                    for (int i = 0; i < aims.Count; i++)
                        if (PhysLosClearBetween(origins[o], aims[i], mask)) return true;
                return false;
            }
            catch { return true; }
        }

        // Killer is standing at (within ~4m of) the current pinned nest node.
        private static bool KillerAtNest(Human killer)
        {
            try
            {
                if (killer == null || _moNestWall == null) return false;
                NewNode nn = _moNestWall.node; NewNode kn = killer.currentNode;
                if (nn == null || kn == null) return false;
                return Vector3.Distance(nn.position, kn.position) <= 4f;
            }
            catch { return false; }
        }

        // The game's OWN default sniper site (its best-vantage rooftop, e.g. the city's dominant street) via
        // Murder.TryPickNewVictimSite. When that returns nothing, anchor to a NON-HOME location the victim is usually
        // away from so the state machine reaches SITECHECK and the game's own force-kill can fire (never the home --
        // that strands a homebody); and if there is no non-home anchor at all (~never), revert the case to vanilla so
        // it can't hang. Also used to RELEASE a stalled local pin: TryPickNewVictimSite returns a site != the current
        // (pinned) one, i.e. the global best, so calling this off the pin lands on the default.
        private static void SeedSniperDefault(MurderController.Murder murder, Human killer, Human victim)
        {
            NewGameLocation seed = null;
            for (int attempt = 0; attempt < 4 && seed == null; attempt++)
            {
                try { if (murder.TryPickNewVictimSite(out var picked) && picked != null) seed = picked; } catch { }
            }
            bool viable = seed != null;
            if (seed == null)
            {
                NewGameLocation vhome = null; try { vhome = victim.home; } catch { }
                NewGameLocation vwork = null, khome = null, kwork = null;
                try { var j = victim.job; var e = j != null ? j.employer : null; if (e != null) vwork = e.placeOfBusiness; } catch { }
                try { khome = killer.home; } catch { }
                try { var j = killer.job; var e = j != null ? j.employer : null; if (e != null) kwork = e.placeOfBusiness; } catch { }
                bool NotHome(NewGameLocation l) { try { return l != null && (vhome == null || l.Pointer != vhome.Pointer); } catch { return false; } }
                if (NotHome(vwork)) seed = vwork;          // victim's workplace (they leave home to reach it)
                else if (NotHome(khome)) seed = khome;     // killer's home (guaranteed non-home for a non-cohabiting pair)
                else if (NotHome(kwork)) seed = kwork;     // killer's workplace
            }
            if (seed != null)
            {
                try { murder.sniperVictimSite = seed; } catch (Exception se) { MotivesPlugin.Log.LogWarning($"[SODMotives][sniper] seed write err: {se.Message}"); }
                MotivesPlugin.Log.LogInfo($"[SODMotives][sniper] seeded sniperVictimSite = {LName(seed)} ({(viable ? "game default (best rooftop)" : "no viable vantage -> non-home anchor; game force-kills")}) for {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)}; deferring shot + force-kill to the game.");
            }
            else
            {
                MotivesPlugin.Log.LogWarning($"[SODMotives][sniper] no vantage-viable site and no non-home anchor for {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} (un-snipeable); reverting this case to vanilla so it can't strand.");
                try { murder.CancelCurrentMurder(); } catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives][sniper] revert err: {ce.Message}"); }
            }
        }

        // Would a VOYEUR sniper work for this pair? VoyeurSniper (requiresSniperVantageAtHome) shoots the victim at
        // their OWN home/work from the KILLER'S window, so it's viable iff the pair does NOT cohabit (you can't
        // snipe your own shared home) AND the killer's home has line of sight to the victim's home or workplace.
        // Uses the game's location-centric vantage solver (killer's home as the nest); retried a few times because
        // the solver's window score is randomized. Returns false -> the caller uses ExCopSniper (street/rooftop),
        // which works for any pair with an exposed routine. Read-only; never throws.
        internal static bool SniperVoyeurViable(Human killer, Human victim)
        {
            try
            {
                if (killer == null || victim == null) return false;
                NewGameLocation kh = null; try { kh = killer.home; } catch { }
                if (kh == null) return false;
                NewGameLocation vh = null; try { vh = victim.home; } catch { }
                if (vh != null && kh.Pointer == vh.Pointer) return false;   // cohabiting -> can't voyeur-snipe the shared home
                NewGameLocation vwork = null;
                try { var job = victim.job; var emp = job != null ? job.employer : null; if (emp != null) vwork = emp.placeOfBusiness; } catch { }
                var tb = Toolbox.Instance; if (tb == null) return false;
                for (int i = 0; i < 3; i++)
                {
                    if (vh != null) { try { if (tb.TryGetSniperVantagePoint(kh, out _, out _, out _, vh)) return true; } catch { } }
                    if (vwork != null) { try { if (tb.TryGetSniperVantagePoint(kh, out _, out _, out _, vwork)) return true; } catch { } }
                }
                return false;
            }
            catch { return false; }
        }

        // Register / clear the active motivated sniper pair so the vantage-solver postfix (Plugin.cs) only ever
        // rewrites the nest for OUR case and never touches a vanilla sniper or the game's selection scoring. Called
        // from the override the instant the sniper pair is committed (and cleared for any non-sniper case).
        internal static void SetActiveMotivatedSniper(Human killer, Human victim)
        {
            _moSniperKiller = killer; _moSniperVictim = victim;
            _moNestWall = null; _moNestSitePtr = System.IntPtr.Zero;
            _moNestPhys = false; _moNestRevalidated = false;   // physics-pin flags share the nest's single-active-case lifecycle
        }
        internal static void ClearActiveMotivatedSniper()
        {
            _moSniperKiller = null; _moSniperVictim = null;
            _moNestWall = null; _moNestSitePtr = System.IntPtr.Zero;
            _moNestPhys = false; _moNestRevalidated = false;
        }

        // The nest for the vantage-solver postfix to force -- ONLY for our active motivated sniper's ONE pinned local
        // site, using the wall FirstViableLocalSite already computed + cached at seeding. This is deliberately narrow:
        // the game's own site picker (Murder.TryPickNewVictimSite) scores MANY candidate sites through the same solver
        // overload, and if we override those we FLATTEN the game's real vantage scoring (every candidate came back 999),
        // which destroys its ranking and makes it pick the same street every time (the "Chandani everywhere" regression).
        // So we return a nest ONLY when the query's requiredTargetSite is exactly the site we pinned -- never a candidate.
        // No recompute here (done at seeding); read-only; never throws.
        internal static bool TryGetPinnedNest(Human sniper, NewGameLocation requiredTargetSite, out NewWall wall)
        {
            wall = null;
            try
            {
                if (!SniperForceLosNest || _inNestScan) return false;                                  // disabled / our own re-rolls
                if (sniper == null || _moSniperKiller == null || sniper.Pointer != _moSniperKiller.Pointer) return false;   // not our killer
                if (requiredTargetSite == null || _moNestSitePtr == System.IntPtr.Zero
                    || requiredTargetSite.Pointer != _moNestSitePtr) return false;                      // ONLY our pinned site, never the game's candidate scoring
                wall = _moNestWall;
                return _moNestWall != null;
            }
            catch { return false; }
        }

        // HERD the victim to the pinned sniper site so they are actually THERE for the killer at the nest to shoot.
        // Mirrors the game's own "GoTo VictimSite routine" (state 3), which never fires for an ExCop case because every
        // location counts as valid -- so we create the same walk goal ourselves, from the game's generic go-to preset
        // (RoutineControls.toGoGoal), and pin it on top so the victim commits. This is the IDENTICAL mechanism the
        // kidnap den-hold uses (EnsureVictimDenGoal, proven in-game): a real WALK with a real trail, not a teleport.
        // Re-asserted each tick (the AI recomputes priority). Never throws.
        private static void EnsureVictimSniperSiteGoal(Human victim, NewGameLocation site, NewNode herdNode, MurderController.Murder murder, int vid)
        {
            try
            {
                if (victim == null || site == null) return;
                NewAIController ai = null; try { ai = victim.ai; } catch { }
                if (ai == null) return;

                // The node to walk to this tick (the current wander target). Always pass a node -- a GoTo goal with a bare
                // location + null node makes the AI log "unable to locate node for GoTo" and drop it.
                NewNode goNode = herdNode;
                if (goNode == null) { try { goNode = victim.FindSafeTeleport(site, false, true); } catch { } }
                if (goNode == null) { try { goNode = site.anchorNode; } catch { } }
                if (goNode == null) return;

                NewAIGoal siteGoal = FindGoalForLocation(ai, site);
                bool sameTarget = _sniperHerdNode.TryGetValue(vid, out var cur); try { sameTarget = sameTarget && goNode != null && cur == goNode.Pointer; } catch { sameTarget = false; }
                if (siteGoal == null || !sameTarget)
                {
                    // First goal, or the wander target rotated: drop ALL old walk goals for this site and issue a fresh
                    // one to the new node so the victim actually MOVES there (and so goals don't accumulate across the
                    // ~30s rotations -- leftovers were keeping the victim stuck at the site after the case fell back).
                    RemoveAllSiteGoals(ai, site);
                    siteGoal = null;
                    AIGoalPreset preset = null; try { preset = RoutineControls.Instance != null ? RoutineControls.Instance.toGoGoal : null; } catch { }
                    if (preset == null) return;
                    try { siteGoal = ai.CreateNewGoal(preset, NowHours(), 999f, goNode, null, site, null, murder, -2); }
                    catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives][sniper] CreateNewGoal(GoTo site) threw: {ce.Message}"); return; }
                    if (siteGoal == null) return;
                    try { _sniperHerdNode[vid] = goNode.Pointer; } catch { }
                    if (DebugTools.EnableDebugKeys && _sniperHerdLogged.Add(vid))
                        MotivesPlugin.Log.LogInfo($"[SODMotives][sniper] herding victim {MotivesPlugin.Name(victim)} to wander the nest's sightline at {LName(site)} (walk goal, so a clear physics shot happens even if the exact node is furniture-blocked).");
                }

                try { siteGoal.basePriority = 100000f; } catch { }
                try { siteGoal.priority = 100000f; } catch { }
                try { siteGoal.isActive = true; } catch { }
                try { ai.currentGoal = siteGoal; } catch { }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][sniper] EnsureVictimSniperSiteGoal err: {e.Message}"); }
        }

        // Undo the herd FORCEFULLY: remove our GoTo-site goal so the victim fully resumes their normal routine. Used when
        // the shot resolves, or when a stalled pin releases to the game default (the reported "victim stayed pinned at
        // home after the case fell back to Mingo" -- dropping the priority alone wasn't enough, so we Remove() it).
        private static void ClearVictimSniperSiteGoal(Human victim, NewGameLocation site)
        {
            try
            {
                if (victim == null || site == null) return;
                NewAIController ai = null; try { ai = victim.ai; } catch { }
                if (ai == null) return;
                RemoveAllSiteGoals(ai, site);
            }
            catch { }
        }

        // Remove EVERY GoTo goal on this AI targeting 'loc' (the wander re-issues one each rotation; leftovers were
        // keeping the victim stuck at the site after a fallback). Bounded, and stops if Remove() is deferred so we
        // never loop forever on the same goal. Never throws.
        private static void RemoveAllSiteGoals(NewAIController ai, NewGameLocation loc)
        {
            try
            {
                var seen = new HashSet<System.IntPtr>();
                for (int iter = 0; iter < 16; iter++)
                {
                    NewAIGoal g = FindGoalForLocation(ai, loc);
                    if (g == null) return;
                    System.IntPtr p; try { p = g.Pointer; } catch { return; }
                    if (!seen.Add(p)) return;   // Remove() didn't take this frame -- stop rather than spin
                    try { g.basePriority = 0f; g.priority = 0f; g.isActive = false; } catch { }
                    try { g.Remove(); } catch { return; }
                }
            }
            catch { }
        }

        // Find a goal on this AI already targeting 'loc' (by gameLocation or passedGameLocation). Read-only; never throws.
        private static NewAIGoal FindGoalForLocation(NewAIController ai, NewGameLocation loc)
        {
            try
            {
                var goals = ai != null ? ai.goals : null;
                if (goals == null || loc == null) return null;
                for (int i = 0; i < goals.Count; i++)
                {
                    var g = goals[i]; if (g == null) continue;
                    NewGameLocation gl = null, pg = null;
                    try { gl = g.gameLocation; } catch { }
                    try { pg = g.passedGameLocation; } catch { }
                    if ((gl != null && gl.Pointer == loc.Pointer) || (pg != null && pg.Pointer == loc.Pointer)) return g;
                }
            }
            catch { }
            return null;
        }

        // WALK-reachable = the victim can reach a node INSIDE the den by NORMAL pathing (allowTrespass=FALSE),
        // i.e. without walking through a locked/forbidden door. This is the property that distinguishes a vanilla
        // den (a walk-in vacant unit — the victim's "GoTo den routine" walk goal completes, VICTIM_AT_DEN=True)
        // from a locked basement (the walk stalls on the street ~89m out and the case loops). Read-only; never
        // throws. This is the pre-condition the den picker enforces so the kidnap walks natively.
        internal static bool KidnapDenWalkReachable(Human victim, NewAddress den)
        {
            try
            {
                if (victim == null || den == null) return false;
                NewNode wn = null; try { wn = victim.FindSafeTeleport(den, false, false); } catch { return false; }
                if (wn == null) return false;
                NewGameLocation wl = null; try { wl = wn.gameLocation; } catch { }
                return wl != null && wl.Pointer == den.Pointer;
            }
            catch { return false; }
        }

        // Called from Patch_MurderStateInject (our existing SetMurderState postfix, already gated to
        // overridden victims) on every state transition of one of our cases.
        internal static void OnState(int victimId, MurderController.MurderState newState)
        {
            if (newState == MurderController.MurderState.executing)
            {
                _watchVictimId = victimId;
                _executingSince = NowHours();
            }
            else if (victimId == _watchVictimId)
            {
                _watchVictimId = -1;   // left 'executing' (post / escaping / etc.) — stop timing it
            }
        }

        private static float NowHours()
        {
            try { var s = SessionData.Instance; if (s != null) return s.gameTime; } catch { }
            return 0f;
        }

        // Polled each frame from DebugTools' Update. Cheap: early-outs unless one of our cases is
        // actually sitting in 'executing'.
        internal static void Tick()
        {
            if (!Enable) return;
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) return;
                var murder = mc.GetCurrentMurder();
                if (murder == null) return;
                var victim = murder.victim; var killer = murder.murderer;
                if (victim == null || killer == null) return;
                int vid = victim.humanID;

                // KIDNAP DIAGNOSTICS (dev-only, gated behind [Debug] EnableDebugKeys): the one-shot observer +
                // the live position sampler both log for ANY kidnap incl. VANILLA, to compare vanilla vs ours
                // side by side, and the observer flips on the game's own verbose murder narration. Off by
                // default so a shipped build keeps a clean log; flip EnableDebugKeys on for a bug report.
                if (DebugTools.EnableDebugKeys)
                {
                    // OBSERVER (once per kidnap): the pair's relationship, the killer's den, the meet state.
                    try
                    {
                        if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap && _observed.Add(vid))
                        {
                            DebugTools.EnableGameVerboseLogging();
                            LogKidnapObservation(murder, killer, victim);
                            LogKidnapReloadDiag(murder, killer, victim);   // post-load state dump (diff vanilla vs ours to find what our setup fails to restore)
                        }
                    }
                    catch { }

                    // LIVE SAMPLER (throttled + capped): whether the victim/killer are actually at the den right
                    // now, and how far the victim is from it — so you can watch the meet->den walk seat.
                    try
                    {
                        bool kidnapActive = murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap
                            && (murder.state == MurderController.MurderState.waitForLocation
                                || murder.state == MurderController.MurderState.travellingTo
                                || murder.state == MurderController.MurderState.executing
                                || murder.state == MurderController.MurderState.post
                                || murder.state == MurderController.MurderState.escaping
                                || murder.state == MurderController.MurderState.unsolved);
                        if (kidnapActive)
                        {
                            float now = NowHours();
                            if (_liveVid != vid) { _liveVid = vid; _liveLastLogH = -999f; _liveCount = 0; }
                            if (_liveCount < 120 && now - _liveLastLogH >= 0.1f)
                            {
                                _liveLastLogH = now; _liveCount++;
                                LogKidnapLive(murder, killer, victim);
                            }
                        }
                    }
                    catch { }

                    // SNIPER OBSERVER (once) + LIVE SAMPLER — sniper is target-site-first: the game picks a
                    // sniperVictimSite (an exposed routine/public spot the victim visits), finds a vantage wall,
                    // then the killer travels there + shoots. Capture that flow (esp. on a VANILLA case, to learn
                    // the intended behaviour) plus whether a vantage exists for the killer+site — the leading
                    // suspect for why a motivated sniper loops in travellingTo instead of firing.
                    try
                    {
                        if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.sniper)
                        {
                            if (_sniperObserved.Add(vid))
                            {
                                DebugTools.EnableGameVerboseLogging();
                                LogSniperObservation(murder, killer, victim);
                            }
                            bool sniperActive = murder.state == MurderController.MurderState.waitForLocation
                                || murder.state == MurderController.MurderState.travellingTo
                                || murder.state == MurderController.MurderState.executing
                                || murder.state == MurderController.MurderState.post
                                || murder.state == MurderController.MurderState.escaping
                                || murder.state == MurderController.MurderState.unsolved;
                            if (sniperActive)
                            {
                                float snow = NowHours();
                                if (_sniperLiveVid != vid) { _sniperLiveVid = vid; _sniperLiveLastLogH = -999f; _sniperLiveCount = 0; }
                                if (_sniperLiveCount < 200 && snow - _sniperLiveLastLogH >= 0.05f)
                                {
                                    _sniperLiveLastLogH = snow; _sniperLiveCount++;
                                    LogSniperLive(murder, killer, victim);
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (!MurderSelector.OverriddenVictimIds.Contains(vid)) return;   // INTERVENTIONS below are OURS only — vanilla cases just get observed above

                // SNIPER SITE. Our pair-swap enters with sniperVictimSite == null, which strands the case (the game
                // locks the un-snipeable home and never re-targets), so on the first tick we give it a site. For an
                // ExCop case we PREFER a believable LOCAL nest (the "Daffodil Ward" pattern): pin the victim's HOME if
                // the killer has a reachable vantage over it, else the WORKPLACE, so the kill lands somewhere personal
                // to the pair instead of the city's single best-scoring rooftop. Local sites score far below that
                // global best, so the game would never pick them itself; a SET sniperVictimSite is honoured, so the pin
                // holds. If no local site is viable we fall to the game's own default (its best rooftop); and if a pin
                // never fires within [Troubleshooting] SniperPinStallHours (a solver false-positive), we RELEASE to the
                // default so a marginal pin can't hang. Voyeur cases just use the game's picker (home/work). All is
                // deferred to the game after seeding; then return so snipers skip the kidnap-oriented interventions.
                if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.sniper)
                {
                    bool isExCop = false; try { isExCop = murder.mo != null && !murder.mo.requiresSniperVantageAtHome; } catch { }
                    if (_sniperSeeded.Add(vid))
                    {
                        NewWall nestWall = null; List<NewNode> herdNodes = null;
                        NewGameLocation pin = isExCop ? FirstViableLocalSite(killer, victim, out nestWall, out herdNodes) : null;
                        if (pin != null)
                        {
                            try { murder.sniperVictimSite = pin; } catch (Exception se) { MotivesPlugin.Log.LogWarning($"[SODMotives][sniper] pin write err: {se.Message}"); }
                            _sniperPin[vid] = pin; _sniperPinSince[vid] = NowHours();
                            if (herdNodes != null && herdNodes.Count > 0) _sniperWander[vid] = herdNodes;   // the nodes the nest can see -- victim wanders among them
                            _moNestWall = nestWall; _moNestSitePtr = pin.Pointer;   // pre-warm the postfix cache so the killer travels to OUR verified nest
                            _moNestPhys = _lastNestWasPhysics; _moNestRevalidated = false;   // physics-rescued pins get re-validated once the killer arrives
                            MotivesPlugin.Log.LogInfo($"[SODMotives][sniper] pinned LOCAL site {LName(pin)} (a public nest with a verified shot overlooks the victim's home/work) for {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)}; killer travels to the nest, releases to the game's default if it stalls ({SniperPinStallHours:0.#}h).");
                        }
                        else
                        {
                            SeedSniperDefault(murder, killer, victim);
                        }
                    }
                    else if (_sniperPin.TryGetValue(vid, out var pinned) && pinned != null)
                    {
                        bool resolved = murder.state == MurderController.MurderState.executing
                            || murder.state == MurderController.MurderState.post
                            || murder.state == MurderController.MurderState.escaping
                            || murder.state == MurderController.MurderState.unsolved;
                        if (resolved)
                        {
                            ClearVictimSniperSiteGoal(victim, pinned);            // un-pin the victim (shot fired / resolving)
                            _sniperPin.Remove(vid); _sniperPinSince.Remove(vid); _sniperWander.Remove(vid); _sniperHerdNode.Remove(vid); _sniperWanderPick.Remove(vid);   // fired/resolved at the local site -- done
                            _moNestPhys = false;
                        }
                        else if (murder.state == MurderController.MurderState.waitForLocation
                                 && NowHours() - (_sniperPinSince.TryGetValue(vid, out var vsince) ? vsince : NowHours()) >= SniperVictimReachHours)
                        {
                            // Victim never reached the pinned site (still waitForLocation after SniperVictimReachHours):
                            // the herd can't get them there (restricted workplace off-shift / unreachable node). Don't
                            // let them sit frozen for the full stall -- un-pin (resume routine) and use the game default.
                            ClearVictimSniperSiteGoal(victim, pinned);
                            _sniperPin.Remove(vid); _sniperPinSince.Remove(vid); _sniperWander.Remove(vid); _sniperHerdNode.Remove(vid); _sniperWanderPick.Remove(vid);
                            _moNestPhys = false;
                            MotivesPlugin.Log.LogInfo($"[SODMotives][sniper] victim never reached pinned site {LName(pinned)} within {SniperVictimReachHours:0.#}h (still waitForLocation) -> releasing to the game's default site.");
                            SeedSniperDefault(murder, killer, victim);
                        }
                        else if (NowHours() - (_sniperPinSince.TryGetValue(vid, out var since) ? since : NowHours()) >= SniperPinStallHours)
                        {
                            ClearVictimSniperSiteGoal(victim, pinned);            // un-pin so the victim resumes routine + can reach the default site
                            _sniperPin.Remove(vid); _sniperPinSince.Remove(vid); _sniperWander.Remove(vid); _sniperHerdNode.Remove(vid); _sniperWanderPick.Remove(vid);
                            _moNestPhys = false;
                            MotivesPlugin.Log.LogInfo($"[SODMotives][sniper] local pin {LName(pinned)} stalled {SniperPinStallHours:0.#}h without a shot -> releasing to the game's default site.");
                            SeedSniperDefault(murder, killer, victim);
                        }
                        else if (_moNestPhys && !_moNestRevalidated && _moNestWall != null && _moNestSitePtr == pinned.Pointer
                                 && KillerAtNest(killer) && !PhysNestStillOverlooksSite(pinned))
                        {
                            // Physics pin, killer now IN POSITION, geometry loaded, yet the nest is actually blind to
                            // the site (a selection-time streaming false positive) -> release NOW, not after the stall.
                            _moNestRevalidated = true;
                            ClearVictimSniperSiteGoal(victim, pinned);
                            _sniperPin.Remove(vid); _sniperPinSince.Remove(vid); _sniperWander.Remove(vid); _sniperHerdNode.Remove(vid); _sniperWanderPick.Remove(vid);
                            _moNestPhys = false;
                            MotivesPlugin.Log.LogInfo($"[SODMotives][phys-los] killer reached {LName(NestLoc(_moNestWall))} but it has NO physics line to {LName(pinned)}'s windows (streaming false positive) -> releasing to the game's default site.");
                            SeedSniperDefault(murder, killer, victim);
                        }
                        else
                        {
                            if (_moNestPhys && !_moNestRevalidated && KillerAtNest(killer)) _moNestRevalidated = true;   // in position + still overlooks -> validated, don't re-check
                            // Keep the pin alive: re-assert the site so the game's state-4 dwell timeout can't re-target it
                            // to the global rooftop, and HERD the victim to the SPECIFIC node the nest can see (not an
                            // arbitrary spot -- that froze them away from the nest's sightline last time). Walking them into
                            // the window the killer is aimed at is the fix that works WITH the node-graph LOS instead of
                            // needing a nest that sees their natural spot.
                            try { var cur = murder.sniperVictimSite; if (cur == null || cur.Pointer != pinned.Pointer) murder.sniperVictimSite = pinned; } catch { }
                            // Wander target: rotate through the nest's visible nodes (~every 3 game-min) so the victim walks
                            // between in-sightline spots and eventually stands where the physics shot is actually clear.
                            // Wander target (two-tier). There are usually only ~2 window nodes with LOS, so a flat
                            // random pick just paces a straight A<->B line -- and if that line is furniture-blocked the
                            // physics shot never clears. Instead: a COARSE ~30s bucket picks an anchor window; within it
                            // a FINE ~10s bucket jitters among the nodes NEAR that anchor (<=7m). So the victim wanders
                            // AROUND one window for ~30s (stepping into physics-clear spots the node-graph missed), then
                            // switches to the other. Anchor stable within the coarse bucket so the walk actually settles.
                            NewNode herd = null;
                            if (_sniperWander.TryGetValue(vid, out var wl) && wl != null && wl.Count > 0)
                            {
                                int coarse = (int)(NowHours() / 0.00833f);   // ~30 game-sec: which window
                                int fine = (int)(NowHours() / 0.00417f);     // ~15 game-sec: jitter around it (a bit calmer)
                                if (!_sniperWanderPick.TryGetValue(vid, out var pick) || pick.Key != fine || pick.Value == null)
                                {
                                    // pick a stable anchor for the coarse bucket, then a nearby node for the fine bucket
                                    var anchor = wl[Math.Abs(coarse) % wl.Count];
                                    var nearby = new List<NewNode>();
                                    for (int k = 0; k < wl.Count; k++)
                                    { try { if (wl[k] != null && Vector3.Distance(wl[k].position, anchor.position) <= 3.5f) nearby.Add(wl[k]); } catch { } }
                                    if (nearby.Count == 0) nearby.Add(anchor);
                                    pick = new KeyValuePair<int, NewNode>(fine, nearby[_sniperRng.Next(nearby.Count)]);
                                    _sniperWanderPick[vid] = pick;
                                }
                                herd = pick.Value;
                            }
                            EnsureVictimSniperSiteGoal(victim, pinned, herd, murder, vid);
                        }
                    }
                    return;   // snipers defer entirely to the game; skip the kidnap-oriented interventions below
                }

                // KILL DEADLINE (our kidnaps only) = the game's OWN Murder.killTime — the value the ransom note
                // counts down to. We deliberately do NOT override killTime, so the note's stated kill time and the
                // actual kill MATCH. The problem our swap creates is only that the game flips kidnapKillPhase on
                // immediately (which suppresses the ransom-demand setup) and tries to kill BEFORE killTime is even
                // reached. So we (a) clear kidnapKillPhase until the deadline (so the ransom objectives get built),
                // and (b) block the kill's state transitions until then (ShouldBlockKidnapKill, same killTime gate).
                if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap)
                {
                    try
                    {
                        float kt = murder.killTime; float now2 = NowHours();
                        bool deadlineReached = kt > 0.5f && now2 >= kt;   // killTime set AND reached
                        if (!deadlineReached)
                        {
                            try { if (murder.kidnapKillPhase) murder.kidnapKillPhase = false; } catch { }
                            if (kt > 0.5f && _killTimeReasserted.Add(vid))
                                MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap-kill] holding the kidnapper off until the game's own killTime={kt:0.0} (now={now2:0.0}) so the kill matches the ransom note; victim held/rescuable until then.");
                        }
                    }
                    catch (Exception ke) { MotivesPlugin.Log.LogWarning($"[SODMotives][kidnap-kill] error: {ke.Message}"); }
                }

                // SAVE/LOAD FIX (B): keep the victim's GoTo-den goal alive during the PRE-RESTRAIN phases
                // (waitForLocation + travellingTo) so a reload can't strand them. DECODED (2026-09-24, side-by-side
                // reload logs): a VANILLA kidnap victim keeps a GoTo@den goal (pri10) through these phases and it
                // survives save/load; OUR swapped victim loses it on reload (currentGoal=null, all routine goals
                // pri0), so nothing pins them -> they wander out of the den -> the seat condition breaks -> the case
                // reverts to waitForLocation. Rebuild + pin the goal so the victim commits to the den like vanilla
                // (a WALK, not a teleport). Stops at 'executing' (the abduction then injects Flee@den, which pins them).
                if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap
                    && (murder.state == MurderController.MurderState.waitForLocation || murder.state == MurderController.MurderState.travellingTo))
                {
                    NewAddress vgDen = null; try { vgDen = killer.den; } catch { }
                    if (vgDen != null) EnsureVictimDenGoal(victim, vgDen, murder, vid);
                }

                // waitForLocation stall (a kidnap with no seatable holding 'den', a sniper with no vantage
                // site): this state has no natural resolution, so the case would hang forever. The victim WALKS
                // to the den via the game's own "GoTo den routine" walk goal, leaving the real meet->den trail
                // like vanilla (the picker hands us a walk-reachable den, so the walk seats on its own:
                // waitForLocation -> travellingTo). We don't cancel while it's still walking; a genuine stall
                // past WaitLocationStallHours falls through and cancels the case -> vanilla so the player is
                // never soft-locked. A one-time IsValidLocation probe is logged (dev-only) when it stalls.
                if (murder.state == MurderController.MurderState.waitForLocation)
                {
                    if (DebugTools.EnableDebugKeys && _probed.Add(vid)) ProbeValidLocations(murder, killer, victim);

                    if (_waitLocVictimId != vid) { _waitLocVictimId = vid; _waitLocSince = NowHours(); return; }
                    if (_waitRecovered.Contains(vid)) return;
                    float wstuck = NowHours() - _waitLocSince;
                    if (wstuck < WaitLocationStallHours) return;
                    if (DebugTools.EnableDebugKeys) LogKidnapState(murder, killer, victim, "stall");   // final state before we give up
                    try { murder.CancelCurrentMurder(); } catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] waitForLocation cancel error: {ce.Message}"); }
                    _waitRecovered.Add(vid);
                    MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] RECOVERED: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} stuck in 'waitForLocation' {wstuck:F1}h — the victim never WALKED into the den in time (den not walk-reachable, or the victim wouldn't attend the meet / path to the den) — cancelled so it can't hang.");
                    return;
                }

                // travellingTo (KILLER -> den): the killer WALKS to the den to carry out the abduction (vanilla).
                // Nothing to do — the game's own goal carries them there.
                if (murder.state == MurderController.MurderState.travellingTo
                    && murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap)
                    return;

                // RANSOM-NOTE spawn (fix): vanilla spawns a ransom note (a preset lead item tagged JobTag.U) at
                // the victim's home and registers it in murder.activeMurderItems[U]; TriggerKidnappingCase then
                // adds the "Examine the ransom note found at <home>" objective (the investigate-the-home lead),
                // and examining it drives the rest of the chain (-> TriggerRansomDelivery -> call/save
                // objectives). Our swapped/rushed case never spawns that note, so no examine-note objective ever
                // appears. Spawn it ourselves ONCE when the case goes live, register it, and let the game's own
                // objective chain proceed — do NOT pre-call TriggerRansomDelivery (that jumps ahead + creates the
                // downstream objectives out of order, which is what happened before).
                // TIMING: the game runs TriggerKidnappingCase at the 'escaping' transition and checks for the note
                // THEN, so it must already exist — spawn at the earlier 'post' (right after "knocked out and
                // restrained"), not at escaping/unsolved (a hair too late, so the objective was only created at the
                // KILL's later post). _ransomTried guards to once → only the abduction's post spawns it.
                if ((murder.state == MurderController.MurderState.post || murder.state == MurderController.MurderState.escaping || murder.state == MurderController.MurderState.unsolved)
                    && murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap)
                {
                    // Re-arm the hold marker every tick while held. It is normally set by the SetMurderState
                    // postfix on the transition INTO the hold, but that transition does NOT re-fire after a
                    // save/reload (the case loads already at post/escaping/unsolved), and the marker is in-memory
                    // and cleared on every game start (INCLUDING a load). Without this, the kill-block
                    // (ShouldBlockKidnapKill) never re-arms after a reload and a held victim could be killed
                    // before the ransom deadline. Setting it here (only in the hold states) restores the block on
                    // the first tick after load, and never mislabels the abduction's own pre-hold
                    // travellingTo/executing (those states do not enter this branch).
                    KidnapReachedHold.Add(vid);
                    if (KidnapRansomAssist && _ransomTried.Add(vid)) SpawnRansomNote(murder, killer, victim);
                    // Seal the den once the abduction is done (post+) AND the killer has physically LEFT it: CLOSE
                    // the front door(s) behind them (matches vanilla's default flee behaviour reliably), and LOCK
                    // only if KidnapLockDen (optional, beyond vanilla). Once per victim.
                    if (!_denSealed.Contains(vid))
                    {
                        NewAddress dl = null; try { dl = killer.den; } catch { }
                        bool killerGone = true;
                        try { var kl = killer.currentGameLocation; killerGone = !(kl != null && dl != null && kl.Pointer == dl.Pointer); } catch { }
                        if (dl != null && killerGone) SealDen(killer, dl, vid);
                    }
                    return;
                }

                // Phase B: we've force-killed the victim; flip the case to 'post' if it didn't self-advance.
                if (_awaitingPost == vid)
                {
                    if (murder.state != MurderController.MurderState.executing) { _awaitingPost = -1; return; }   // advanced on its own
                    if (IsDead(victim))
                    {
                        try { murder.SetMurderState(MurderController.MurderState.post, true); } catch { }
                        MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] forced 'post' after force-finishing {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} — vanilla evidence + motive clues now spawn.");
                        _awaitingPost = -1;
                    }
                    return;
                }

                if (murder.state != MurderController.MurderState.executing) return;
                // A KIDNAP in 'executing' resolves to knock-out + RESTRAINT, not death — never force-KILL a kidnap
                // victim (that would turn an abduction into a murder). The killer walked here on their own
                // (walk-native), so just let the game's own abduction run.
                if (murder.preset != null && murder.preset.caseType == MurderPreset.CaseType.kidnap)
                    return;
                if (_intervened.Contains(vid)) return;              // already handled this victim
                if (_watchVictimId != vid) { _watchVictimId = vid; _executingSince = NowHours(); return; }   // missed the entry — start timing now

                float stuckH = NowHours() - _executingSince;
                if (stuckH < StallGameHours) return;               // not stuck long enough yet

                // Genuine whiff-stall only: the victim's health must be essentially untouched. If damage
                // IS landing, the kill is progressing (just slowly) — leave it well alone.
                float hp = 1f; try { hp = victim.currentHealthNormalized; } catch { }
                if (hp < 0.9f) return;

                // CO-LOCATION GATE: the killer must be AT the victim, else force-killing would leave a
                // body with no coherent attacker/scene/spatter. If they can't reach each other, this is a
                // different stall — log once and leave it entirely to vanilla.
                if (!SameLocation(killer, victim))
                {
                    if (_loggedNoReach.Add(vid))
                        MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] STALL, NO INTERVENTION: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} stuck in 'executing' {stuckH:F1}h but the killer is NOT co-located ({LocName(killer)} vs {LocName(victim)}) — leaving to vanilla to avoid an evidence-less death.");
                    return;
                }

                // Land the killing blow the enactment failed to. Credits the killer + drops spatter at the
                // scene; vanilla death processing then runs, and the forced 'post' (Phase B) spawns the
                // usual evidence + our motive clues.
                Vector3 pos = Vector3.zero, dir = Vector3.zero;
                try { var n = victim.currentNode; if (n != null) pos = n.position; } catch { }
                try
                {
                    victim.RecieveDamage(9999f, killer, pos, dir, null, null, enableKill: true, forceRagdoll: true);
                }
                catch (Exception de)
                {
                    MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] force-finish RecieveDamage error: {de.Message}");
                }
                _intervened.Add(vid);
                _awaitingPost = vid;
                MotivesPlugin.Log.LogWarning($"[SODMotives][watchdog] INTERVENED: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} stalled in 'executing' {stuckH:F1}h (co-located at {LocName(victim)}, victim HP {hp:P0}); force-finished the kill.");
            }
            catch { }
        }

        // DIAGNOSTIC (dev-only): when an overridden case parks in waitForLocation, ask the game's OWN
        // Murder.IsValidLocation which places it would accept. Probes the killer/victim homes explicitly
        // (are they cohabiting? is a home a valid den?) and scans every city location to count how many
        // qualify right now. This reveals the real den predicate empirically — far more reliable than
        // decoding the 1300-call Update() by hand — and tells us how to constrain the kidnap victim pool.
        private static void ProbeValidLocations(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                NewGameLocation kh = null, vh = null;
                try { kh = killer.home; } catch { }
                try { vh = victim.home; } catch { }
                bool cohab = false; try { cohab = kh != null && vh != null && kh.Pointer == vh.Pointer; } catch { }
                string mo = "?"; try { if (murder.mo != null) mo = murder.mo.name; } catch { }
                string preset = "?"; try { if (murder.preset != null) preset = murder.preset.name; } catch { }
                log.LogInfo($"[SODMotives][den probe] {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)}  preset={preset} mo={mo}");
                log.LogInfo($"[SODMotives][den probe]   killer.home={LName(kh)} valid={ValidLoc(murder, kh)} ; victim.home={LName(vh)} valid={ValidLoc(murder, vh)} ; cohabiting={cohab}");

                var cd = CityData.Instance;
                var dir = cd != null ? cd.gameLocationDirectory : null;
                int total = 0, valid = 0; var sample = new List<string>();
                if (dir != null)
                    for (int i = 0; i < dir.Count; i++)
                    {
                        var loc = dir[i]; if (loc == null) continue;
                        total++;
                        bool ok = false; try { ok = murder.IsValidLocation(loc); } catch { }
                        if (ok) { valid++; if (sample.Count < 10) sample.Add(LName(loc)); }
                    }
                log.LogInfo($"[SODMotives][den probe]   IsValidLocation over {total} city locations -> {valid} VALID. first valid: {string.Join(" | ", sample)}");
                LogKidnapState(murder, killer, victim, "entry");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][den probe] error: {e.Message}"); }
        }

        // KIDNAP OBSERVER (dev-only): one-shot dump comparing a VANILLA kidnap to a MOD-FORCED one. The big
        // unknowns are (a) the killer<->victim RELATIONSHIP, (b) the DEN (killer's own home/property vs an
        // injected vacant unit), and (c) whether the victim attends the meet. Logs (a)+(b) here; (c) comes from
        // the game's own narration + the meet-state line. Read-only.
        private static void LogKidnapObservation(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID);
                log.LogInfo($"[SODMotives][kidnap-obs] ===== {(ours ? "MOD-FORCED" : "VANILLA")} KIDNAP: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} =====");
                log.LogInfo($"[SODMotives][kidnap-obs]   relationship killer->victim: {DescribeEdge(killer, victim)}");
                log.LogInfo($"[SODMotives][kidnap-obs]   relationship victim->killer: {DescribeEdge(victim, killer)}");
                NewAddress kden = null, khome = null, vhome = null;
                try { kden = killer.den; } catch { }
                try { khome = killer.home; } catch { }
                try { vhome = victim.home; } catch { }
                bool denIsKillerHome = false, denIsVictimHome = false;
                try { denIsKillerHome = kden != null && khome != null && kden.Pointer == khome.Pointer; } catch { }
                try { denIsVictimHome = kden != null && vhome != null && kden.Pointer == vhome.Pointer; } catch { }
                log.LogInfo($"[SODMotives][kidnap-obs]   killer.den={LName(kden)}  (killer.home={LName(khome)} denIsKillerHome={denIsKillerHome};  victim.home={LName(vhome)} denIsVictimHome={denIsVictimHome})");
                LogKidnapState(murder, killer, victim, "obs");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][kidnap-obs] error: {e.Message}"); }
        }

        // KIDNAP RELOAD DIAG (dev-only): dump the RESTORED state of a kidnap right after it's (re)observed —
        // fires once per game start incl. every load, so a save/reload captures exactly what the game rebuilt.
        // The executing-reload bug is OURS (vanilla keeps killer+victim at the den; ours sends both home), so we
        // diff this line between a VANILLA and an OURS mid-executing reload to find what our swapped setup fails
        // to restore. Prime suspect: murderGoal (the killer's abduction AI goal) + the actors' current AI goals.
        private static void LogKidnapReloadDiag(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = false; try { ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID); } catch { }
                string st = "?"; try { st = murder.state.ToString(); } catch { }
                string mg = "?"; try { mg = murder.murderGoal != null ? DescribeGoal(murder.murderGoal) : "NULL"; } catch { mg = "err"; }
                string g1 = "?", g2 = "?";
                try { g1 = murder.meetGoal1 != null ? "set" : "null"; } catch { }
                try { g2 = murder.meetGoal2 != null ? "set" : "null"; } catch { }
                string ids = "?";
                try { ids = $"victimID={murder.victimID} victimSiteID={murder.victimSiteID} victimSiteIsStreet={murder.victimSiteIsStreet} kidnapKillPhase={murder.kidnapKillPhase} killTime={murder.killTime:0.0} ransomPhase={murder.ransomPhase}"; } catch { ids = "err"; }
                log.LogInfo($"[SODMotives][kidnap-reload] {(ours ? "OURS" : "VANILLA")} state={st} murderGoal={mg} meetGoal1={g1} meetGoal2={g2} ; {ids}");
                log.LogInfo($"[SODMotives][kidnap-reload]   killer.ai: {DescribeActorGoals(killer)}");
                log.LogInfo($"[SODMotives][kidnap-reload]   victim.ai: {DescribeActorGoals(victim)}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][kidnap-reload] err: {e.Message}"); }
        }

        // Compact one-goal description: preset name @ target location (priority). Never throws.
        private static string DescribeGoal(NewAIGoal g)
        {
            try
            {
                if (g == null) return "null";
                string p = "?"; try { p = g.preset != null ? g.preset.name : "?"; } catch { }
                NewGameLocation gl = null; try { gl = g.gameLocation; } catch { }
                if (gl == null) { try { gl = g.passedGameLocation; } catch { } }
                float pr = 0f; try { pr = g.priority; } catch { }
                return $"{p}@{LName(gl)}(pri{pr:0})";
            }
            catch { return "err"; }
        }

        // Dump an actor's current AI goal + its goal list (compact) so we can see whether the abduction goals
        // (den-targeting) survived the reload, or whether the actor reverted to routine goals (home/needs).
        private static string DescribeActorGoals(Human h)
        {
            try
            {
                NewAIController ai = null; try { ai = h.ai; } catch { }
                if (ai == null) return "no ai";
                string cur = "?"; try { cur = ai.currentGoal != null ? DescribeGoal(ai.currentGoal) : "null"; } catch { }
                var parts = new List<string>();
                try { var goals = ai.goals; if (goals != null) for (int i = 0; i < goals.Count && i < 10; i++) { var g = goals[i]; if (g != null) parts.Add(DescribeGoal(g)); } } catch { }
                return $"currentGoal={cur} ; goals=[{string.Join(", ", parts.ToArray())}]";
            }
            catch { return "err"; }
        }

        // Describe the directed acquaintance edge a->b: connection types + like + known (or "NO EDGE").
        private static string DescribeEdge(Human a, Human b)
        {
            try
            {
                if (a == null || b == null) return "<null>";
                Acquaintance acq;
                if (!a.FindAcquaintanceExists(b, out acq) || acq == null) return "NO EDGE (strangers)";
                var parts = new List<string>();
                try { var conns = acq.connections; if (conns != null) for (int i = 0; i < conns.Count; i++) parts.Add(conns[i].ToString()); } catch { }
                string connStr = parts.Count > 0 ? string.Join(",", parts.ToArray()) : "(no connection types)";
                float like = float.NaN, known = float.NaN;
                try { like = acq.like; } catch { }
                try { known = acq.known; } catch { }
                return $"[{connStr}] like={like:0.00} known={known:0.00}";
            }
            catch { return "<err>"; }
        }

        // Kidnap abduction state — was the MEET set up (the thing that brings killer+victim together so the
        // killer can knock out + restrain + carry the victim to the den)? And where is the pair? This tells us
        // WHERE in the flow a stuck kidnap is stalling. Cheap; safe to call more than once.
        private static void LogKidnapState(MurderController.Murder murder, Human killer, Human victim, string tag)
        {
            var log = MotivesPlugin.Log;
            try
            {
                string mr = "<null>"; int mrid = -1;
                try { if (murder.meetRestaurant != null) mr = murder.meetRestaurant.name; } catch { }
                try { mrid = murder.meetRestaurantID; } catch { }
                bool g1 = false, g2 = false;
                try { g1 = murder.meetGoal1 != null; } catch { }
                try { g2 = murder.meetGoal2 != null; } catch { }
                string loc = "<null>"; try { if (murder.location != null) loc = murder.location.name; } catch { }
                string kloc = "?", vloc = "?";
                try { if (killer.currentGameLocation != null) kloc = killer.currentGameLocation.name; } catch { }
                try { if (victim.currentGameLocation != null) vloc = victim.currentGameLocation.name; } catch { }
                NewAddress den = null; try { den = killer.den; } catch { }
                log.LogInfo($"[SODMotives][kidnap {tag}]   meet: restaurant={mr}(id={mrid}) goal1set={g1} goal2set={g2}; location={loc}; killer.den={LName(den)}; killer@{kloc}; victim@{vloc}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][kidnap {tag}] state log error: {e.Message}"); }
        }

        // LIVE SAMPLE (dev-only): one line capturing whether the victim (and killer) are ACTUALLY at the den
        // right now, plus how far the victim is from it and whether the den's safe-teleport spot is inside the
        // den. If VICTIM_AT_DEN is never true across the whole stall, the "GoTo den routine" walk isn't landing.
        private static void LogKidnapLive(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = false; try { ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID); } catch { }
                string st = "?"; try { st = murder.state.ToString(); } catch { }
                string rp = "?"; try { rp = murder.ransomPhase.ToString(); } catch { }
                string kt = "?"; try { kt = $"killTime={murder.killTime:0.0} now={NowHours():0.0} killPhase={murder.kidnapKillPhase}"; } catch { }
                NewAddress den = null; try { den = killer.den; } catch { }
                NewGameLocation vloc = null, kloc = null;
                try { vloc = victim.currentGameLocation; } catch { }
                try { kloc = killer.currentGameLocation; } catch { }
                bool atDen = false, kAtDen = false;
                try { atDen = vloc != null && den != null && vloc.Pointer == den.Pointer; } catch { }
                try { kAtDen = kloc != null && den != null && kloc.Pointer == den.Pointer; } catch { }
                float dist = -1f;
                try
                {
                    var vn = victim.currentNode; var da = den != null ? den.anchorNode : null;
                    if (vn != null && da != null) dist = Vector3.Distance(vn.position, da.position);
                }
                catch { }
                string tp = den != null ? DescribeDenTeleport(victim, den) : "den=NULL";
                log.LogInfo($"[SODMotives][kidnap-live] {(ours ? "OURS" : "VANILLA")} [{st}] ransomPhase={rp} {kt} VICTIM_AT_DEN={atDen} victim@{LName(vloc)} ; KILLER_AT_DEN={kAtDen} killer@{LName(kloc)} ; den={LName(den)} ; dist(victim->den.anchor)={dist:0.0} ; {tp}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][kidnap-live] err: {e.Message}"); }
        }

        // SNIPER OBSERVER (dev-only, one-shot): compare a VANILLA sniper to a MOD-FORCED one — the pair's
        // relationship, the killer archetype (MO) + its site/vantage flags, homes, and the initial target site +
        // whether a vantage wall exists for it. Read-only.
        private static void LogSniperObservation(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID);
                log.LogInfo($"[SODMotives][sniper-obs] ===== {(ours ? "MOD-FORCED" : "VANILLA")} SNIPER: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} =====");
                log.LogInfo($"[SODMotives][sniper-obs]   relationship killer->victim: {DescribeEdge(killer, victim)}");
                log.LogInfo($"[SODMotives][sniper-obs]   relationship victim->killer: {DescribeEdge(victim, killer)}");
                string preset = "?"; try { if (murder.preset != null) preset = murder.preset.name; } catch { }
                string mo = "?"; try { if (murder.mo != null) mo = murder.mo.name; } catch { }
                string moFlags = "?";
                try { var m = murder.mo; if (m != null) moFlags = $"requiresSniperVantageAtHome={m.requiresSniperVantageAtHome} allow home/work/public/streets/anywhere={m.allowHome}/{m.allowWork}/{m.allowPublic}/{m.allowStreets}/{m.allowAnywhere}"; } catch { }
                log.LogInfo($"[SODMotives][sniper-obs]   preset={preset} mo={mo} ; {moFlags}");
                NewAddress kh = null, vh = null; try { kh = killer.home; } catch { } try { vh = victim.home; } catch { }
                log.LogInfo($"[SODMotives][sniper-obs]   killer.home={LName(kh)} ; victim.home={LName(vh)}");
                NewGameLocation site = null; try { site = murder.sniperVictimSite; } catch { }
                NewGameLocation loc = null; try { loc = murder.location; } catch { }
                NewGameLocation probeSite = site != null ? site : loc;   // VoyeurSniper leaves sniperVictimSite null and shoots the victim at murder.location (their home)
                log.LogInfo($"[SODMotives][sniper-obs]   sniperVictimSite={LName(site)} murder.location={LName(loc)} ; siteVantage[{LName(probeSite)}]: {DescribeSniperVantage(killer, probeSite)}");
                log.LogInfo($"[SODMotives][sniper-obs]   victim.home={LName(vh)} ; homeVantage: {DescribeSniperVantage(killer, vh)}");
                NewGameLocation vwork = null; try { var job = victim.job; var emp = job != null ? job.employer : null; if (emp != null) vwork = emp.placeOfBusiness; } catch { }
                log.LogInfo($"[SODMotives][sniper-obs]   victim.work={LName(vwork)} ; workVantage: {DescribeSniperVantage(killer, vwork)}");
                log.LogInfo($"[SODMotives][sniper-obs]   {DescribeSniperWeapon(murder)}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][sniper-obs] error: {e.Message}"); }
        }

        // SNIPER LIVE SAMPLE (dev-only, throttled): where the target site is, whether the victim is AT it, where
        // the killer is (and distances), the resolved kill-shot node, and whether a vantage wall exists for the
        // killer+site RIGHT NOW. If the site keeps changing / no vantage is ever found, that is the travellingTo
        // re-pick loop; if a vantage exists but the killer never reaches it, it is a travel/positioning problem.
        private static void LogSniperLive(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                bool ours = false; try { ours = MurderSelector.OverriddenVictimIds.Contains(victim.humanID); } catch { }
                string st = "?"; try { st = murder.state.ToString(); } catch { }
                NewGameLocation site = null; try { site = murder.sniperVictimSite; } catch { }
                NewGameLocation loc = null; try { loc = murder.location; } catch { }
                NewGameLocation probeSite = site != null ? site : loc;   // real target site (VoyeurSniper uses murder.location, not sniperVictimSite)
                NewGameLocation vhome = null; try { vhome = victim.home; } catch { }
                NewGameLocation vloc = null, kloc = null;
                try { vloc = victim.currentGameLocation; } catch { }
                try { kloc = killer.currentGameLocation; } catch { }
                bool vAtSite = false; try { vAtSite = vloc != null && probeSite != null && vloc.Pointer == probeSite.Pointer; } catch { }
                float vDist = -1f, kDist = -1f, kvDist = -1f;
                var sa = probeSite != null ? SafeAnchor(probeSite) : null;
                try { var vn = victim.currentNode; if (vn != null && sa != null) vDist = Vector3.Distance(vn.position, sa.position); } catch { }
                try { var kn = killer.currentNode; if (kn != null && sa != null) kDist = Vector3.Distance(kn.position, sa.position); } catch { }
                try { var kn = killer.currentNode; var vn = victim.currentNode; if (kn != null && vn != null) kvDist = Vector3.Distance(kn.position, vn.position); } catch { }
                string shot = "?"; try { shot = murder.sniperKillShotNode.ToString(); } catch { }
                log.LogInfo($"[SODMotives][sniper-live] {(ours ? "OURS" : "VANILLA")} [{st}] site={LName(probeSite)}(vs={LName(site)}/loc={LName(loc)}) victim@{LName(vloc)} V_AT_SITE={vAtSite} dist(victim->site)={vDist:0.0} ; killer@{LName(kloc)} dist(killer->site)={kDist:0.0} dist(killer->victim)={kvDist:0.0} ; killShotNode={shot} ; siteVantage={DescribeSniperVantage(killer, probeSite)} ; homeVantage={DescribeSniperVantage(killer, vhome)} ; {DescribeSniperWeapon(murder)}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][sniper-live] err: {e.Message}"); }
        }

        // Does a vantage wall exist for this killer to shoot the given target site? Uses the game's OWN solver
        // (Toolbox.TryGetSniperVantagePoint, target-site-first). Read-only; never throws.
        private static string DescribeSniperVantage(Human killer, NewGameLocation site)
        {
            try
            {
                if (killer == null || site == null) return "vantage: killer/site null";
                var tb = Toolbox.Instance; if (tb == null) return "vantage: no Toolbox";
                float score = 0f; NewWall wall = null; bool found;
                try { found = tb.TryGetSniperVantagePoint(killer, site, out wall, out score); }
                catch (Exception e) { return "vantage: TryGetSniperVantagePoint threw: " + e.Message; }
                if (!found) return "vantage=NONE (no wall covers this killer+site)";
                string nestLoc = "?"; try { var nn = wall != null ? wall.node : null; var gl = nn != null ? nn.gameLocation : null; if (gl != null) nestLoc = gl.name; } catch { }
                return $"vantage=FOUND score={score:0.00} nest@{nestLoc}";
            }
            catch (Exception e) { return "vantage: err " + e.Message; }
        }

        // Sniper WEAPON/equipment probe (dev-only, OURS-diagnosis aid): did the killer actually acquire a rifle?
        // The acquireEquipment phase sets Murder.acquiredEquipment and fills Murder.weapon (the real Interactable);
        // weaponPreset is the intended weapon. A sniper that reaches travellingTo has PASSED acquire, so this should
        // read acquired=True with a real held weapon — if it does NOT, an acquire failure (not geometry) is the loop
        // cause, and the fix is different. Read-only; never throws.
        private static string DescribeSniperWeapon(MurderController.Murder murder)
        {
            try
            {
                if (murder == null) return "weapon: murder null";
                bool acquired = false; try { acquired = murder.acquiredEquipment; } catch { }
                string preset = "?"; try { var wp = murder.weaponPreset; preset = wp != null ? wp.name : "<null>"; } catch { }
                string held = "?"; try { var w = murder.weapon; held = w != null ? w.name : "<none>"; } catch { }
                string wstr = "?"; try { wstr = string.IsNullOrEmpty(murder.weaponStr) ? "<empty>" : murder.weaponStr; } catch { }
                return $"weapon: acquired={acquired} preset={preset} held={held} weaponStr='{wstr}'";
            }
            catch (Exception e) { return "weapon: err " + e.Message; }
        }

        // The loaded sniper MO of a given vantage flavour: homeVantage=true -> the home-voyeur MO (VoyeurSniper,
        // requiresSniperVantageAtHome), homeVantage=false -> the street/rooftop MO (ExCopSniper). The override sets
        // the case's MO to match whether SniperVoyeurViable found a killer-home vantage, so the killer shoots from
        // home (voyeur) or travels to a public nest (street) as appropriate. Cached; per-MO score boosts (a
        // "Retired" job boost on ExCopSniper) are only preferences, so assigning either to any motivated killer is fine.
        private static MurderMO _homeSniperMO, _streetSniperMO; private static bool _sniperMOsSearched;
        internal static MurderMO SniperMO(bool homeVantage)
        {
            if (!_sniperMOsSearched)
            {
                _sniperMOsSearched = true;
                try
                {
                    var mos = Resources.FindObjectsOfTypeAll<MurderMO>();
                    if (mos != null)
                        for (int i = 0; i < mos.Length; i++)
                        {
                            var mo = mos[i]; if (mo == null) continue;
                            var compat = mo.compatibleWith; if (compat == null) continue;
                            bool sniper = false;
                            for (int j = 0; j < compat.Count; j++) { var p = compat[j]; if (p != null && p.caseType == MurderPreset.CaseType.sniper) { sniper = true; break; } }
                            if (!sniper) continue;
                            if (mo.requiresSniperVantageAtHome) { if (_homeSniperMO == null) _homeSniperMO = mo; }
                            else { if (_streetSniperMO == null) _streetSniperMO = mo; }
                        }
                }
                catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] SniperMO scan error: {e.Message}"); }
            }
            return homeVantage ? _homeSniperMO : _streetSniperMO;
        }

        private static NewNode SafeAnchor(NewGameLocation l) { try { return l != null ? l.anchorNode : null; } catch { return null; } }

        // Seal the den behind the fleeing killer: CLOSE the front door(s) — matches vanilla's default (the killer
        // shuts doors on the way out; our swapped killer sometimes left them open). LOCK them too only if
        // KidnapLockDen (optional, beyond vanilla — vanilla leaves the unowned den unlocked). `entrances` are the
        // address's outer doors; the interior (bathroom) door isn't in this list, so it's untouched. Once per victim.
        private static void SealDen(Human killer, NewAddress den, int vid)
        {
            try
            {
                if (den == null) return;
                if (!_denSealed.Add(vid)) return;   // once per victim
                int closed = 0, locked = 0;
                var ents = den.entrances;
                if (ents != null)
                    for (int i = 0; i < ents.Count; i++)
                    {
                        var e = ents[i]; if (e == null) continue;
                        NewDoor d = null; try { d = e.door; } catch { }
                        if (d == null) continue;
                        try { d.SetOpen(0f, killer, true); closed++; } catch { }   // close it (skip animation)
                        if (KidnapLockDen) { try { d.SetLocked(true, killer, false); locked++; } catch { } }
                    }
                MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap-seal] {MotivesPlugin.Name(killer)} closed {closed}{(KidnapLockDen ? $" + locked {locked}" : " (unlocked, like vanilla)")} front door(s) at {LName(den)}.");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][kidnap-seal] err: {e.Message}"); }
        }

        // SAVE/LOAD FIX (B): give the kidnap victim the same persistent "GoTo the den" goal a VANILLA victim has
        // during the pre-restrain phases. Vanilla's victim carries a GoTo@den goal (pri10) through waitForLocation
        // + travellingTo that survives save/load; ours loses it on reload, so nothing pins them and they wander
        // out of the den. We find that goal (present in normal play) or, if it's gone (after a reload), recreate it
        // from the game's own "go to" preset (RoutineControls.toGoGoal) targeting the den, then pin it on top so
        // the victim commits to walking to / staying in the den. Re-asserted each tick (the AI recomputes
        // priority). This is a WALK, not a teleport — the physical trail is unchanged. Never throws.
        private static void EnsureVictimDenGoal(Human victim, NewAddress den, MurderController.Murder murder, int vid)
        {
            try
            {
                if (victim == null || den == null) return;
                NewAIController ai = null; try { ai = victim.ai; } catch { }
                if (ai == null) return;

                // Find an existing goal already targeting the den (the game's own GoTo-den routine in normal play).
                NewAIGoal denGoal = null;
                try
                {
                    var goals = ai.goals;
                    if (goals != null)
                        for (int i = 0; i < goals.Count; i++)
                        {
                            var g = goals[i]; if (g == null) continue;
                            NewGameLocation gl = null, pg = null;
                            try { gl = g.gameLocation; } catch { }
                            try { pg = g.passedGameLocation; } catch { }
                            if ((gl != null && gl.Pointer == den.Pointer) || (pg != null && pg.Pointer == den.Pointer)) { denGoal = g; break; }
                        }
                }
                catch { }

                // Missing (typically after a reload) -> recreate it from the game's own "go to" goal preset.
                if (denGoal == null)
                {
                    AIGoalPreset preset = null; try { preset = RoutineControls.Instance != null ? RoutineControls.Instance.toGoGoal : null; } catch { }
                    if (preset == null) return;
                    try { denGoal = ai.CreateNewGoal(preset, NowHours(), 999f, null, null, den, null, murder, -2); }
                    catch (Exception ce) { MotivesPlugin.Log.LogWarning($"[SODMotives][kidnap-hold] CreateNewGoal(GoTo den) threw: {ce.Message}"); return; }
                    if (denGoal != null && DebugTools.EnableDebugKeys && _denGoalLogged.Add(vid))
                        MotivesPlugin.Log.LogInfo($"[SODMotives][kidnap-hold] rebuilt victim GoTo-den goal for {MotivesPlugin.Name(victim)} -> {LName(den)} (save/load recovery; matches vanilla's persistent GoTo@den).");
                }
                if (denGoal == null) return;

                // Pin it on top so the victim commits to the den (re-asserted each tick; the AI recomputes priority).
                try { denGoal.basePriority = 100000f; } catch { }
                try { denGoal.priority = 100000f; } catch { }
                try { denGoal.isActive = true; } catch { }
                try { ai.currentGoal = denGoal; } catch { }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][kidnap-hold] EnsureVictimDenGoal err: {e.Message}"); }
        }

        // Spawn the vanilla ransom-note lead item (the preset lead tagged JobTag.U) and register it in
        // murder.activeMurderItems[U], so the game's TriggerKidnappingCase adds the "Examine the ransom note found
        // at <home>" objective for our swapped-in victim (it's gated on that dict entry existing). Replicates
        // exactly what the game's own SpawnItemsCheck would have done for a kidnap victim it set up itself.
        private static void SpawnRansomNote(MurderController.Murder murder, Human killer, Human victim)
        {
            var log = MotivesPlugin.Log;
            try
            {
                // Already registered (game spawned it, or we already did)? Leave it.
                try { if (murder.activeMurderItems != null && murder.activeMurderItems.ContainsKey(JobPreset.JobTag.U)) { log.LogInfo("[SODMotives][ransom] ransom note (tag U) already present; leaving it."); return; } } catch { }

                var preset = murder.preset; if (preset == null) { log.LogWarning("[SODMotives][ransom] preset null; can't spawn note."); return; }
                var leads = preset.leads; if (leads == null) { log.LogWarning("[SODMotives][ransom] preset.leads null; can't spawn note."); return; }

                int noteIdx = -1; var tags = new List<string>();
                for (int i = 0; i < leads.Count; i++)
                {
                    var ld = leads[i]; if (ld == null) continue;
                    try { tags.Add(ld.itemTag.ToString()); } catch { }
                    try { if (ld.itemTag == JobPreset.JobTag.U) { noteIdx = i; break; } } catch { }
                }
                if (noteIdx < 0)
                {
                    log.LogWarning($"[SODMotives][ransom] no lead tagged U (ransom note) in preset '{SafePresetName(preset)}'. Lead tags present: {string.Join(",", tags.ToArray())}");
                    return;
                }

                var note = leads[noteIdx];
                Interactable item = null;
                try
                {
                    item = MurderController.Instance.SpawnItem(murder, note.spawnItem, note.where, note.belongsTo, note.writer, note.receiver, note.security, note.ownershipRule, note.priority, note.itemTag);
                }
                catch (Exception se) { log.LogWarning($"[SODMotives][ransom] SpawnItem(ransom note) threw: {se.Message}"); return; }
                if (item == null) { log.LogWarning("[SODMotives][ransom] SpawnItem(ransom note) returned null."); return; }

                try { if (murder.activeMurderItems != null && !murder.activeMurderItems.ContainsKey(JobPreset.JobTag.U)) murder.activeMurderItems.Add(JobPreset.JobTag.U, item); }
                catch (Exception ae) { log.LogWarning($"[SODMotives][ransom] registering note in activeMurderItems threw: {ae.Message}"); }

                string pn = "?"; try { pn = note.spawnItem != null ? note.spawnItem.name : "?"; } catch { }
                log.LogInfo($"[SODMotives][ransom] spawned ransom note (tag U, preset '{pn}') for {MotivesPlugin.Name(victim)} + registered in activeMurderItems. TriggerKidnappingCase should now add the 'Examine the ransom note' objective; examining it drives the rest of the chain.");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][ransom] SpawnRansomNote err: {e.Message}"); }
        }

        private static string SafePresetName(MurderPreset p) { try { return p != null ? p.name : "?"; } catch { return "?"; } }

        // Decode-backed helper (dev diagnostic): the game seats a kidnap only when the VICTIM is inside
        // murderer.den, and it gets them there via victim.FindSafeTeleport(den) -> walk. This reports whether the
        // safe-teleport spot is inside the den and whether the den is WALK-reachable (allowTrespass=false), which
        // is the property that distinguishes a walk-in vacant unit from a locked basement. Read-only; never throws.
        internal static string DescribeDenTeleport(Human victim, NewAddress den)
        {
            try
            {
                if (victim == null || den == null) return "victim/den null";
                NewNode node = null;
                try { node = victim.FindSafeTeleport(den, false, true); }
                catch (Exception e) { return "FindSafeTeleport threw: " + e.Message; }
                if (node == null) return "FST-node=NULL (no safe teleport spot found at all)";
                NewGameLocation nodeLoc = null; try { nodeLoc = node.gameLocation; } catch { }
                bool inside = false; try { inside = nodeLoc != null && nodeLoc.Pointer == den.Pointer; } catch { }
                Vector3 npos = default; try { npos = node.position; } catch { }
                // allowTrespass=FALSE = can the victim reach a node inside the den by NORMAL pathing (no
                // trespassing through locked/forbidden doors)? If this is false/outside while the trespass one is
                // inside, the den is walk-UNREACHABLE for the victim (why the "GoTo den" walk goal stalls).
                NewNode walkNode = null; try { walkNode = victim.FindSafeTeleport(den, false, false); } catch { }
                NewGameLocation walkLoc = null; try { walkLoc = walkNode != null ? walkNode.gameLocation : null; } catch { }
                bool walkInside = false; try { walkInside = walkLoc != null && walkLoc.Pointer == den.Pointer; } catch { }
                string walk = walkNode == null ? "WALK_REACHABLE=NULL" : $"WALK_REACHABLE_loc={LName(walkLoc)} WALK_INSIDE_DEN={walkInside}";
                // IsPublicallyOpen(forPlayer=false): is the den enterable by an NPC without keys/trespass?
                string openness = ""; try { openness = $" ; NPC_OPEN={den.IsPublicallyOpen(false)}"; } catch { openness = " ; NPC_OPEN=?"; }
                return $"FST-node.loc={LName(nodeLoc)} INSIDE_DEN={inside} node.pos={npos} ; {walk}{openness}";
            }
            catch (Exception e) { return "DescribeDenTeleport err: " + e.Message; }
        }

        private static bool ValidLoc(MurderController.Murder m, NewGameLocation l)
        { if (l == null) return false; try { return m.IsValidLocation(l); } catch { return false; } }

        private static string LName(NewGameLocation l) { try { return l != null ? l.name : "<null>"; } catch { return "?"; } }

        private static bool IsDead(Human h) { try { return h.isDead; } catch { return false; } }

        // True if the two are at the same murder scene (same game-location, or at least the same room),
        // i.e. the killer is present with the victim — the only situation in which force-finishing yields
        // a coherent crime scene.
        private static bool SameLocation(Human a, Human b)
        {
            try
            {
                var la = a.currentGameLocation; var lb = b.currentGameLocation;
                if (la != null && lb != null && la.Pointer == lb.Pointer) return true;
            }
            catch { }
            try
            {
                var ra = a.currentRoom; var rb = b.currentRoom;
                if (ra != null && rb != null && ra.Pointer == rb.Pointer) return true;
            }
            catch { }
            return false;
        }

        private static string LocName(Human h)
        {
            try { var l = h.currentGameLocation; if (l != null && !string.IsNullOrEmpty(l.name)) return l.name; } catch { }
            try { var b = h.currentBuilding; if (b != null) return "bldg " + b.buildingID; } catch { }
            return "?";
        }
    }
}

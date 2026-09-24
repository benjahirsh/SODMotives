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

        // Always-answer HUD readout: the last NPC who spoke to the player (so you can always identify who you're
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

        // RELEASE GATE (D2): master switch for the developer tooling. Bound to [Debug] EnableDebugKeys
        // (default FALSE for a shipped build). When OFF, every dev hotkey below is inert, the on-screen
        // FORCE/GHOST/ALWAYS-ANSWER indicators are hidden, and the verbose per-case log dumps are silenced —
        // only the release-safe F9 diagnostics view (case type + murder state + injected clues) and the
        // gameplay stall-watchdog keep running. Flip it on (in the config overlay) to get the full test loop
        // back for debugging or a bug report.
        internal static bool EnableDebugKeys = false;

        // F9 detail toggle: when true, the case-solution overlay ALSO lists the full SUSPECT POOL and the
        // victim/killer KNOWER interview lists. Default false = F9 shows just the core solution (case type,
        // state, killer, victim, scene, motive, injected clues). Bound to [Debug] ShowSuspectPoolAndKnowers;
        // independent of EnableDebugKeys.
        internal static bool ShowSuspectPoolAndKnowers = false;

        // TESTING accelerator ([Troubleshooting] FastMurderCadence): while ON, forces
        // MurderController.pauseBetweenMurders to 0 so the game does NOT wait between murders — subsequent
        // cases schedule back-to-back. (Verified in-game the field's natural value is 0, so 0 is the safe
        // "fastest" setting; a positive value would only ADD a gap and, if the field is a countdown, could
        // stall the loop.) NOTE this compresses the pause BETWEEN murders — it does NOT speed the current
        // murder's planning/enactment (murderer research -> weapon -> opportunity), nor force a sniper case.
        // Self-heals every frame; restores the original when turned OFF. Independent of EnableDebugKeys.
        internal static bool FastMurderCadence = false;
        private static bool _pauseCaptured;
        private static float _origPause;

        // Configurable debug hotkeys — bound to the [Debug Keys] config section in Plugin.Load and
        // re-applied live from the in-game overlay (which renders KeyCode as a key-binder). Defaults are
        // the original F-keys (F5 left free for the game's quicksave + the config-menu toggle).
        internal static KeyCode KeyCaseSolution   = KeyCode.F9;
        internal static KeyCode KeyCycleForce     = KeyCode.F6;
        internal static KeyCode KeyTestAccess     = KeyCode.F7;   // MERGED test toggle: ghost + always-answer together (used as a pair)
        internal static KeyCode KeyTeleportScene  = KeyCode.F10;
        internal static KeyCode KeyTeleportMeet   = KeyCode.F11;   // teleport to the kidnap MEETING location (was: victim's work)
        internal static KeyCode KeyTeleportVictim = KeyCode.F12;   // teleport to the VICTIM's CURRENT position (tail them)
        internal static KeyCode KeyTriggerMurder  = KeyCode.F4;   // force the game's next murder NOW (fast test loop)
        internal static KeyCode KeyTeleportKiller = KeyCode.F8;   // teleport to the KILLER's CURRENT position (tail them)
        internal static KeyCode KeyForceSniper    = KeyCode.F3;   // CREATE a motivated sniper case immediately (bypasses the scheduler)
        internal static KeyCode KeyTeleportCityHall = KeyCode.Home;   // teleport to City Hall (fixed landmark)
        internal static KeyCode KeyTimeBoost      = KeyCode.End;   // toggle fast-forward for testing
        private static bool _timeBoost = false;
        private static float _simBase = 0f;   // currentTimeMultiplier captured at the game's 'simulation' speed
        // EXTRA multiple applied ON TOP of the game's fastest built-in speed ('simulation'), by pushing the
        // game's OWN currentTimeMultiplier (NOT Time.timeScale — that brute-forces full-detail updates and
        // tanks the frame rate, running SLOWER). 1 = just use simulation speed. Configurable
        // ([Troubleshooting] TimeBoostMultiplier). The game may clamp/ignore values it can't keep up with.
        internal static float TimeBoostMultiplier = 2f;

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
                if (EnableDebugKeys)
                    MotivesPlugin.Log.LogInfo("[SODMotives] Debug keys ON (defaults; rebindable in the config menu -> SOD Motives / Debug Keys): F3=FORCE a motivated SNIPER case now, F4=trigger next murder NOW, F6=cycle FORCE EVENT (off/affair/promotion/layoffs/eviction/rentarrears/feud/debt), F7=TEST ACCESS (ghost + always-answer together), F8=teleport to nearest KILLER-knower, F9=case solution overlay, F10=teleport to scene, F11=to kidnap MEETING location, F12=to VICTIM's home, Home=teleport to City Hall, End=toggle fast-forward (simulation speed). (F1 reserved by the game; F2 + F5 unbound.)");
                else
                    MotivesPlugin.Log.LogInfo($"[SODMotives] Debug tooling OFF. {KeyCaseSolution} = case-solution overlay is always available (set it to None in [Debug Keys] to disable); enable [Debug] EnableDebugKeys in the config overlay for the full test loop (force event, teleports, ghost, etc.).");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] hotkey register failed: {e.Message}"); }
        }

        // TESTING: set the player's money to a high value so a ransom can be paid immediately. Fired when test
        // access (F7) turns on. Never throws.
        internal static void GiveTestCash(int amount = 10000)
        {
            try
            {
                var gc = GameplayController.Instance;
                if (gc != null) { gc.SetMoney(amount); MotivesPlugin.Log.LogInfo($"[SODMotives] TEST CASH: set player money to {amount} (so you can pay a ransom)."); }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] GiveTestCash error: {e.Message}"); }
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
                // Let the player into echelon (high-security) zones so you can knock on tenants' doors.
                EnsureEchelonAccess(true);
            }
            catch { }
        }

        internal static void ClearGhost()
        {
            try { var p = Player.Instance; if (p != null) { try { p.unreportable = false; } catch { } } } catch { }
            EnsureEchelonAccess(false);
        }

        // Grant/revoke ECHELON-ZONE ACCESS the same way the game's "allowedInEchelons" sync disk does: the
        // echelon gate queries UpgradeEffectController for that passive effect, so we inject our OWN
        // AppliedEffect into Instance.appliedEffects while ghost is on and remove exactly it when ghost is
        // off — never touching a real installed sync-disk effect. Self-heals if the effect list is rebuilt.
        // (If the player installs/uninstalls a real sync disk mid-ghost the list rebuilds; the next frame
        // re-adds our grant.)
        private static UpgradeEffectController.AppliedEffect _echelonGrant;
        private static void EnsureEchelonAccess(bool on)
        {
            try
            {
                var uec = UpgradeEffectController.Instance;
                if (uec == null) return;
                var list = uec.appliedEffects;
                if (list == null) return;
                if (on)
                {
                    bool present;
                    try { present = _echelonGrant != null && list.Contains(_echelonGrant); }
                    catch { present = _echelonGrant != null; }
                    if (present) return;
                    var eff = new UpgradeEffectController.AppliedEffect();
                    try { eff.effect = SyncDiskPreset.Effect.allowedInEchelons; } catch { }
                    try { eff.value = 1f; } catch { }
                    list.Add(eff);
                    _echelonGrant = eff;
                    MotivesPlugin.Log.LogInfo("[SODMotives] ghost: granted echelon access (allowedInEchelons effect).");
                }
                else if (_echelonGrant != null)
                {
                    try { list.Remove(_echelonGrant); } catch { }
                    _echelonGrant = null;
                    MotivesPlugin.Log.LogInfo("[SODMotives] ghost: revoked echelon access.");
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives] ghost: echelon access {(on ? "grant" : "revoke")} error: {e.Message}"); }
        }

        // F4: force the game to run its next murder NOW (testing) — combine with F6=<type> to get that
        // case fast instead of waiting for / re-rolling sandboxes. Our override still gates on proc-gen +
        // caseType==murder, so a triggered kidnap/sniper is left to vanilla.
        internal static void TriggerMurder()
        {
            var log = MotivesPlugin.Log;
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) { log.LogInfo("[SODMotives][F4] no MurderController."); return; }
                log.LogInfo($"[SODMotives][F4] triggering next murder (Force={ForceLabel()})...");
                mc.TriggerNextMurder();
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][F4] trigger error: {e.Message}"); }
        }

        // F3 (testing): CREATE a motivated sniper case RIGHT NOW, bypassing the game's slow scheduler.
        // Confirmed via Cpp2IL that ExecuteNewMurder(murderer, victim, preset, mo, site) is the universal
        // murder-creation entry point (it just builds the Murder + enables its update loop; the scheduler's
        // Tick calls the exact same thing). So we grab a loaded sniper preset+MO, pick a motivated pair with
        // the mod's own selector, and call ExecuteNewMurder directly. The override skips sniper, so our pair
        // passes through untouched — this tests whether a MOTIVATED sniper actually executes or stalls at
        // waitForLocation (the vantage/target-site question). Generalised to any case type for kidnap later.
        internal static void ForceSniperCase() => ForceCase(MurderPreset.CaseType.sniper, "F3");

        // Turn ON the game's OWN verbose murder logging so it narrates the kidnap flow ("Murder: Completing
        // meet goal 1", "Victim is knocked out and restrained", etc.) into the log. Those messages call
        // Game.Log(msg, level=2), gated ONLY by Game.Instance.printDebug + debugPrintLevel >= 2 (the spammy
        // per-human logs have their own separate debugHuman* gates, left off, so this narrates the murder
        // without flooding). Session-persistent; a game restart resets it. This is our window into exactly how
        // vanilla drives the meet + abduction.
        // Config toggle: keep the game's verbose murder logging on (applied every frame while true), so a
        // VANILLA kidnap gets narrated from the very start (before it even appears). Turn on to observe.
        internal static bool GameVerboseLogging = false;
        private static bool _verboseLogged = false;

        internal static void EnableGameVerboseLogging()
        {
            try
            {
                var g = Game.Instance;
                if (g == null) return;   // game not loaded yet; a per-frame caller will retry
                g.printDebug = true;
                if (g.debugPrintLevel < 2) g.debugPrintLevel = 2;
                if (!_verboseLogged)
                {
                    _verboseLogged = true;
                    MotivesPlugin.Log.LogInfo($"[SODMotives][gamelog] enabled the game's own verbose murder logging (printDebug=true, debugPrintLevel={g.debugPrintLevel}). NOTE: the 'Murder:' flow prints to the game's OWN log at %USERPROFILE%\\AppData\\LocalLow\\ColePowered Games\\Shadows of Doubt\\Player.log — NOT the BepInEx log. Restart to silence.");
                }
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][gamelog] error: {e.Message}"); }
        }

        // Dump the case-defining fields of a preset/MO so a playtest reveals what a kidnap actually requires
        // (den? where can it happen? occupancy caps? research/acquire phases?). These are ScriptableObject
        // asset data — only readable at runtime — so logging them here is how we learn the real requirements.
        private static void LogCaseAssetDetails(MurderPreset p, MurderMO mo, string tag)
        {
            var log = MotivesPlugin.Log;
            try
            {
                log.LogInfo($"[SODMotives][force:{tag}] preset '{(p != null ? p.name : "<null>")}': caseType={p?.caseType}, pickDen={p?.pickDen}, blockVictimFromLeavingLocation={p?.blockVictimFromLeavingLocation}, killerMeetsVicim={p?.killerMeetsVicim}, requiresResearchPhase={p?.requiresResearchPhase}, requiresAcquirePhase={p?.requiresAcquirePhase}, nonHomeMaxOccupants trigger/cancel={p?.nonHomeMaximumOccupantsTrigger}/{p?.nonHomeMaximumOccupantsCancel}");
                log.LogInfo($"[SODMotives][force:{tag}] MO '{(mo != null ? mo.name : "<null>")}': allow home/work/public/streets/den/anywhere = {mo?.allowHome}/{mo?.allowWork}/{mo?.allowPublic}/{mo?.allowStreets}/{mo?.allowDen}/{mo?.allowAnywhere}, requiresSniperVantageAtHome={mo?.requiresSniperVantageAtHome}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][force:{tag}] asset-detail dump error: {e.Message}"); }
        }

        internal static void ForceCase(MurderPreset.CaseType caseType, string tag)
        {
            var log = MotivesPlugin.Log;
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) { log.LogInfo($"[SODMotives][force:{tag}] no MurderController."); return; }

                // A loaded MO + one of its compatible presets of the requested case type. Assets stay loaded
                // even when the sandbox toggle for that type is OFF, so this works regardless of settings.
                // For SNIPER, PREFER a street MO (requiresSniperVantageAtHome == false) so F3 tests the
                // rooftop/public pattern that works for any pair, not the home-voyeur MO (VoyeurSniper) that
                // needs a rare home-to-home vantage and force-resolves a nonsensical kill without one. Keep the
                // first matching MO as a fallback if no street MO exists.
                MurderMO useMo = null; MurderPreset usePreset = null;
                MurderMO fbMo = null; MurderPreset fbPreset = null;
                var mos = Resources.FindObjectsOfTypeAll<MurderMO>();
                if (mos != null)
                    for (int i = 0; i < mos.Length && useMo == null; i++)
                    {
                        var mo = mos[i]; if (mo == null) continue;
                        var compat = mo.compatibleWith; if (compat == null) continue;
                        for (int j = 0; j < compat.Count; j++)
                        {
                            var p = compat[j];
                            if (p == null || p.caseType != caseType) continue;
                            if (fbMo == null) { fbMo = mo; fbPreset = p; }
                            bool prefer = caseType != MurderPreset.CaseType.sniper || !mo.requiresSniperVantageAtHome;
                            if (prefer) { useMo = mo; usePreset = p; break; }
                        }
                    }
                if (useMo == null) { useMo = fbMo; usePreset = fbPreset; }
                if (useMo == null || usePreset == null)
                { log.LogInfo($"[SODMotives][force:{tag}] no {caseType} preset/MO found among loaded assets."); return; }

                // Log the case-defining asset fields (den? occupancy caps? phases?) — the playtest signal.
                LogCaseAssetDetails(usePreset, useMo, tag);

                // A motivated (killer -> victim) pair from the mod's own selector (also marks the victim as
                // ours, so F9 shows the motive). No motivated pair -> abort with a clear note.
                if (!MurderSelector.TryPickVictimCentric(out Human killer, out Human victim, out var pool)
                    || killer == null || victim == null)
                { log.LogInfo($"[SODMotives][force:{tag}] selector found no motivated (killer->victim) pair."); return; }

                mc.currentMurderer = killer; mc.currentVictim = victim;
                // A kidnap hangs at waitForLocation unless the killer has a den (IsValidLocation accepts only
                // murderer.den). Assign one before creating the case so it can seat its holding location.
                if (caseType == MurderPreset.CaseType.kidnap) MurderSelector.EnsureKidnapDen(killer, victim, useMo);
                log.LogInfo($"[SODMotives][force:{tag}] forcing {caseType.ToString().ToUpper()}: {MotivesPlugin.Name(killer)} -> {MotivesPlugin.Name(victim)} (preset={usePreset.name}, mo={useMo.name}). Watch F9 MURDER STATE: reaches 'executing'/'post' = WORKS; stuck at 'waitForLocation' = site/vantage problem.");
                var murder = mc.ExecuteNewMurder(killer, victim, usePreset, useMo, null);
                log.LogInfo($"[SODMotives][force:{tag}] ExecuteNewMurder -> {(murder != null ? "Murder created" : "null")}.");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][force:{tag}] error: {e.Message}"); }
        }

        // DEV one-shot: enumerate every LOADED MurderMO compatible with a SNIPER preset, with the flags that decide
        // its behaviour (requiresSniperVantageAtHome = shoot the victim at HOME vs a routine/public site; the allow
        // location flags). Answers "what sniper archetypes exist, and which are home-vantage?" — the input to the
        // vantage-viable constraint. Deduped by asset name (FindObjectsOfTypeAll can return multiple copies). Called
        // once per game start from the OnStartGame seed hook, gated behind EnableDebugKeys.
        internal static void DumpSniperMOs()
        {
            var log = MotivesPlugin.Log;
            try
            {
                var mos = Resources.FindObjectsOfTypeAll<MurderMO>();
                if (mos == null || mos.Length == 0) { log.LogInfo("[SODMotives][mo-dump] no MurderMO assets loaded yet."); return; }
                var seen = new System.Collections.Generic.HashSet<string>();
                int count = 0;
                for (int i = 0; i < mos.Length; i++)
                {
                    var mo = mos[i]; if (mo == null) continue;
                    var compat = mo.compatibleWith; if (compat == null) continue;
                    bool isSniper = false; string presets = "";
                    for (int j = 0; j < compat.Count; j++)
                    {
                        var p = compat[j]; if (p == null) continue;
                        if (presets.Length > 0) presets += ",";
                        presets += p.name + "(" + p.caseType + ")";
                        if (p.caseType == MurderPreset.CaseType.sniper) isSniper = true;
                    }
                    if (!isSniper) continue;
                    string nm = mo.name ?? "<null>";
                    if (!seen.Add(nm)) continue;   // dedupe repeated copies of the same asset
                    count++;
                    log.LogInfo($"[SODMotives][mo-dump]   SNIPER MO '{nm}': requiresSniperVantageAtHome={mo.requiresSniperVantageAtHome} ; allow home/work/public/streets/den/anywhere={mo.allowHome}/{mo.allowWork}/{mo.allowPublic}/{mo.allowStreets}/{mo.allowDen}/{mo.allowAnywhere} ; compatibleWith=[{presets}]");
                }
                log.LogInfo($"[SODMotives][mo-dump] {count} distinct sniper-compatible MO(s) loaded.");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][mo-dump] error: {e.Message}"); }
        }

        // Testing accelerator: while FastMurderCadence is on, hold MurderController.pauseBetweenMurders at 0
        // so the game never waits between murders (subsequent cases chain immediately). Called every frame;
        // captures the original on first apply and restores it when toggled off. No-op when off.
        internal static void ApplyFastCadence()
        {
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) return;
                if (FastMurderCadence)
                {
                    float cur = 0f; try { cur = mc.pauseBetweenMurders; } catch { }
                    if (!_pauseCaptured)
                    {
                        _origPause = cur; _pauseCaptured = true;
                        MotivesPlugin.Log.LogInfo($"[SODMotives] FAST CADENCE ON — pauseBetweenMurders was {cur:0.##}, forcing 0 (no gap between murders). NOTE: this does not speed the CURRENT murder's planning, nor force a sniper. Turn off to restore.");
                    }
                    if (cur != 0f)
                        try { mc.pauseBetweenMurders = 0f; } catch { }
                }
                else if (_pauseCaptured)
                {
                    try { mc.pauseBetweenMurders = _origPause; } catch { }
                    MotivesPlugin.Log.LogInfo($"[SODMotives] FAST CADENCE OFF — pauseBetweenMurders restored to {_origPause:0.##}.");
                    _pauseCaptured = false;
                }
            }
            catch { }
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

        // Teleport to the kidnap MEETING location — the public spot (restaurant) the killer lures the victim to
        // before the abduction. This is the first leg we want to make match vanilla (walked, physical trail), so
        // being able to jump there and watch it happen is the key dev aid for the meet work.
        internal static void TeleportToMeet()
        {
            var log = MotivesPlugin.Log;
            try
            {
                var mc = MurderController.Instance;
                var m = mc != null ? mc.GetCurrentMurder() : null;
                if (m == null) { log.LogInfo("[SODMotives][F11] No active case."); return; }
                NewGameLocation meet = null;
                try { meet = m.meetRestaurant; } catch { }
                if (meet == null) { log.LogInfo("[SODMotives][F11] No meeting location (meetRestaurant) set for this case."); return; }
                var player = Player.Instance;
                NewNode node = player.FindSafeTeleport(meet, false, true);
                if (node == null) { log.LogInfo($"[SODMotives][F11] No safe teleport spot at the meeting location {meet.name}."); return; }
                player.Teleport(node, null, true, false, true);
                log.LogInfo($"[SODMotives][F11] Teleported to the meeting location: {meet.name}");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][F11] meet teleport error: {e}"); }
        }

        // F12: teleport to the VICTIM's CURRENT position (tail them — where they are right now, not their home).
        internal static void TeleportToVictim()
        {
            try
            {
                if (!TryGetCase(out _, out Human victim, out _) || victim == null)
                { MotivesPlugin.Log.LogInfo("[SODMotives][F12] No victim."); return; }
                TeleportToActorCurrent(victim, "F12", "victim");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][F12] victim teleport error: {e}"); }
        }

        // Teleport the player to an actor's CURRENT position: their live node if available, else a safe spot in
        // their current location. For tailing the killer/victim during a case.
        private static void TeleportToActorCurrent(Human who, string tag, string label)
        {
            var log = MotivesPlugin.Log;
            var player = Player.Instance;
            if (player == null) { log.LogInfo($"[SODMotives][{tag}] No player."); return; }
            NewGameLocation loc = null; try { loc = who.currentGameLocation; } catch { }
            NewNode node = null; try { node = who.currentNode; } catch { }
            if (node == null && loc != null) { try { node = player.FindSafeTeleport(loc, false, true); } catch { } }
            if (node == null) { log.LogInfo($"[SODMotives][{tag}] No current position for the {label} ({MotivesPlugin.Name(who)})."); return; }
            player.Teleport(node, null, true, false, true);
            string locName = "?"; try { if (loc != null) locName = loc.name; } catch { }
            log.LogInfo($"[SODMotives][{tag}] Teleported to the {label}'s current position: {MotivesPlugin.Name(who)} @ {locName}");
        }

        // Teleport to City Hall — a fixed central landmark (handy when a case's scene is unresolved, e.g. a
        // kidnap stuck at waitForLocation with no den). Scans the city's locations for one named "City Hall".
        internal static void TeleportToCityHall()
        {
            var log = MotivesPlugin.Log;
            log.LogInfo("[SODMotives][cityhall] key pressed — attempting City Hall teleport…");
            try
            {
                var cd = CityData.Instance; var player = Player.Instance;
                if (cd == null || player == null) { log.LogInfo("[SODMotives][cityhall] No city/player."); return; }
                var dir = cd.gameLocationDirectory;
                if (dir == null) { log.LogInfo("[SODMotives][cityhall] No location directory."); return; }
                // A city has several "City Hall" locations (building, lobby, floors); some sub-locations (e.g.
                // the lobby) have no anchorNode and FindSafeTeleport returns null. Try EVERY match and use the
                // first that yields a reachable node (FindSafeTeleport -> anchorNode -> its own node list ->
                // a room's centre node).
                for (int i = 0; i < dir.Count; i++)
                {
                    var loc = dir[i]; if (loc == null) continue;
                    string n = null; try { n = loc.name; } catch { }
                    if (string.IsNullOrEmpty(n) || n.IndexOf("City Hall", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    NewNode node = NodeForLocation(player, loc);
                    if (node == null) continue;
                    player.Teleport(node, null, true, false, true);
                    log.LogInfo($"[SODMotives][cityhall] Teleported to {n}.");
                    return;
                }
                log.LogInfo("[SODMotives][cityhall] Found City Hall but no location had a reachable node to teleport to.");
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][cityhall] teleport error: {e}"); }
        }

        // Best-effort reachable node for a location: safe-teleport spot, else the anchor, else the location's
        // own nodes, else a room's centre node. Returns null only if the location truly has no nodes.
        private static NewNode NodeForLocation(Player player, NewGameLocation loc)
        {
            NewNode node = null;
            try { node = player.FindSafeTeleport(loc, false, true); } catch { }
            if (node == null) { try { node = loc.anchorNode; } catch { } }
            if (node == null) { try { var ns = loc.nodes; if (ns != null && ns.Count > 0) node = ns[0]; } catch { } }
            return node;
        }

        // Toggle fast-forward for testing. ON sets the game's fastest built-in speed ('simulation') and, if
        // TimeBoostMultiplier > 1, pushes the game's OWN currentTimeMultiplier to that multiple of simulation's
        // base each frame (ApplyTimeBoost). This uses the game's efficient fast-sim path rather than
        // Time.timeScale (which overloaded physics and ran slower). OFF restores normal speed.
        internal static void ToggleTimeBoost()
        {
            var log = MotivesPlugin.Log;
            try
            {
                var sd = SessionData.Instance;
                if (sd == null) { log.LogInfo("[SODMotives][time] No SessionData."); return; }
                _timeBoost = !_timeBoost;
                try { Time.timeScale = 1f; } catch { }   // undo any prior Time.timeScale experiment
                if (_timeBoost)
                {
                    try { sd.SetTimeSpeed(SessionData.TimeSpeed.simulation); } catch { }
                    _simBase = 0f; try { _simBase = sd.currentTimeMultiplier; } catch { }
                    log.LogInfo($"[SODMotives][time] Fast-forward ON — game 'simulation' speed (base multiplier x{_simBase:0.##}); pushing to x{(_simBase * TimeBoostMultiplier):0.##} (TimeBoostMultiplier {TimeBoostMultiplier:0.#}). Press again for normal. (Time.timeScale intentionally NOT used — it ran slower.)");
                }
                else
                {
                    _simBase = 0f;
                    try { sd.SetTimeSpeed(SessionData.TimeSpeed.normal); } catch { }
                    log.LogInfo("[SODMotives][time] Fast-forward OFF — normal speed.");
                }
            }
            catch (Exception e) { log.LogWarning($"[SODMotives][time] error: {e}"); }
        }

        // While ON, keep pushing the game's own currentTimeMultiplier up to (simulation base x multiplier),
        // re-asserting it each frame in case the game recomputes it. Never touches a pause (multiplier ~0).
        // No-op when off, when the multiplier is 1, or if we couldn't capture a base. Called every frame.
        internal static void ApplyTimeBoost()
        {
            try
            {
                if (!_timeBoost || TimeBoostMultiplier <= 1.01f || _simBase <= 0.01f) return;
                var sd = SessionData.Instance;
                if (sd == null) return;
                float target = _simBase * TimeBoostMultiplier;
                float cur = 0f; try { cur = sd.currentTimeMultiplier; } catch { return; }
                if (cur > 0.01f && cur < target - 0.01f) { try { sd.currentTimeMultiplier = target; } catch { } }
            }
            catch { }
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
            // The FULL case-solution overlay — always the same pane whether or not [Debug] EnableDebugKeys is
            // on (that flag gates the OTHER dev hotkeys, not this one). A player who doesn't want a solution
            // pane can set the CaseSolutionOverlay key to None in the config overlay to disable it.
            try
            {
                if (!TryGetCase(out Human killer, out Human victim, out string scene))
                {
                    Overlay.Add("No active murder case right now.");
                    return;
                }

                bool ours = false;
                try { ours = victim != null && MurderSelector.OverriddenVictimIds.Contains(victim.humanID); } catch { }

                // Grab the live Murder once: its preset gives the MURDER TYPE (murder / sniper / kidnap),
                // shown next to whether the mod overrode it; its state drives the progress line below.
                MurderController.Murder mrd = null;
                try { mrd = MurderController.Instance != null ? MurderController.Instance.GetCurrentMurder() : null; } catch { }
                string murderType = "?";
                try { if (mrd != null && mrd.preset != null) murderType = mrd.preset.caseType.ToString(); } catch { }
                Overlay.Add((ours ? "CASE TYPE: motivated (mod)" : "CASE TYPE: vanilla / not overridden") + $"  [{murderType}]");
                try { if (mrd != null) Overlay.Add($"MURDER STATE: {mrd.state}"); } catch { }

                Overlay.Add($"KILLER: {MotivesPlugin.Name(killer)}  @ {HomeName(killer)}");
                Overlay.Add($"VICTIM: {MotivesPlugin.Name(victim)}  @ {HomeName(victim)}");
                Overlay.Add($"SCENE : {scene}");
                // Meeting location (kidnap): the public spot the killer lures the victim to before the abduction.
                // F11 teleports here (see TeleportToMeet). Also show the killer's den (the hold location) if set.
                try
                {
                    if (mrd != null && mrd.preset != null && mrd.preset.caseType == MurderPreset.CaseType.kidnap)
                    {
                        string meet = "<none set>"; try { if (mrd.meetRestaurant != null) meet = mrd.meetRestaurant.name; } catch { }
                        Overlay.Add($"MEET  : {meet}   (F11 to teleport)");
                        NewAddress denAddr = null; string den = "<none>";
                        try { if (killer != null) denAddr = killer.den; if (denAddr != null) den = denAddr.name; } catch { }
                        Overlay.Add($"DEN   : {den}");
                        // WALK-NATIVE watch: is the den walk-reachable, and is the victim walking into it? (dist to
                        // the den anchor should shrink to ~0 as the victim walks the meet->den leg, then victim@den
                        // flips true and the case seats — the real trail, no teleport). Lets you watch it live on F9.
                        if (denAddr != null && victim != null)
                        {
                            bool walkOk = false; try { walkOk = MurderWatchdog.KidnapDenWalkReachable(victim, denAddr); } catch { }
                            bool atDen = false; try { var vl = victim.currentGameLocation; atDen = vl != null && vl.Pointer == denAddr.Pointer; } catch { }
                            float dist = -1f; try { var vn = victim.currentNode; var da = denAddr.anchorNode; if (vn != null && da != null) dist = Vector3.Distance(vn.position, da.position); } catch { }
                            Overlay.Add($"        walk-reachable={walkOk}  victim@den={atDen}  dist={(dist < 0 ? "?" : dist.ToString("0") + "m")}");
                        }
                    }
                }
                catch { }

                // MOTIVE + SUSPECT POOL read the REAL event-backed pool the killer was drawn from
                // (MurderSelector), not the retired like-based Motive.Score.
                if (victim != null && MurderSelector.MotiveByVictim.TryGetValue(victim.humanID, out var kmot))
                    Overlay.Add($"MOTIVE: [{kmot.type}] {kmot.detail} (score {MotivesPlugin.F(kmot.score)})");
                else if (killer != null && victim != null)
                    Overlay.Add("MOTIVE: (vanilla / not overridden)");

                if (victim != null)
                {
                    // SUSPECT POOL — the full event-backed breakdown (with the killer marked *) is extra
                    // detail, shown only when [Debug] ShowSuspectPoolAndKnowers is on.
                    if (ShowSuspectPoolAndKnowers)
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
                    }

                    // Injected clues for this case (did our clues spawn, and where?) — always shown.
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

                    // Interview lists — NPCs who can name the victim/killer AND know a motive event about
                    // them (F12 / F8 jump to the closest when debug keys are on). Nearest the scene first.
                    // Extra detail, shown only when ShowSuspectPoolAndKnowers is on.
                    if (ShowSuspectPoolAndKnowers)
                    {
                        try { AddVictimKnowers(victim, ScenePos(victim)); } catch { }
                        try { AddKillerKnowers(killer, ScenePos(victim)); } catch { }
                    }
                }
            }
            catch (Exception e) { Overlay.Add($"error: {e.Message}"); log.LogWarning($"[SODMotives][F9] {e}"); }

            // Mirror to log too.
            log.LogInfo("[SODMotives][F9] ---- case solution ----");
            foreach (var l in Overlay) log.LogInfo("[SODMotives][F9]   " + l);
        }

        // Home address name for the F9 panel (shown next to the killer/victim names so the tester can jump there).
        private static string HomeName(Human h)
        {
            try { return h != null && h.home != null ? h.home.name : "?"; }
            catch { return "?"; }
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

        // KILLER-side interview list — the mirror of AddVictimKnowers: every NPC who KNOWS THE KILLER'S
        // NAME (can identify the face-only body profile) AND knows a motive event involving the killer, so
        // (shown the killer's photo) they'll name them AND volunteer gossip. Lets you test killer-side
        // gossip, not just victim-side. Nearest the scene first (F8 = jump to closest).
        private static void AddKillerKnowers(Human killer, Vector3 scenePos)
        {
            if (killer == null) return;
            int kid = killer.humanID;

            // events involving the killer, tallied per knower and per type (participants aren't knowers).
            var perKnower = new Dictionary<int, Dictionary<SocialEventType, int>>();
            foreach (var e in EventStore.All)
            {
                if (!e.InvolvesHuman(kid)) continue;
                foreach (var knid in e.knownBy)
                {
                    if (e.InvolvesHuman(knid)) continue;
                    if (!perKnower.TryGetValue(knid, out var m)) { m = new Dictionary<SocialEventType, int>(); perKnower[knid] = m; }
                    m.TryGetValue(e.type, out int c); m[e.type] = c + 1;
                }
            }

            var dir = CityData.Instance != null ? CityData.Instance.citizenDirectory : null;
            var rows = new List<(float d, string line)>();
            if (dir != null)
                for (int i = 0; i < dir.Count; i++)
                {
                    var h = dir[i]; if (h == null) continue;
                    if (!perKnower.TryGetValue(h.humanID, out var m)) continue;   // knows no event about the killer
                    if (!Motive.KnowsName(h, killer)) continue;                    // can't name the killer -> skip
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
            Overlay.Add("KILLER KNOWERS — know the killer + a motive event (F8 = jump to closest):");
            if (rows.Count == 0) Overlay.Add("  (nobody both knows the killer's name AND a motive event about them)");
            for (int i = 0; i < rows.Count && i < 8; i++) Overlay.Add("  " + rows[i].line);
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

        // F8: teleport to the KILLER's CURRENT position (tail them — where they are right now).
        internal static void TeleportToKiller()
        {
            try
            {
                if (!TryGetCase(out Human killer, out _, out _) || killer == null)
                { MotivesPlugin.Log.LogInfo("[SODMotives][F8] No killer."); return; }
                TeleportToActorCurrent(killer, "F8", "killer");
            }
            catch (Exception e) { MotivesPlugin.Log.LogWarning($"[SODMotives][F8] killer teleport error: {e}"); }
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
                // Release-safe diagnostics key — ALWAYS available (trimmed unless dev tooling is on).
                // Configurable via [Debug Keys] CaseSolutionOverlay (DebugTools.KeyCaseSolution).
                if (Input.GetKeyDown(DebugTools.KeyCaseSolution))
                {
                    if (DebugTools.Show) { DebugTools.Show = false; }
                    else { DebugTools.BuildSolution(); DebugTools.Show = true; }
                }

                // Everything below is developer tooling — gated behind [Debug] EnableDebugKeys (default OFF).
                if (DebugTools.EnableDebugKeys)
                {
                    if (Input.GetKeyDown(DebugTools.KeyCycleForce)) DebugTools.CycleForceMotive();
                    if (Input.GetKeyDown(DebugTools.KeyTriggerMurder)) DebugTools.TriggerMurder();
                    if (Input.GetKeyDown(DebugTools.KeyForceSniper)) DebugTools.ForceSniperCase();
                    if (Input.GetKeyDown(DebugTools.KeyTeleportCityHall)) DebugTools.TeleportToCityHall();
                    if (Input.GetKeyDown(DebugTools.KeyTimeBoost)) DebugTools.ToggleTimeBoost();
                    if (Input.GetKeyDown(DebugTools.KeyTeleportScene)) DebugTools.TeleportToScene();
                    if (Input.GetKeyDown(DebugTools.KeyTeleportMeet)) DebugTools.TeleportToMeet();
                    if (Input.GetKeyDown(DebugTools.KeyTeleportVictim)) DebugTools.TeleportToVictim();
                    if (Input.GetKeyDown(DebugTools.KeyTeleportKiller)) DebugTools.TeleportToKiller();
                    // MERGED test toggle (one key flips ghost + always-answer together — they're always used
                    // as a pair): walk freely (NPCs ignore you) AND interrogate anyone without a bribe.
                    if (Input.GetKeyDown(DebugTools.KeyTestAccess))
                    {
                        bool on = !DebugTools.Ghost;   // both track together; use Ghost as the shared state
                        DebugTools.Ghost = on;
                        DebugTools.AlwaysAnswer = on;
                        if (!on) DebugTools.ClearGhost();
                        if (on) DebugTools.GiveTestCash();   // top up money so you can pay a ransom immediately
                        MotivesPlugin.Log.LogInfo($"[SODMotives] TEST ACCESS {(on ? "ON" : "OFF")}: ghost (NPCs ignore you) + always-answer (no bribe) + test cash, toggled together.");
                    }
                    if (DebugTools.Ghost) DebugTools.ApplyGhost();
                }

                // Gameplay feature (NOT debug) — recover a mod motive-murder stuck in 'executing' (killer
                // whiffing forever). Scoped to our overridden cases + co-located killer only; no-op
                // otherwise. Always runs. See MurderWatchdog.
                MurderWatchdog.Tick();

                // Testing accelerator (config-gated, default off; independent of EnableDebugKeys) — hold the
                // murder cadence low while on, restore on off. Self-gates on FastMurderCadence.
                DebugTools.ApplyFastCadence();

                // Testing fast-forward: re-assert Time.timeScale multiplier while ON (never overrides a pause).
                DebugTools.ApplyTimeBoost();

                // Keep the game's verbose murder logging on while the toggle is set (retries until the game
                // loads Game.Instance), so a VANILLA kidnap is narrated from the start.
                if (DebugTools.GameVerboseLogging) DebugTools.EnableGameVerboseLogging();
            }
            catch { }
        }

        void OnGUI()
        {
            // TOP-RIGHT status area, right-aligned, stacked top-down with no gaps.
            try
            {
                if (_hudStyle == null)
                    _hudStyle = new GUIStyle { fontSize = 14, wordWrap = false, alignment = TextAnchor.UpperRight };

                const float rm = 8f, w = 760f;
                float x = Screen.width - w - rm;
                float y = 8f;

                if (DebugTools.EnableDebugKeys)
                {
                    // Dev-tooling indicators (F6 force / F7 test access = ghost + always-answer) — only while
                    // dev tooling is enabled. Each is shown only when its state is active, so a clean screen means "off".
                    bool fm = DebugTools.ForceEventType.HasValue || DebugTools.ForceMotiveType != MotiveType.None;
                    if (fm)
                    {
                        _hudStyle.normal.textColor = Color.yellow;
                        GUI.Label(new Rect(x, y, w, 20), $"FORCE: {DebugTools.ForceLabel()} (F6)", _hudStyle);
                        y += 20f;
                    }

                    if (DebugTools.AlwaysAnswer)
                    {
                        _hudStyle.normal.textColor = Color.cyan;
                        GUI.Label(new Rect(x, y, w, 20), "ALWAYS-ANSWER ON (F7) - NPCs never refuse 'do you know this person?'", _hudStyle);
                        y += 20f;
                        if (!string.IsNullOrEmpty(DebugTools.TalkingToLabel))
                        {
                            GUI.Label(new Rect(x, y, w, 20), "TALKING TO: " + DebugTools.TalkingToLabel, _hudStyle);
                            y += 20f;
                        }
                    }

                    if (DebugTools.Ghost)
                    {
                        _hudStyle.normal.textColor = Color.green;
                        GUI.Label(new Rect(x, y, w, 20), "GHOST MODE ON (F7) - NPCs ignore you", _hudStyle);
                        y += 20f;
                    }
                }
                // No persistent on-screen hint in release: the case-solution key (F9) is discoverable and
                // disable-able in the config overlay's [Debug Keys] section, so the screen stays clean.
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
                GUI.Label(new Rect(18, 14, w - 20, lineH), $"SOD MOTIVES — case solution ({DebugTools.KeyCaseSolution} to hide)", _style);
                for (int i = 0; i < count; i++)
                    GUI.Label(new Rect(18, 14 + lineH * (i + 1.4f), w - 20, lineH), DebugTools.Overlay[i], _style);
            }
            catch { }
        }
    }
}

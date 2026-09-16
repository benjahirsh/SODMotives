# A3 recon — in-game menu config

**Branch:** `v2` · **Date:** 2026-09-16 · Recon-only (no code). Companion to `docs/pre-release-plan.md`
(item **A3**) and the auto-loaded memory. **Unlike A6, this needs NO game-symbol recon** — A3 lives
entirely in the BepInEx/our-config layer, so there's nothing to `ilspycmd`/Cpp2IL against
`Assembly-CSharp.dll`. Facts below are verified against our own source (cited by line) and the external
libs' docs/repos (cited at the end).

## Goal
Let a player change the mod's knobs from **inside the game**, not by hand-editing
`BepInEx/config/com.benhirsh.sodmotives.cfg` and restarting. Ideally every existing `ConfigEntry` is
menu-exposed before Phase D (per the plan). Decide **dependency vs roll-our-own**, then wire it.

---

## Current state (the thing we're surfacing)
- **~26 knobs**, all bound in `MotivesPlugin.Load` (`Plugin.cs:28-97`) across sections General /
  Selection / Flavour / Workplace / Property / Feud / Clues.
- **THE pivotal fact:** every bind is `Config.Bind(...).Value` — we **take `.Value` and throw the
  `ConfigEntry<T>` away**, copying the value into a plain `static` field (`MurderSelector.EnableOverride`,
  `ClueInjector.MaxClues`, …) read **once at load**. Grep confirms **zero** retained `ConfigEntry<>`
  refs and **zero** `SettingChanged` handlers anywhere in the project.
  - **Consequence (applies to ALL three options below):** the entries still register in the `ConfigFile`
    (so an overlay can *find and edit* them), **but our code never re-reads them** → an in-game edit
    updates `ConfigEntry.Value` and the `.cfg`, and changes **nothing** in behaviour. Even editing the
    `.cfg` mid-session, or starting a new game in the same process, does nothing today. **Making edits
    actually apply is the real work of A3, and it is identical whichever menu we pick.**
- **We already own an in-game IMGUI stack.** `DebugTools.Register` injects a MonoBehaviour
  (`ClassInjector.RegisterTypeInIl2Cpp<DebugHotkey>()`) onto a `DontDestroyOnLoad` GameObject, which
  polls `Input.GetKeyDown` and draws via `OnGUI`/`GUI.Label`/`GUI.Box`/`GUIStyle`
  (`DebugTools.cs:70-82, 499-618`). A custom menu is **not new infrastructure** — it's the same pattern
  we already ship, with `GUI.Toggle`/`GUILayout.HorizontalSlider` added.
- **Precedent — we deliberately avoided SOD.Common once already.** `docs/save-reload-recon.md:45`:
  custom sidecar persistence, "No `SOD.Common` dependency required." (v2-design.md only ever listed it as
  a *possible future* time/save-hook dep.)

---

## The three options

### (a) SOD.Common (Ven0maus) — community lib
- **What its config feature actually is:** `PluginController` + an `IConfigBindings` interface whose
  properties carry a `[Binding(default, description, "Section.Key")]` attribute; `OnConfigureBindings`
  generates the matching `ConfigEntry`s in the BepInEx `.cfg`. It is **model-based `.cfg` binding sugar**
  — a tidier way to *declare* config.
- **Does it expose an in-game settings menu? NO.** Its documented modules are `PluginController`,
  `Helpers` (save/time/input/GameMessage hooks), `Extensions`, `Custom`, `Useful Information`. No UI /
  overlay / options-screen module surfaced in the README, wiki index, or feature discussions. It answers
  the plan's exact question with a clear no.
- **Cost if adopted anyway:** restructure the plugin onto `PluginController<TBase,TBindings>` + an
  interface-modelled config — a real refactor — for **zero UI benefit**. Contradicts our standing
  "no SOD.Common" choice. **→ Rejected for A3** (still fine to reconsider later for time/save hooks; out
  of scope here).

### (b) ConfigurationManager overlay (external, user-installed) — the SOD community norm
- **Two concrete, IL2CPP-compatible overlays exist and both fit SOD (BepInEx 6 IL2CPP):**
  - **BepInExConfigManager** (sinai-dev; repacked for SOD on Thunderstore by TeamSpyraxi). Opens with
    **F5** (rebindable in-menu or via `com.sinai.BepInExConfigManager.cfg`). "Responds to configuration
    file changes while in game." **This is the established SOD pattern** — e.g. the *GameBalanceOptions*
    mod ships `ConfigEntry`s and depends on it for its menu.
  - **Official BepInEx.ConfigurationManager** — BepInEx 6 nightly (build 664+) IL2CPP build; opens with
    **F1**; the generic, dev-flavoured baseline.
- **How it works:** reflects over every loaded plugin's `ConfigFile` and renders a toggle/slider/textbox
  per entry, grouped by section. **It discovers our knobs with no code change** (they're in the
  `ConfigFile` despite the discarded handle — confirmable in one F1/F5 press once installed).
- **What we still must do:** the re-read refactor (see "How to surface"), or every edit is a no-op. Also
  our descriptions/ranges become the UI, and optional `ConfigurationManagerAttributes` tags
  (ordering / `IsAdvanced` / value ranges) would need referencing if we want to polish layout.
- **Trade-offs:** ✅ tiny code, ✅ idiomatic for SOD, ✅ no UI to maintain, ✅ auto-covers future knobs.
  ⚠️ user installs a second mod (one-click via a Thunderstore mod manager as a declared dependency),
  ⚠️ generic dev look, ⚠️ **F5 collides with our debug "trigger murder"** (rebindable, and F5 is a
  Phase-D-gated debug key — not a real blocker; F1 variant avoids it).

### (c) Custom Unity IMGUI menu (roll-our-own)
- **Feasibility: high — infra already present** (see current state). Add a toggle key (pick one free of
  the game + any overlay), a scrollable window with per-section toggles/sliders/int-fields bound to our
  `ConfigEntry`s, and Save/Reset that write `ConfigEntry.Value` (+ `.cfg`).
- **Trade-offs:** ✅ zero extra install (works out of the box), ✅ brandable / can live off the game's
  pause menu, ✅ full control of wording, grouping, apply-timing hints. ⚠️ most code (~a few hundred
  lines of IMGUI + input/mouse-capture/pause handling), ⚠️ **ours to maintain** as knobs change,
  ⚠️ re-implements a solved problem.
- Still requires the **same** re-read refactor to make edits take effect.

---

## How to surface the ConfigEntrys (the shared enabling refactor — needed by ALL of the above)
The point-of-use code reads plain static fields today; keep that (fast, no per-read interop) but make the
fields **track their entry**. Minimal, one line per knob (same shape as now) via a small helper:

```csharp
// binds, applies now, and re-applies whenever the entry changes (overlay edit, .cfg reload, our menu)
void BindApply<T>(string sec, string key, T def, string desc, Action<T> apply) {
    var e = Config.Bind(sec, key, def, desc);
    apply(e.Value);
    e.SettingChanged += (_, __) => apply(e.Value);
    _entries.Add(e);                 // list also feeds a custom menu if we build (c)
}
// e.g.  BindApply("General","EnableOverride",true,"…", v => MurderSelector.EnableOverride = v);
```

`SettingChanged` fires when either overlay (b) **or** our own menu (c) writes the value, so the same
helper lights up every path.

**Apply-timing — most knobs go live with NO restart:**
| When the field is consulted | Knobs | In-game edit takes effect |
|---|---|---|
| Per-case / clue-inject | selection shares, `KillerPoolSize`, `MinSuspects`, `NameKnownThreshold`, `VanillaCaseEvery`, all `[Clues]`, workplace/property shares+caps, all `Enable*` toggles, watchdog | **next case** (no restart) |
| New-game seeding | `[Feud]` `MaxFeuds`/`MaxDebts` (+ `EnableFeuds/Debts` at seed) | **next new game** |
| Never bound yet | `DebugTools.ForceMotiveType` (hardcoded `Plugin.cs:103`) | fold into the `[Debug]` work in Phase D |

So the honest UX is "**applies to the next case; a few apply on a new game**" — a full process restart is
essentially never needed once the refactor lands. Add a one-line note per such knob (or a menu hint).

---

## Sizing verdict
- **(b) external overlay:** **SMALL.** ≈ the re-read refactor + declare the dependency + (optional)
  attribute polish. No UI code. Idiomatic. Lands comfortably before Phase D.
- **(c) custom IMGUI:** **MEDIUM.** The re-read refactor + build & own the settings window. No user
  install. Justified only if we want zero-dependency / pause-menu / branded config.
- **(a) SOD.Common:** **not applicable** (no UI) and a net-negative refactor — rejected.

## Recommendation
**Go (b) — declare the ConfigEntrys through the shared re-read refactor and lean on an external
ConfigurationManager overlay, defaulting to the SOD-native BepInExConfigManager** (with the official
F1 ConfigurationManager as an equivalent). It's the smallest change, matches how SOD mods already do
this, needs no UI maintenance, and auto-covers knobs we add later. The one real cost — a second
user-install — is a one-click declared dependency in a Thunderstore mod manager.

**Pick (c) instead only if** "no second install / config in the pause menu / branded UI" outweighs owning
~a few hundred lines of IMGUI forever. The infra cost is genuinely low (we already ship the MonoBehaviour
+ `OnGUI` stack), so (c) is viable — it's a product call, not a technical blocker.

**Either way, step 1 is the same:** the `BindApply`/`SettingChanged` re-read refactor. That's the gating
work and it's independent of the menu choice, so it can start the moment the approach is picked.

---

## UPDATE 2026-09-16 — chosen direction: the game's OWN new-game settings screen (recon)
**User's call:** not a floating overlay or external manager — put our knobs in **the game's gameplay
modifiers / the gameplay settings you pick when generating a new game**. This is a *fourth* option beyond
the three above (a bespoke, in-native-menu variant of (c)). It **does** need game-symbol recon; findings
below are `ilspycmd`-verified against the interop DLL (dumps in scratchpad `a3recon/`; bodies would be
Cpp2IL, not needed yet).

### How the new-game settings screen is actually built (verified)
- **`MainMenuController`** owns the whole new-game screen as **fixed, individually-named widget fields** —
  `gameDifficultyDropdown`(+`2`), `gameLengthDropdown`, `selectCityDropdown`, `citySizeDropdown`,
  `cityPopDropdown`, `startTimeDropdown`, `playerGenderDropdown`, `statusEffectsDropdown`, plus modifier
  UI (`mousedOverModifier : ToggleController`, `modifiersHeader`/`modifiersDescription : TMP_Text`).
  Build/entry methods: `Start()`, `LoadDropdownContent()`, and **`NewGameTypeButton(bool sandbox)`** (the
  "start a new game" click). **There is NO data-driven settings list to append to** — every option is a
  hand-placed widget on a prefab (the one `List<ToggleController> statusEffectToggles` is
  status-effect-specific, not a general modifier list).
- **The 8 gameplay modifiers are a hardcoded set** on **`ModifiersController`** (a MonoBehaviour):
  `filmNoir / ironman / fameAndFortune / gamblingDebt / houseArrest / ratDetective / shortSighted /
  snailNemesis` — each a `bool <name>ModifierEnabled` with an `Activate<M>()`/`Deactivate<M>()` pair.
  Applied at **`ModifiersController.OnNewGameStarted(bool loadedSaveGame)`** (clean "apply seed-time
  settings now, and I can tell new-vs-loaded" hook). No `customModifier`/`ModifierPreset`/`RegisterModifier`
  anywhere → **no way to register a 9th modifier via data.**
- **The widgets are the game's own clone-able controllers** (good news for injection):
  - `ToggleController`: `isOn`, `SetIsOnWithoutNotify(bool)`, `SetOn/SetOff`, `OnValueChange()`,
    `onButton/offButton : ButtonController`, and a **`playerPrefsID`** (each toggle persists to PlayerPrefs).
  - `DropdownController`: `AddOptions(List<string>,…)`, `GetCurrentSelectedStaticOption()`,
    `dropdown : TMP_Dropdown`, `OnValueChange()`, `playerPrefsID`.
- **Native persistence = PlayerPrefs per widget** (`playerPrefsID`), *not* `SessionData`; values reach the
  sim at `OnNewGameStarted`. So the game's new-game choices are PlayerPrefs-backed, applied at game start.
- **Precedent:** "Better Main Menu" (Nexus/mod.io) already modifies this menu → **the menu is moddable via
  runtime UI injection.** Notably, **GameBalanceOptions** (the balance-tuning mod, closest analog to us)
  did **not** inject into the native menu — it ships `ConfigEntry`s + leans on BepInExConfigManager (the
  overlay path (b)). Nobody surfaced a "register a new-game option" API (SOD.Common included).

### Feasibility verdict
✅ **Doable, but it's the LARGEST and most fragile option, with no modding API to lean on.** Because the
screen is a fixed prefab, we must **instantiate our own widgets at runtime and inject them into the menu
layout**: clone a `ToggleController`/`DropdownController`, reparent it under the modifiers/settings page,
set label + hover description, and wire `OnValueChange` → our `ConfigEntry`/static field. That's IL2CPP
runtime-UI work with **vmail-class "looks simple, needs in-game iteration" risk** (styling, anchoring,
layout rebuild), and it's **brittle across game updates** — the Modifiers Update reworked this very page,
so a future patch can move our anchor and silently break the injection. It also only appears on the
**new-game screen** (can't retune mid-game) — which happens to fit our seed/new-game-time knobs, but not
debug toggles or anything you'd want to change in a running game.

**Sizing:** native injection = **LARGE** (vs overlay (b) SMALL, custom-IMGUI (c) MEDIUM). Still requires
the same `BindApply` re-read refactor underneath.

### Two ways to honor the intent
- **A — Full native interleave:** our knobs appear as extra toggles/dropdowns among the vanilla modifiers/
  settings, styled to match. Most seamless; most code; most breakage-prone across patches.
- **B — One "Mod Settings" entry in that screen → our own panel** (a cloned game panel or an IMGUI panel
  opened by a single injected button/toggle). Preserves "configure it where you start a new game" while
  injecting **one** stable anchor instead of N interleaved widgets → far less layout-brittle and cheaper
  to maintain. **Recommended balance of the user's intent vs. cost/risk.**

### Concrete plan (whichever of A/B)
1. **Re-read refactor** (`BindApply` + `SettingChanged`) — unchanged prerequisite; makes edits actually
   apply (next case / next new game).
2. **Inject UI:** Harmony **postfix on `MainMenuController.Start()`** (or `LoadDropdownContent()`); clone a
   `ToggleController`/`DropdownController`, reparent under the modifiers page (anchor off `modifiersHeader`
   / an existing modifier toggle), set text, wire `OnValueChange` → our entry. (A = N widgets; B = 1
   button opening our panel.)
3. **Capture/apply at start:** Harmony hook on **`NewGameTypeButton(bool sandbox)`** and/or
   **`ModifiersController.OnNewGameStarted(bool loadedSaveGame)`** → push widget values into our static
   fields + persist (our `.cfg`, optionally mirror to PlayerPrefs like vanilla).
4. **Test-hook first** (the vmail lesson): inject ONE dummy toggle, confirm it renders/styles/persists in
   the real menu before wiring all knobs.

### Open question for the user (before any code)
Native injection is the biggest, most maintenance-heavy path and has no API to lean on. **Confirm the
scope:** **A** (full interleaved native widgets), **B** (one "Mod Settings" entry in that screen → our
panel — recommended), or fall back to **(b)** the external overlay (smallest, but not in the new-game
screen). Then step 1 (the re-read refactor) starts regardless.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

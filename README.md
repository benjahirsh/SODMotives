# SOD Motives Mod

A BepInEx (IL2CPP) plugin for **Shadows of Doubt** that replaces procedurally-generated murders with
ones driven by **real NPC relationships and events**, so the player can reconstruct an actual motive
from a discoverable trail — clues, gossip and interrogation — instead of chasing a random stranger.

> **Version 1.0.0 — first release.** Feature-complete and playtested at production balance.

## What it does

For ordinary generated murders only (`MurderPreset.CaseType.murder` — kidnap/sniper and story cases are
left fully vanilla), the mod picks a **victim with several real, event-backed enemies** and then a
**killer at random from that pool**, so every suspect is a genuine red herring and the killer looks no
guiltier than the rest. Vanilla physical forensics (weapon prints / CCTV / alibi) still convicts the one
— the motive layer supplements it, it never replaces it.

### Motive families

Motives come from **simulated social events**, not the static `like` value. Each is traceable:

| Family | Events | Who's involved |
|---|---|---|
| **Affair** | infidelity / love-triangle | the two lovers + betrayed partners |
| **Workplace** | promotion, layoffs | the promotee/boss + passed-over rivals / laid-off staff |
| **Property** | eviction, rent-arrears | landlord + aggrieved tenants (bidirectional for arrears) |
| **Feud** | personal bad blood | the two feuding parties (bidirectional) |
| **Debt** | unpaid debts | creditor + debtor (bidirectional) |

Feuds and debts are seeded at new-game from genuinely soured relationships; workplace and property cases
are built on demand from live company rosters and the residency graph at murder time.

### The trail

- **Physical clues** — motive-appropriate notes rendered from document DDS trees, placed among the
  victim's things (home *or* workplace, so location never betrays the motive), with the author's
  handwriting and (per an absolute per-type policy) fingerprints. Custom-written bodies carry clickable,
  board-pinnable citizen/address links. The affair love letter is latent (initialled salutation, no named
  From/To).
- **Emails** — eligible clues (affair letter, redundancy list, redevelopment plan) can instead arrive as
  a vmail in an NPC's inbox; the promotion letter always emails boss → promotee. Survives save/reload via
  a sidecar.
- **Gossip / interrogation** — asking an NPC "do you know this person?" can append naturalised gossip
  about the subject's motive events, so you can build the suspect pool by asking around. The promotee, for
  instance, will hint that some coworkers were bitter about being passed over.

Serial-killer dressing (calling card / moniker / graffiti) is stripped from motivated cases so they read
as personal crimes.

## Build & deploy

Requires the **.NET SDK**. From this folder:

```bash
dotnet build -c Release
```

The build auto-copies `SODMotives.dll` into `<game>/BepInEx/plugins/SODMotives/` (skipped if the game is
running). **Restart the game to load new code** (plugins load at startup). Logs:
`<game>/BepInEx/LogOutput.log` (filter `[SODMotives]`).

Toolchain: BepInEx 6 IL2CPP + Il2CppInterop + HarmonyX, referencing the game's interop assemblies under
`<game>/BepInEx/interop`.

## Dependencies

- **Required:** `BepInEx-BepInExPack_IL2CPP` (the IL2CPP BepInEx pack for Shadows of Doubt).
- **Recommended:** `TeamSpyraxi-BepInExConfigManager` — a ConfigurationManager-style overlay used across
  Shadows of Doubt mods (GUID `com.sinai.BepInExConfigManager`). It gives you an **in-game config screen**
  for every knob below; open it with the **`` ` ``** (backquote) key. The mod ships plain `ConfigEntry`s, so
  without the overlay it still runs — you just edit the values in the `.cfg` file by hand instead.

## Config

`<game>/BepInEx/config/com.benhirsh.sodmotives.cfg`, grouped into sections (all live-editable in the
overlay; the `[Feud]` seeding caps apply at the next New Game):

- **`[Motive Mix]`** — `MotiveCaseShare` (fraction of cases that are motive cases vs left vanilla) plus five
  relative family weights `AffairShare` / `WorkplaceShare` / `PropertyShare` / `FeudShare` / `DebtShare`
  (normalised by their sum — they need not total 1). `PromotionShare` and `EvictionShare` split those
  sub-types.
- **`[Selection]`** — `MinSuspects`, `KillerPoolSize`, `NameKnownThreshold`.
- **`[Workplace]`** — `EnableWorkplace`, `MaxSuspects`.
- **`[Property]`** — `EnableProperty`, `MaxTenantSuspects`.
- **`[Feud]`** — `EnableFeuds`, `EnableDebts`, `MaxFeuds`, `MaxDebts`.
- **`[Clues]`** — `MaxCluesPerCase`, `WorkplaceClueShare`, `EmailClueShare`.
- **`[Troubleshooting]`** — `UnstickStalledMurders`, `StallGameHours`, `StripSignatures`, and the inverted
  `DisableClueInjection` / `DisableInterrogation`, plus test aids `ObviousTestNames` / `ForceAllPrints`
  (both off by default).
- **`[Debug]`** — `EnableDebugKeys` (default **off**) — the developer-tooling master switch (see below).
- **`[Debug Keys]`** — the rebindable hotkeys (only active when `EnableDebugKeys` is on).

## Diagnostics & developer tooling

Out of the box the mod adds **one** always-available key:

- **F9** — the **case-solution** overlay: case type (mod / vanilla), murder state, killer / victim / scene,
  motive + suspect pool, injected clues (+ locations), and the VICTIM / KILLER KNOWERS interview lists. It's
  a full spoiler pane — press it only if you want the answer. Don't want it? Set `CaseSolutionOverlay` to
  `None` in `[Debug Keys]` (via the `` ` `` config overlay) to disable it entirely.

The rest of the test loop is off by default. Turn on **`[Debug] EnableDebugKeys`** in the config overlay to
enable it (rebindable in `[Debug Keys]`; defaults shown). It does **not** change F9 — only these:

- **F4** — trigger the next murder now. **F6** — cycle the forced next-murder event type
  (off / affair / promotion / layoffs / eviction / rent-arrears / feud / debt).
- **F3** — teleport to the nearest killer-knower. **F10** — crime scene. **F11** — victim's workplace.
  **F12** — nearest case-knower (victim's side).
- **F7** — ghost mode (invincible; NPCs ignore you). **F8** — always-answer (NPCs never refuse
  "do you know this person?").
- Plus the on-screen dev indicators and verbose per-case logging.

## Architecture

| File | Role |
|---|---|
| `Plugin.cs` | BepInEx entry, config binding (`BindApply` live re-apply), Harmony patches (override, signature strip, clue/fact hooks) |
| `Motive.cs` | motive scoring + name-familiarity helpers |
| `SocialEvent.cs` | the event model (edges, groups, testimony) |
| `AffairSim.cs` / `WorkplaceSim.cs` / `PropertySim.cs` / `FeudSim.cs` | per-family event sources |
| `MurderSelector.cs` | victim-centric mixed-motive pool → killer pick |
| `ClueInjector.cs` | physical notes + emails, custom DDS bodies, prints, connections |
| `Interrogation.cs` | appended gossip on "do you know this person?" |
| `Persistence.cs` | save/reload replay of custom notes + emails |
| `MurderWatchdog.cs` | recovers a mod murder that soft-locks in the executing state |
| `DebugTools.cs` | F-key testing aids + on-screen HUD (injected MonoBehaviour) |

## Key engine facts (verified via ilspycmd / Cpp2IL on the interop assembly)

- Pipeline: `MurderController.ExecuteNewMurder(murderer, victim, preset, MO, site)` — hooked with a
  prefix; `victimSite` arrives null, so swapping the pair rebuilds location/clues cleanly. Gated to
  `procGenLoopActive` + `CaseType.murder`.
- Clue text: notes render live from a DDS tree; override with `Interactable.SetDDSOverride(treeID)` +
  `Evidence.SetOverrideDDS`. The tree MUST be `treeType==document` (vmail/misc render blank). Mods CAN
  author fully-custom document body text at runtime by registering the tree/message/block in Toolbox's DDS
  dictionaries and writing the body via `Strings.LoadIntoDictionary`. Vanilla letter trees resolve
  `|writer|`/`|receiver|` tokens from the note's writer/receiver **fields**, so those must be set for the
  text to render (the facts can be stripped separately to keep a clue latent).
- Prints: `Interactable.AddNewDynamicFingerprint(h, PrintLife.manualRemoval)`. A fingerprint on a clue at
  the crime scene feeds the game's "placed at the scene" evidence.
- "Do you know this person?" recognises a subject by acquaintance-edge **existence** (no `known`
  threshold), so gossip gating on `known` is always a subset of vanilla recognition.

## License

Released under the [MIT License](LICENSE).

# SOD Motives Mod

A BepInEx (IL2CPP) plugin for **Shadows of Doubt** that makes procedurally-generated
murders driven by **NPC relationships**, so the player can reconstruct a real motive
from the evidence instead of chasing a random stranger.

## V1 — feature complete

- **Motivated killer/victim.** For ordinary generated murders only (`MurderPreset.CaseType.murder`;
  kidnap/sniper cases are left fully vanilla), the killer & victim are chosen from the city's
  social graph instead of at random.
- **Motive model.** A directed score per relationship from the game's existing data:
  infidelity (partner/paramour/love-triangle), professional (shared employer / boss / salary gap),
  money (landlord), and a low-`like` "personal feud" catch-all. See `Motive.cs`.
- **Selection.** Weighted-random among the strongest feuds (`WeightExponent` compresses the lean,
  `TopPoolSize` sets the pool), a red-herring bonus for victims with multiple enemies, and a
  same-type penalty for cross-case variety. See `MurderSelector.cs`.
- **No serial-killer dressing.** Calling card / moniker / graffiti are stripped from motivated
  cases so they read as personal crimes. An occasional case (`VanillaSerialKillerChance`) is left
  fully vanilla to preserve the classic hunt.
- **Discoverable paper trail.** 1–3 notes injected per case (the killer's plus red-herring
  suspects), placed among the victim's things, rendered from motive-appropriate **document-type**
  DDS trees, attributed to the real sender, with the author's fingerprints (`FingerprintChance`).
  See `ClueInjector.cs`.

## Build & deploy

Requires the **.NET SDK**. From this folder:

```bash
dotnet build -c Release
```

The build auto-copies `SODMotives.dll` into `<game>/BepInEx/plugins/SODMotives/`.
**Restart the game to load new code** (plugins load at startup). Logs:
`<game>/BepInEx/LogOutput.log` (filter `[SODMotives]`).

Toolchain: BepInEx 6 IL2CPP + Il2CppInterop + HarmonyX, referencing the game's interop
assemblies under `<game>/BepInEx/interop`.

## Config

`<game>/BepInEx/config/com.benhirsh.sodmotives.cfg`:
`EnableOverride`, `TopPoolSize`, `WeightExponent`, `SameTypePenalty`, `RedHerringBonusPer`,
`StripSignatures`, `VanillaSerialKillerChance`, `InjectClues`, `MaxCluesPerCase`,
`FingerprintChance`, `ObviousTestNames`.

## Debug / testing keys (remove for release)

- **F9** — on-screen case solution + suspect shortlist + each injected clue's room.
- **F10** — teleport to the crime scene. **F11** — teleport to the victim's workplace.
- **F7** — ghost mode: invincible + zeroes heat/trespass (NPCs still detect but can't kill you).

## Architecture

| File | Role |
|---|---|
| `Plugin.cs` | BepInEx entry, config binding, Harmony patches (override, signature strip, clue trigger) |
| `Motive.cs` | relationship → motive scoring |
| `MurderSelector.cs` | chooses the motivated pair |
| `ClueInjector.cs` | spawns discoverable notes with motive text + prints |
| `DebugTools.cs` | F7/F9/F10/F11 testing aids (injected MonoBehaviour) |

## Key engine facts (verified via ilspycmd on the interop assembly)

- Pipeline: `MurderController.ExecuteNewMurder(murderer, victim, preset, MO, site)` — hooked with a
  prefix; `victimSite` arrives null so swapping the pair rebuilds location/clues cleanly.
- Relationship: `Human.acquaintances : List<Acquaintance>`; `Acquaintance` carries directed
  `like` (~0.58 neutral, static — trait-compatibility + connection type, no dynamic opinion events),
  `connections`, `Human from/with`. `Human.partner` / `Human.paramour`.
- Murder types: `MurderPreset.CaseType = {murder, sniper, kidnap}` — override only `murder`.
- Clue text: notes read live via `mainEvidenceText`; override with `SetDDSOverride(treeID)` — the
  tree MUST be `treeType==document` (vmail/misc render blank). Prints via
  `Interactable.AddNewDynamicFingerprint(h, PrintLife.manualRemoval)`.

## Release cleanup TODO

- `ObviousTestNames = false`; remove/gate F7 ghost + debug teleports; consider hiding the F9 overlay.

## V2 direction (not started)

Go deeper than clue injection: add a **dynamic social simulation** whose opinion changes leave
**traceable trails** the player can follow (emails, dialogue, phone calls, letters), plus richer
scenarios — inheritance / family feuds, framing & false accusations, accidental (Knives-Out)
murders. With a traceable opinion layer, murder selection can be probability-driven; until then,
selection favors variety over realism because the underlying `like` isn't player-discoverable.

# Save / reload persistence recon — custom notes & interrogation events

**Date:** 2026-09-07 · **Branch:** v2 · **Method:** multi-agent recon over a fresh
`ilspycmd` decompile of the IL2CPP **interop** `Assembly-CSharp.dll` (declarations only —
**no method bodies**), with an adversarial verify pass on every persistence claim. Findings
are tagged **[confirmed]** (a specific declared field/signature/attribute backs it) or
**[inferred]** (reasoning with no body to prove it). This document is recon + a fix design;
**no code here is wired into the build.** One cheap in-game test (below) settles the last
inference.

---

## TL;DR verdict

**A save → quit-to-menu → reload almost certainly blanks our custom notes.** This was the
suspected W3/W4 risk (memory follow-up #1); the decompile now grounds it structurally.

- The **note object itself survives** the reload. It is a `[Serializable]` `Interactable`
  stored in `StateSaveData.interactables` (a `List<Interactable>`) and re-indexed into
  `CityData.Instance.savableInteractableDictionary` by its int `id`. Its DDS-override id
  (`Interactable.dds`), evidence override (`EvidenceStateSave.dds`), **custom name**
  (`EvidenceStateSave.customName`), author **fingerprints** (`Interactable.df`), and
  passed-vars (`Interactable.pv`, incl. a `PassedVarType.ddsOverride` entry) are all fields of
  `[Serializable]` save classes. **[confirmed]**
- But everything the note's override id *points at* is **runtime-only, rebuilt from
  StreamingAssets at boot, and absent from every `*SaveData` class**: the Toolbox DDS
  dictionaries (`allDDSBlocks/allDDSMessages/allDDSTrees`), the Strings `dds.blocks` body
  text, the clickable-link ids (`Strings.AddOrGetLink` → `linkIDReference`/`LinkData`, not even
  `[Serializable]`), and the doc→citizen **connection Facts**
  (`Evidence.allFacts`/`factDictionary`, never serialized). **[confirmed]**
- **Net on reload:** the note comes back keeping its override id and its title, but that id
  resolves to nothing → **the page renders blank, the clickable citizen links are dead, and the
  case-board connections are gone.** The one piece that survives on its own is the **title**
  ("Redundancy List") because `EvidenceStateSave.customName` stores it as literal text.
- **The note object alone is NOT enough to rebuild** — the suspect name-list lived in the
  Facts (gone) and the persisted fields can't encode citizen identities (`Evidence.DataKey` is a
  fixed attribute enum: name/photo/fingerprints/…, no citizen refs). **[confirmed]** So the fix
  needs mod-owned persisted content.
- **The W4 interrogation layer breaks the same way:** `EventStore` and the `MurderSelector`
  per-victim maps are pure mod statics, never serialized. After reload they're empty, so an NPC
  asked about the (still-active) case says nothing. **[confirmed — they're plain statics.]**

**Good news:** the game gives us clean hooks and stable handles for a fix, and the note keeps a
usable **join key** (its persisted `dds` treeId). The fix is a mod-owned **sidecar** written
next to the `.sodb` save file, replayed on load. No `SOD.Common` dependency required.

---

## RUN THIS FIRST — the one cheap test that confirms the prediction

Everything above is structural; the single [inferred] link is "boot rebuild fully repopulates
the DDS/Strings tables from disk, so our runtime entries are gone." Confirm it in ~3 minutes
before building anything:

1. Start a game, force a **layoffs** case (F6 → Professional, F5), let the **Redundancy List**
   note spawn, open it, confirm it reads correctly (body + clickable names + connections tab).
2. **Save.** Quit to main menu. **Load** that save.
3. Open the same note.
   - **If blank / dead links / no connections** → prediction confirmed; implement the fix below.
   - **If still perfect** → the game is doing something we can't see from declarations (e.g.
     merging rather than clearing DDS tables, or caching resolved text on the object). Capture
     exactly what survives and we re-scope — the fix shrinks or disappears.

Also glance at whether the **title** ("Redundancy List") survives while the body doesn't — that
asymmetry is the signature of this exact failure and a useful confirmation.

---

## What persists vs. what's orphaned (evidence table)

| Mod state (per custom note) | Persists? | Where / why | Confidence |
|---|---|---|---|
| The note **Interactable** object | **Yes** | `StateSaveData.interactables : List<Interactable>`; `[Serializable] Interactable`; re-indexed in `CityData.savableInteractableDictionary : Dictionary<int,Interactable>` by int `id` | confirmed¹ |
| `Interactable.dds` (SetDDSOverride id) | **Yes (value)** | serialized field on the `[Serializable]` interactable; also mirrored as a `PassedVarType.ddsOverride` entry in `Interactable.pv : List<Passed>` | confirmed¹ |
| `Evidence.overrideDDS` (SetOverrideDDS) | **Yes (value)** | `EvidenceStateSave.dds : string` (in `StateSaveData.evidence`) | confirmed² |
| Custom **name/title** (AddOrSetCustomName) | **Yes** | `EvidenceStateSave.customName : List<Evidence.CustomName{DataKey,string}>` — literal text, needs no runtime dict | confirmed |
| Author **fingerprints** (AddNewDynamicFingerprint) | **Yes** | `Interactable.df : List<DynamicFingerprint>`, `[Serializable]` (id/created/seed/life); global `dynamicPrintsCount` also saved | confirmed |
| Toolbox DDS tree/block/message the id points at | **No** | `allDDSBlocks/allDDSMessages/allDDSTrees` are plain instance `Dictionary` fields on `Toolbox`, absent from all `*SaveData`; rebuilt via `Toolbox.LoadDDS()/LoadDDSFilesFromPath()`/`LoadModdedDDSFiles()` at boot | confirmed (absence) / inferred (rebuild) |
| Note **body text** (`Strings.LoadIntoDictionary("dds.blocks",…)`) | **No** | `Strings.stringTable` is a static dict populated from disk (`LoadTextFiles`/`ParseLine`/`LoadIntoDictionary`); not in any save class | confirmed (absence) / inferred (rebuild) |
| Clickable **link ids** (`Strings.AddOrGetLink`) | **No** | `Strings.linkIDReference : Dictionary<int,LinkData>`; `LinkData` is **not** `[Serializable]` and in no save class | confirmed |
| Case-board **connection Facts** (`CreateFact "To"`) | **No** | facts live only in runtime `Evidence.allFacts`/`factDictionary`; no `Fact`/`FactLink` field in any `*SaveData`; custom "To" facts aren't auto-regenerated | confirmed (non-persistence) / inferred (regeneration) |
| `writer`/`reciever`/`belongsTo` (Human refs on the note) | **Unknown** | no dedicated save field encodes them; whether the serializer preserves the Human refs is a body detail | inferred |
| Mod `EventStore` + `MurderSelector` maps (interrogation) | **No** | plain mod statics; nothing serializes them | confirmed |

¹ Caveat from the verify pass: in interop *every* native field appears as a get/set property,
so "serialized public field" can't be distinguished from a computed/`[NonSerialized]` property
at the declaration level. What *is* unusually well-grounded: `StateSaveData.interactables` is a
`List<Interactable>` of the **live `[Serializable]` class** (not a stripped DTO), so "the note is
serialized whole" is structurally solid. The DTO-population mapping (that `Save()` copies these
in and `Load()` restores them) lives in unreadable native bodies.
² The `Evidence.overrideDDS → EvidenceStateSave.dds` mapping is a name/type coincidence (it's the
only `dds`-typed string on each side) — strong circumstantial, not declaration-proof.

**Gate to be aware of:** whether our placed note is written at all depends on
`Interactable.save` / `IsSaveStateEligable()` (unreadable). `Evidence.forceSave` (mirrored as
`EvidenceStateSave.fs`) can force inclusion if a `PlaceObject` note turns out not to be
save-eligible by default. Confirm in the test above (does the note reappear in the world at all
after reload? — it should, as ordinary evidence).

---

## Why it breaks (mechanism, one paragraph)

Text is resolved **at read time** by dictionary+key lookup — `Strings.Get(dictionary, key, …)`
and the `allDDSTrees[id] → allDDSMessages[msgID] → allDDSBlocks[blockID] + stringTable["dds.blocks"][blockKey]`
chain — nothing caches resolved text into the save. Our note stored only the **tree id**; the
tree, its messages/blocks, the body string, and the link ids were all runtime entries we injected
into those boot-rebuilt tables. After reload the tables contain only the game's baseline content,
so our tree id is orphaned and every lookup misses. The title survives solely because it's stored
as literal text on the evidence save, not via a dictionary.

---

## The fix — mod-owned sidecar + deterministic replay on load

The game exposes **no** mod-data channel to piggyback the save (`ModLoader` handles only
`ModSettingsData`; no `customData`/`modData` field on any save class). So we own a sidecar.

### Handles the recon pinned (all confirmed unless noted)

- **Save hook:** `SaveStateController.CaptureSaveStateAsync(string path, bool isOverwrite)`
  (decomp line 1583) — `path` is the full `.sodb` path. PREFIX/POSTFIX here to write the sidecar.
- **Load-restore hook:** `SaveStateController.LoadSaveState(StateSaveData load)` (line 1612) —
  runs **only** when loading a save (new-game generation doesn't call it). POSTFIX = "save
  restored." Interactables are restored during this call (`_LoadSaveState_b__2/5/15(Interactable)`
  closures), so a postfix sees them.
- **World-ready signal (alternative/complement):** `CityConstructor.LoadState.loadComplete`
  (enum member) with **`CityConstructor.generateNew == false`** (bool field) distinguishing
  load from new-game. Safer pattern: LoadSaveState postfix sets a "replay pending" flag, then do
  the actual re-registration when the world reaches `loadComplete` (all interactables placed).
- **Find a note after load:** `CityData.Instance.savableInteractableDictionary[int id]`
  (fallback `interactableDirectory`); its evidence via `Evidence.evID`.
- **Re-registration handles:** `Toolbox.Instance.allDDSBlocks/allDDSMessages/allDDSTrees`,
  `Strings.LoadIntoDictionary("dds.blocks", …)`, `Strings.AddOrGetLink`,
  `EvidenceCreator.Instance.CreateFact`, `Interactable.SetDDSOverride` + `Evidence.SetOverrideDDS`.
- **On-disk save layout (verified on this machine):** saves are single files at
  `%USERPROFILE%\AppData\LocalLow\ColePowered Games\Shadows of Doubt\Save\<name>.sodb`
  (e.g. `Quick Save.sodb`). Sidecar → `<same dir>\<name>.sodmotives.json`.

### The key insight that makes replay clean

The note persists its **`dds` treeId**. On load we re-register a document tree under **that same
treeId** — then the surviving note resolves again. Only the *tree id* must match what the note
stored; the tree's internal block/message/string keys and the `AddOrGetLink` link ids can be
minted fresh, because we also **rebuild the body text fresh with the new link ids and re-point the
tree at the new keys** — the whole graph is internally consistent, and the note's stored id is the
only external anchor. So `ClueInjector.BuildRedundancyDocTree` needs one change: accept an
**optional fixed `treeId`** instead of always minting a fresh GUID (it already mints one — we just
store it and reuse it on replay).

### Sidecar contents

Two parts, both keyed by the save file:

1. **Injected-note records** — one per custom note we placed:
   `{ interactableId:int, evId:string, treeId:string, kind:"layoffs"|"promotion"|"threat"|…,
      title:string, authorHumanId:int, orderedCitizenHumanIds:int[], unsent:bool }`
2. **Interrogation state** — so W4 survives reload:
   the `EventStore` events (`type`, participant `humanID`s for `a`/`b`/`group`, `knownBy` ids,
   `companyId`, `placeName`) and the `MurderSelector` per-victim maps
   (`EventByVictim`/`PoolByVictim`/`MotiveByVictim`/`OverriddenVictimIds`) as ids.

### Replay on load (per note record)

1. Look up the live interactable by `interactableId` in `savableInteractableDictionary`
   (skip if gone — the note may have been destroyed in-world).
2. Resolve the persisted `authorHumanId` + `orderedCitizenHumanIds` back to live `Human`s via
   `CityData.Instance.GetHuman(id)` / the citizen directory.
3. `BuildRedundancyDocTree(citizens, author, title, fixedTreeId: record.treeId)` → re-registers
   blocks/messages/tree (under the stored treeId) + Strings body + fresh `AddOrGetLink` ids.
4. Re-apply `Interactable.SetDDSOverride(treeId)` + `Evidence.SetOverrideDDS(treeId)` on the live
   objects (belt-and-suspenders — the stored id should already match, but this guarantees the
   render path re-reads).
5. Re-run `EvidenceCreator.Instance.CreateFact("To", docEv, citizenEv, …, forceDiscoveryOnCreate:true)`
   per citizen to redraw the board connections.
6. Separately, rehydrate `EventStore` + `MurderSelector` maps from part 2 so interrogation works.

### Draft skeleton (UNTESTED — design sketch, not in the build)

```csharp
// Persistence.cs  (NEW — sketch only)
internal struct NoteRecord {
    public int interactableId; public string evId; public string treeId;
    public string kind; public string title;
    public int authorHumanId; public int[] citizenHumanIds; public bool unsent;
}

internal static class NotePersistence {
    // ClueInjector calls this the moment it finishes placing a custom note.
    internal static readonly List<NoteRecord> Records = new List<NoteRecord>();

    // SAVE: hook SaveStateController.CaptureSaveStateAsync(string path, bool) — PREFIX.
    internal static void OnSave(string sodbPath) {
        var payload = new { notes = Records, events = EventStore.Export(), sel = MurderSelector.Export() };
        File.WriteAllText(SidecarPath(sodbPath), JsonSerialize(payload));   // System.Text.Json or a tiny writer
    }

    // LOAD: hook SaveStateController.LoadSaveState(StateSaveData) POSTFIX -> set pending;
    //       replay when CityConstructor reaches loadComplete && !generateNew.
    internal static void OnLoad(string sodbPath) {
        var p = ReadSidecar(SidecarPath(sodbPath)); if (p == null) return;
        EventStore.Import(p.events); MurderSelector.Import(p.sel);           // fixes interrogation
        foreach (var r in p.notes) {
            var inter = CityData.Instance.savableInteractableDictionary[r.interactableId]; if (inter == null) continue;
            var author = Human(r.authorHumanId);
            var citizens = r.citizenHumanIds.Select(Human).Where(h => h != null).ToList();
            string treeId = ClueInjector.BuildRedundancyDocTree(citizens, author, r.title, fixedTreeId: r.treeId);
            inter.SetDDSOverride(treeId); inter.evidence?.SetOverrideDDS(treeId);
            foreach (var c in citizens) ClueInjector.AddCitizenConnection(inter.evidence, c);  // re-run CreateFact
        }
    }

    static string SidecarPath(string sodb) =>
        Path.Combine(Path.GetDirectoryName(sodb), Path.GetFileNameWithoutExtension(sodb) + ".sodmotives.json");
}
```
Plus: `ClueInjector.BuildRedundancyDocTree(..., string fixedTreeId = null)` — use `fixedTreeId`
when supplied instead of minting a GUID. `EventStore.Export/Import` + `MurderSelector.Export/Import`
(ids only). Serialize with `System.Text.Json` (present in net6) or a hand-rolled writer to avoid
IL2CPP reflection quirks.

### The one implementation-time unknown (small)

**Getting the save path on the LOAD side.** `CaptureSaveStateAsync(path)` gives it for free on
save. On load, `CityConstructor.LoadSaveStateFile()` (line 3054) reads the file but takes no path
arg, and `LoadSaveState` gets the already-deserialized `StateSaveData`. The path is chosen upstream
in the menu: `SaveGameEntryController.Setup(FileInfo newInfo)` (each slot carries its `.sodb`
`FileInfo`) → `MainMenuController.LoadGame()` (line 4169). **Pin at implementation time** by
capturing the selected `FileInfo` in a hook on `SaveGameEntryController.Setup` (or the slot-click),
or on `MainMenuController.LoadGame`. Fallback if that's fiddly: key the sidecar by an id we mint
and stash in the note's `customName` tie so records are self-identifying on load (no path needed).

---

## Confidence & open questions (need the in-game test / runtime to settle)

- **[inferred]** Boot fully repopulates DDS/Strings from disk so prior-session entries are gone
  (vs. merge-by-key). Either way our keys are absent → blank; the test confirms the visible
  outcome.
- **[inferred]** `Strings.Get` on a missing `dds.blocks` key returns "" (vs. the raw key or a
  throw) — decides whether an orphaned note is blank vs. shows a key string. Cosmetic; test shows it.
- **[inferred]** Whether `Interactable.writer`/`reciever` Human refs survive reload (no save field
  encodes them). If they don't, the note's forensic writer link may also need re-applying on load
  (`SetWriter` from the persisted `authorHumanId` — cheap to add to replay regardless).
- **[unknown]** Whether a `PlaceObject` note is save-eligible by default (`IsSaveStateEligable`).
  If not, set `Evidence.forceSave`/`Interactable.save` at inject time. Test confirms (note present
  in world after reload?).
- **[inferred]** `AutoCreateFacts` on the restored evidence won't recreate our custom "To" facts
  (it won't — they're custom), and shouldn't clobber our re-created ones. Confirm no double-draw.

---

## Effort estimate

- **Verify (test above):** ~5 min, no code.
- **If confirmed:** ~1 focused session. New `Persistence.cs` (~150 lines), two Harmony hooks, an
  `Export/Import` on `EventStore` + `MurderSelector` (ids only), a `fixedTreeId` param on
  `BuildRedundancyDocTree`, and the `ClueInjector` inject sites recording a `NoteRecord`. The
  risk is all in the two timing hooks and the load-path pin — everything else is deterministic
  replay through APIs the recon already grounded.
- **Scope note:** doing this once fixes **both** the W3 custom-doc clues and the W4 interrogation
  layer (shared `EventStore` rehydration), and any future custom notes/emails built on the engine.

---

*Recon inputs preserved in the workflow transcript
(`…/subagents/workflows/wf_5bee826c-fab/journal.jsonl`) and the decompile at
`…/scratchpad/decomp` (ephemeral — regenerate with `ilspycmd` if needed). Two facets
(save-orchestration, mod-save-channel) were completed by hand after the workflow agents returned a
placeholder and a schema error respectively; their findings are folded in above.*

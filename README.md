# Better Leads and Motives

*A vanilla+ mod for Shadows of Doubt*

Murders are now built around believable, traceable motives, with bespoke clues and NPC gossip that the player can interrogate through the game's dialog system. I made this because I wanted the generated cases to grow deeper narratives the player can piece together and follow as leads. I hope this brings the vanilla experience to its full potential.

**Please leave a comment on the [Steam guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3804929108).** Feedback and bug reports are very welcome.

I'm keeping this vague on purpose so as not to spoil the experience. The rest of this page is structured to reveal more and more about how the mod works the further down you read, so you can choose how much you'd like to know. Each section says how spoilery it is, so stop wherever you're comfortable.

## How to play

Some things to know before starting:

- **"Do you know this person?"** In addition to the usual reply, an NPC will also share what they know about that person's involvement in events. This is your main tool for building a suspect list.
- **"Can I talk to your partner?"** This dialog option appears when speaking to an NPC at home, which sends them to fetch their partner so you can question them too.
- **By default every murder, kidnapping, and sniper case is a motive case.** Change this in the settings overlay (opened with the backquote key, `` ` `` above Tab) to mix motive murders with the vanilla cases.
- **"Procedural Murders" in the Gameplay settings must be switched ON (default)** with "Regular Murders" enabled for murder cases, the "Kidnappings" enabled for motivated kidnappings, and "Sniper cases" enabled for motivated snipers.

## Requirements

- **Required:** the BepInEx pack for IL2CPP (mod managers install this for you automatically).
- **Recommended:** BepInExConfigManager, an in-game settings overlay you open with the `` ` `` (backquote) key. (If it does not open, that overlay's own toggle may have defaulted to F5; you can rebind it to backquote in its settings.)

## Compatibility

This mod has not been tested alongside other mods yet. It should coexist with most, but if you run into a conflict (or confirm it works well with something), a quick report is very welcome and helps me make it a good neighbour in your mod list.

## Installation

Mod manager (r2modman or the Thunderstore Mod Manager): install the mod and launch through the manager. Get it here: [Better Leads and Motives on Thunderstore](https://thunderstore.io/c/shadows-of-doubt/p/opi/Better_Leads_and_Motives/). Manual install works too: put the plugin in `BepInEx/plugins`.

**Works with existing saves.**

STOP HERE - that is everything you need to play.

---

## Mild spoilers: what changes

Vanilla murders are anonymous killings by a roaming serial killer. This mod instead chooses an NPC killer and victim with real reasons behind why the murder would occur. Every case has a list of genuine suspects (not fabricated ones) so the killer is never obvious. The game's normal forensic evidence (fingerprints, cameras, footprints) still decides the case, and the motive layer sits above the physical evidence, so you can know why a murder happened and use that as a lead toward solving it.

---

## More spoilers: lead types

Traces left behind by the motivated murder:

- **Physical clues.** Bespoke notes and documents turn up among the victim's things, written in a real person's hand and carrying links you can pin to your case board.
- **Emails.** Emails are now useful leads too.
- **Questioning people.** The "Do you know this person?" option now relays gossip about that person's involvement in events, allowing you to better solve the case by talking to NPCs.

---

## Full spoilers: the mechanics

Motives come from simulated social events:

| Family | Events | Who is involved |
|---|---|---|
| Affair | infidelity, love triangles | the two lovers and betrayed partners |
| Workplace | promotions, layoffs | the promotee or boss, plus passed over rivals or laid off staff |
| Property | evictions, rent arrears | a landlord and aggrieved tenants |
| Feud | personal bad blood | the two feuding parties |
| Debt | unpaid debts | creditor and debtor |

Feuds and debts are seeded at the start of a new game. Workplace and property cases are built on demand from company rosters and the residency graph when the murder happens.

The mod finds a victim with several event-backed suspects, then picks the killer at random from that pool. Every suspect is a red herring and the killer looks no guiltier than the rest. Vanilla serial-killer dressing (calling card, moniker, graffiti) is stripped so the crime reads as personal.

Notes are placed at the victim's home or workplace. They carry the author's handwriting and sometimes fingerprints. Notes contain clickable links to citizens and addresses.

---

## Configuration

Mixing the probability of a motivated murder and other tweaks can be made in the BepInExConfigManager overlay (the backquote key, `` ` ``), or by editing the config file:

- **Motive mixer:** how many murders are motive cases versus left vanilla, and the relative weight of each motive family.
- **Selection, Workplace, Property, Feud, Clues:** suspect counts, how often each sub-type appears, clue caps, and whether clues favour home, workplace, or email.
- **Troubleshooting:** safety toggles and test aids.
- **Debug:** developer tooling, off by default, and how much detail the F9 overlay shows.

## Running into problems?

There is a built-in diagnostics view to help me fix issues. Press **F9** to open a panel showing what the mod thinks is going on for the current case. If you hit a bug, open this panel and include what it says (a screenshot is perfect). To turn it off, set `CaseSolutionOverlay` to `None` in the config; turning on `ShowSuspectPoolAndKnowers` adds more detail for a report.

**Please leave a comment on the [Steam guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3804929108).** Feedback and bug reports are very welcome.

---

## License

MIT. See LICENSE.

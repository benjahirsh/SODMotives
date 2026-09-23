# Better Leads and Motives

*A vanilla+ mod for Shadows of Doubt*

Murders are now built around believable, traceable motives, with bespoke clues and NPC gossip that the player can interrogate through the game's dialog system. I made this because I wanted the generated cases to grow deeper narratives the player can piece together and follow as leads. I hope this brings the vanilla experience to its full potential.

**Please leave a comment on the [Steam guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3804929108).** Feedback and bug reports are very welcome.

I'm keeping this vague on purpose so as not to spoil the experience. The rest of this page is structured to reveal more and more about how the mod works the further down you read, so you can choose how much you'd like to know. Each section says how spoilery it is, so stop wherever you're comfortable.

## How to play

Some things to know before starting:

- **Asking "Do you know this person?" now tells you more.** On top of the usual reply, an NPC will also share what they know about that person's involvement in events around town. This is your main tool for building a suspect list, and it is easy to miss, so lean on it.
- **By default every murder and kidnapping is a motive case.** You can configure this in the overlay (`` ` `` key) to mix motive cases in with the classic vanilla ones; under Motive Mix, separate sliders set the share for murders and for kidnappings.
- **"Procedural Murders" in the Gameplay settings must be turned ON (default)** for the mod to generate motives, with "Regular Murders" enabled for murder cases and the "Kidnapping" case type enabled for motivated kidnappings. Motives for Sniper cases are in development.

## Requirements

- **Required:** the BepInEx pack for IL2CPP (mod managers install this for you automatically).
- **Recommended:** BepInExConfigManager, an in-game settings overlay you open with the `` ` `` (backquote) key. (If it does not open, that overlay's own toggle may have defaulted to F5; you can rebind it to backquote in its settings.)

## Compatibility

This mod has not been tested alongside other mods yet. It should coexist with most, but if you run into a conflict (or confirm it works well with something), a quick report is very welcome and helps me make it a good neighbour in your mod list.

## Installation

Easiest is a mod manager (r2modman or the Thunderstore Mod Manager): install the mod and launch through the manager. Manual install works too: put the plugin in `BepInEx/plugins`.

**Works with existing saves, no need to start a new game.**

That is everything you need to play. If you want to discover the rest yourself, stop reading here.

---

## Mild spoilers: what changes

Normally a murder in the city is an anonymous killing by a roaming serial killer. With this mod the game instead chooses a victim who has real, human reasons for someone to want them gone, and a killer who is one of those someones. Everyone else with a reason is a genuine suspect, not a fabricated one, so the culprit never stands out as obviously guilty. The game's normal forensic evidence (fingerprints, cameras, alibis) still decides the case, and the motive layer sits above the physical evidence, so you can now know why a murder happened and use that as a lead toward solving it.

---

## More spoilers: lead types

Traces left behind by the motivated murder:

- **Physical clues.** Bespoke notes and documents turn up among the victim's things, written in a real person's hand and carrying links you can pin to your case board.
- **Emails.** Emails are now useful leads too.
- **Questioning people.** As noted above, the "Do you know this person?" option now surfaces gossip about that person's involvement in events, so you build your suspect pool by asking around.

The mod deliberately does not hand you the answer.

---

## Full spoilers: the mechanics

Motives come from simulated social events, not the static "like" value, and each is traceable:

| Family | Events | Who is involved |
|---|---|---|
| Affair | infidelity, love triangles | the two lovers and betrayed partners |
| Workplace | promotions, layoffs | the promotee or boss, plus passed over rivals or laid off staff |
| Property | evictions, rent arrears | a landlord and aggrieved tenants |
| Feud | personal bad blood | the two feuding parties |
| Debt | unpaid debts | creditor and debtor |

Feuds and debts are seeded at the start of a new game from genuinely soured relationships. Workplace and property cases are built on demand from real company rosters and the residency graph at the moment of the murder.

**How a case is built.** The mod finds a victim with several real, event-backed enemies, then picks the killer at random from that pool, so every suspect is a real red herring and the killer looks no guiltier than the rest. Serial-killer dressing (calling card, moniker, graffiti) is stripped so the crime reads as personal.

**The clue layer.** Notes render from in-game document templates and are placed among the victim's things, at home or at their workplace so the location never betrays the motive. They carry the author's handwriting and sometimes their fingerprints. Custom notes contain clickable, board-pinnable links to citizens and addresses.

---

## Configuration

Everything is tunable, live, through the BepInExConfigManager overlay (`` ` `` to open), or by editing the config file:

- **Motive mixer:** how many murders are motive cases versus left vanilla, and the relative weight of each motive family.
- **Selection, Workplace, Property, Feud, Clues:** suspect counts, how often each sub-type appears, clue caps, and whether clues favour home, workplace, or email.
- **Troubleshooting:** safety toggles and test aids.
- **Debug:** developer tooling, off by default, and how much detail the F9 overlay shows.

## Running into problems?

There is a built-in diagnostics view to help me fix issues. Press **F9** to open a panel showing what the mod thinks is going on for the current case: the case type, its current state, the killer and victim, the motive, and which clues were placed. If you hit a bug, open this panel and include what it says (a screenshot is perfect) in your report, and it will help me track the problem down. Note it reveals the answer to the current case, so it doubles as a spoiler. If you would rather never see it, set `CaseSolutionOverlay` to `None` in the config; turning on `ShowSuspectPoolAndKnowers` adds even more detail for a report.

**Please leave a comment on the [Steam guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3804929108).** Feedback and bug reports are very welcome.

---

## License

MIT. See LICENSE.

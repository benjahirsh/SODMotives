# Changelog

## 1.3.3
Fix: fully quitting and reloading a save could stop murders from happening, an in-progress case would show as active but never play out. Reloaded cases now continue correctly. Also renamed the Troubleshooting setting "UnstickStalledMurders" to "MotiveCaseWatchdog" with a clearer description; it stays on by default and gameplay is unchanged.

## 1.3.2
Fix: the mod's injected emails and notes (affair love letters, threatening notes, redundancy and eviction letters, and so on) could turn up blank, showing the sender but no body, after you fully quit the game and loaded a save again. The mod was failing to find its saved data on a cold restart, so it could not rebuild those letters. They now survive a full restart and display correctly. Note: saves that were already affected before this fix may keep some blank entries from before; newly created and still-intact letters are restored.

## 1.3.1
Fix: motivated sniper cases could throw an error that stopped any further murders from happening. Snipers now run reliably, and a sniper case that loses its target (for example if another mod kills the would-be killer) cancels itself instead of jamming the game.

## 1.3.0
New interrogation option: "Can I talk to your partner?". When you talk to someone at home, you can ask to speak to their partner instead. The person steps aside and closes the door, then their partner comes to answer it so you can question them too. If they live alone or their partner is out they say so, and a sleeping partner is woken to answer.

## 1.2.3
New Motive Mix slider, Structural Victim Weight: makes bosses and landlords less likely to be the victim (default: half as likely), which also nudges workplace cases toward the promotee and property cases toward the tenant. Set it to 1 for no bias, or toward 0 to spare them unless there is no other victim. Also removed some developer-only test options from the settings panel.

## 1.2.2
Fix: closed a rare remaining case where a motivated sniper could still stall. After a sniper gave up its planned nest and fell back to a normal firing site, that fallback site could itself never line up a clear shot, leaving the killer repositioning indefinitely with the case unresolved. A motivated sniper that produces no shot at its fallback site now reverts to an ordinary case after a day, so a sniper can never hang the game.

## 1.2.1
Fix: a motivated sniper case could hang if the victim's workplace was chosen as the shot site but the sniper's nest could never line up a clear shot. The killer and victim would then stand in position indefinitely, with no shot and no resolution. The case now gives up after a full shift in that standoff and hands off to a vanilla firing site, so it stays solvable and cannot stall.

## 1.2.0
Motivated sniper cases. Sniper murders can now be driven by real relationship motives, just like murders and kidnappings. The mod picks how the shot happens from a line-of-sight test: if the killer's home overlooks the victim's home or workplace, they take the shot from their own window (a voyeur killing); otherwise they set up at a rooftop or street nest overlooking the victim's routine. For the rooftop cases the mod finds a believable nest that actually overlooks the victim's workplace when it can, and otherwise chooses the shot location from the city's real sniper vantage points (ranked by coverage), so cases spread across many streets instead of always the same rooftop. If a clean shot can't be lined up, the case falls back to a vanilla firing site so it always stays solvable and never stalls. The kill leaves the usual physical trail (a shell casing at the nest, a broken window, and a wound tied to the case). Configure it in the overlay (`` ` `` key) under Motive Mix, where a new slider sets what share of sniper cases are motive cases (default: all); the sandbox "Sniper cases" type must be enabled.

## 1.1.1
Fix: saving and reloading during a motivated kidnapping could break the case, either the kidnapper killing the victim earlier than the ransom deadline, or the victim wandering out of the den and the abduction stalling. Reloading at any stage of a kidnapping now keeps the victim on track, so a saved and reloaded case stays solvable and plays out as intended.

## 1.1.0
Motivated kidnappings. Kidnap cases can now be driven by real relationship motives, just like murders. The victim is lured to a public meeting, walked to a hidden holding den, and held for ransom, all leaving a physical trail you can follow, and fully solvable. Configure it in the overlay (`` ` `` key) under Motive Mix, where a new slider sets what share of kidnappings are motive cases (default: all); the sandbox "Kidnapping" case type must be enabled. Sniper cases still run as vanilla for now; motives for them are in development. Also fixed: a betrayed partner no longer talks about a secret affair they are meant to be unaware of (they remain a suspect, just not a source you can hear it from).

## 1.0.2
Updated readme. Mod needs "Procedural Murders" game setting enabled for motive cases. Sniper and kidnapping cases remain vanilla for now, motive support for them is in progress. Also noted that the mod can be added to a save already in progress. No gameplay changes.

## 1.0.1
The config overlay now opens on the backquote key by default.

## 1.0.0
Initial release.

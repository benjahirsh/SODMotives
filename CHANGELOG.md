# Changelog

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

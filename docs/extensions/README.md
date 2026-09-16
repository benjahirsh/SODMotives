# Extensions backlog (post-release)

Ideas deliberately deferred past the first release. One resume kit per extension in this folder;
smaller items are indexed here with a pointer to where their recon already lives. Newest / highest
interest at the top. Trust but verify any symbol before relying on it.

| Extension | Status | Resume kit / recon |
|---|---|---|
| **Fabricated cross-motive "red herring" clues** | design pass done, no code, DEFERRED | [red-herring-clues.md](red-herring-clues.md) |
| **Theft motive** (stolen-goods custom doc) | recon done, no code | rides the custom-doc engine, no new object type — see `v2.4-feud-debt-handover.md` + `v2.3-landlord-handover.md` |
| **Address book / call history as a "who to interview" lead** | recon done (API identified) | `AddressBookController` + `CallLogsContentController` (`TelephoneController.PhoneCall`); `Toolbox.GetMailbox()` = physical mailbox — see `v2.6-email-recon.md` (Deferred) + `v2.5-handover.md` |
| **Guarantee a non-killer knower for socially-isolated victims** | open (task chip history) | a lone Debt/Feud/RentArrears victim can leave the killer as the only knower — see `v2.3.1-threat-note-handover.md` + `v2.5-handover.md` |
| **Collapse many feud gossip bubbles into one** | idea | copy the existing debt-collapse pattern |

**Not in this folder:** the **W5 release cleanup** checklist (revert test config, gate F-keys, strip
debug logging, the eviction `PlaceName` F9/log cosmetic) is PRE-release cleanup, not an extension — it
lives in the auto-loaded memory (`sod-motives-mod.md`).

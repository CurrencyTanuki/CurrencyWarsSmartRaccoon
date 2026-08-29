# Currency Wars Smart Raccoon 0.2.842 Handoff

Date: 2026-08-09

## Release scope

This snapshot is the authoritative source handoff for version `0.2.842`. The workspace has no Git metadata, so preserve this package and its `FILE_SHA256_MANIFEST.txt` together.

## Privilege equipment repair

- The catalog still contains 36 ordinary equipment records and 36 matching privilege records. The previous icon files were identical, which made the privilege entries appear absent.
- Recognition now treats equipment identity and privilege state as separate facts: first recognize the ordinary equipment artwork, then decide `IsPrivileged` from the cyan/blue frame and lower-right V marker.
- Both user catalog screenshots are permanent fixtures with provenance. They cover all 36 privilege equipment icons, including six dim locked entries.
- Focused verification recognizes 36/36 privilege entries and does not mark the real ordinary-equipment negative fixture as privileged.
- `IsPrivileged` is preserved through frame/state merge, checkpoint JSON, historical projection, realtime history and settlement HTML. Old archives remain compatible because the new field is optional.
- The HTML layer adds the privilege visual treatment while retaining the ordinary equipment name and canonical ID.

## Preserved 0.2.841 work

- Formation equipment supports centered 0/1/2/3-item layouts; the supplied 1-6 frame resolves Yaoguang as `Perpetual Motion Machine`, ordinary `Firepower Storm`, then an empty slot.
- Supply node topology skips 1-5 preparation/battle records and advances 1-4 to 1-6.
- Final-battle evidence retention, cross-frame consensus, historical scalar consistency, synergy tier display and realtime-history refresh dedup remain included.
- Recognition and presentation remain separate components: analyzer/tracker/contracts produce structured state; the App projection and HTML renderer only consume that state.

## Verification status

- Release solution build: 0 warnings, 0 errors.
- Privilege focused suite: 8 passed, 0 failed.
- Full suite: 876 total; 868 passed, 6 failed, 2 not executed.
- The six failures are wall-clock performance budgets only. Five battle frames completed in 3.13-4.59 seconds, and two warm preparation samples completed in 3.28/3.34 seconds. Their recognition correctness assertions completed before the timing assertions failed.
- The two not-executed cases are the existing explicitly skipped legacy performance benchmarks.
- The final published App/DI batch replay processes the supplied 1-6 frame successfully as a preparation page from the packaged binaries.

Do not describe this release as an all-green full-test build. Functional privilege coverage is green; the remaining known red gate is recognition latency on this machine.

## Remaining live validation

- Obtain a real 16:9 battle/preparation screenshot where a character is actually wearing a privilege item. The catalog fixtures validate the overlay at large size, but a real 45-55 pixel equipped-item positive is still needed for final small-icon acceptance.
- Run a complete live match to confirm 1-4 persistence, 1-5 supply behavior, 1-6 transition, realtime detail refresh and settlement history.
- Close all older program instances before review because versions share a single-instance mutex.

## Evidence

- `audit/privilege-overlay-20260809/privilege-overlay-before.trx`
- `audit/privilege-overlay-20260809/privilege-overlay-final-focused.trx`
- `audit/final-verification-0.2.842/build-0.2.842.log`
- `audit/final-verification-0.2.842/build-0.2.842.binlog`
- `audit/final-verification-0.2.842/full-0.2.842.trx`
- `tests/CurrencyWarsAssistant.Tests/Fixtures/PageReplay/privilege_equipment_catalog_01_1745x951.png`
- `tests/CurrencyWarsAssistant.Tests/Fixtures/PageReplay/privilege_equipment_catalog_02_1696x929.png`

See `docs/BUILD_AND_VERIFY_0.2.842.md` for reproducible commands.

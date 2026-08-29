# ISSUE-017 reproduction — reward shop close can trigger twice

## User impact

The shop control is a toggle. If the first click already closes the shop but page verification exceeds its time budget, a blind second click can reopen it. The automation may then continue from the wrong page or oscillate under load.

## Before evidence

- Approved clean baseline source and tests were used.
- Full-suite TRX: `audit/baseline-20260809/test-results/baseline-full.trx`
- TRX SHA-256: `149F7877331A149A6DC8212DDF9232C3A55EF6A27FAE1EE14F900BC26CA60BF3`
- Full result: 807 total; 791 passed; 14 failed; 2 skipped.
- Failing test: `RewardShopCloseIntegrationTests.DisabledRefreshShopIsRecognizedClickedAndVerifiedClosed`
- Failure: expected one `收起商店` click; observed two.
- Duration: approximately 28 seconds in the loaded full suite.

The same unchanged test passed five isolated process runs. The five TRX files are under `test-results/isolated-1.trx` through `isolated-5.trx`. This demonstrates a load-sensitive branch rather than a deterministic basic-path failure.

## Relevant unchanged files

- `tests/CurrencyWarsAssistant.Tests/RewardShopCloseIntegrationTests.cs`
  - SHA-256 `BCA5FFE436043D4493545EFA2FEFF5008AEAF8D477DEEC66C66EB55656C46E29`
- `src/CurrencyWarsAssistant.Tasks/RewardStageAutomation.Shop.cs`
  - SHA-256 `2E96F07A59CEAA7D356905857392605931BAEB95DF150AAB00A0DEB2EBB6406D`
- `src/CurrencyWarsAssistant.Tasks/RewardStageAutomation.cs`
  - SHA-256 `93434D742689C5582E87C66DE01D206F0D646AB0FC51A0349C38AA3EA0E2C77B`

## Deterministic reproduction

Before the production modification, the new regression test preserved the real
shop and preparation screenshots and the real template classifier. It delayed
only the first post-click `preparation_generic` result by 5200 ms so that the
synchronous classification began before the five-second deadline and returned
after it.

- TRX: `test-results/before-deterministic.trx`
- SHA-256: `EF1400D7B6089E16DA2F96643AAAD428D9DAE0FBCFB5635EF3EFD226A5790E6A`
- Result: 0 passed, 1 failed.
- Failure: expected one shop-toggle input; the unchanged production code sent
  two inputs.
- The TRX and test assembly predate the production-file modification, so this
  is a valid before snapshot.

# ISSUE-017 verification

## Final changed files

- Production: `src/CurrencyWarsAssistant.Tasks/RewardStageAutomation.Shop.cs`
  - SHA-256: `A0D30CE9C711F912474A9E811D99BA0F2D69079521131CB572A43B24C27C5C58`
  - The only production difference from the handoff original is the 24-line
    stable-page recheck in `CloseShopAsync`.
- Test: `tests/CurrencyWarsAssistant.Tests/RewardShopCloseIntegrationTests.cs`
  - SHA-256: `33D7F268FBC50FFB245AC4734F66C65959D223B223DD6739DA7A27C9173FC30B`
  - Existing assertions were preserved. Four result rows were added: the
    deadline-crossing regression, confirmed-shop retry, null-page stop and
    other-page stop.

No template, timeout, coordinate, OCR, purchase-planning, page-classifier or
other production file changed in this issue.

## Before evidence

- Loaded full baseline:
  `audit/baseline-20260809/test-results/baseline-full.trx`
  - SHA-256: `149F7877331A149A6DC8212DDF9232C3A55EF6A27FAE1EE14F900BC26CA60BF3`
  - 807 total: 791 passed, 14 failed, 2 not executed.
  - Existing close test reached `closed == true` but sent two toggle inputs.
- Deterministic regression before the production change:
  `test-results/before-deterministic.trx`
  - SHA-256: `EF1400D7B6089E16DA2F96643AAAD428D9DAE0FBCFB5635EF3EFD226A5790E6A`
  - 1 total: 0 passed, 1 failed.
  - Expected one toggle input; actual two.
- Five unchanged isolated runs of the original test passed. Their TRX files are
  `isolated-1.trx` through `isolated-5.trx`; this establishes load sensitivity
  without weakening the deterministic control-flow reproduction.

## Targeted and related regression

- Focused class:
  - TRX: `test-results/after-focused.trx`
  - SHA-256: `FA9CAAD9427BA53570B96FB43CF57B022BC77BF43BFCAC834FB0829406BA4F98`
  - 5/5 passed.
  - Covers normal one-click close, deadline overrun with no duplicate input,
    confirmed-shop safe retry, null-page stop and other-page stop.
- Reward/shop related regression:
  - TRX: `test-results/after-related.trx`
  - SHA-256: `930253D5D9F512A39B4EBAF1DDCA1501EC201B3938838C335E6C925E3E939025`
  - 140/140 passed.

## Full regression and exact baseline comparison

- TRX: `test-results/after-full.trx`
- SHA-256: `49DBA09AAC10C50989783F0F7D74AB9592549E7E218C70A1B6703867C1DAF83E`
- 811 total: 796 passed, 13 failed, 2 not executed.
- Compared by unique test name with the 807-test clean baseline:
  - added: 4, all passed;
  - missing: 0;
  - existing RewardShop close test: Failed -> Passed;
  - two existing Phase2 timing rows: Failed -> Passed;
  - two PpOCR timing rows: Passed -> Failed;
  - no correctness or functional test changed from Passed to Failed.

All 13 remaining failures are wall-clock budget assertions. Their recognition
content, provider, confidence or field assertions passed before the final time
assertion. They are not claimed as fixed by ISSUE-017 and remain separate open
issues. The two NotExecuted performance tests remain skips, not passes.

## Build

- Release solution build completed with 0 warnings and 0 errors.
- Log: `test-results/after-final-solution-build.log`
  - SHA-256: `28EECD3FF2A2EC0BF050F68B44D154038B01636BFD4992DFE5AFF9BB58E372FA`
- Binlog: `test-results/after-final-solution-build.binlog`
  - SHA-256: `6A1D02B1100CF8DCE7B6DBB54994D713A56E551C079104B4C46B3821A93B9CD4`
- Final Tasks assembly:
  - SHA-256: `F62E5349380714857FF59B4ACA4C5A4B3D98719BC6255FB39A80828E37D52BE3`
- Final Tests assembly:
  - SHA-256: `9AB2CEF4921C42F5875958807069CAAAD9391EAC6AB8B8036665A4FA723D37C5`

## Honest boundary

This evidence closes only the duplicate-toggle control-flow bug. The test uses
real screenshots and the real classifier, but a fake capture/input boundary;
actual game focus, animation and physical click behavior are still to be
validated during final executable E2E. Full-suite performance instability is
open and blocks declaring the whole product stable.

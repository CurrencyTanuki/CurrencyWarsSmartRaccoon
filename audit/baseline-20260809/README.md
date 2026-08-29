# Confirmed working baseline — 2026-08-09

## Purpose

This directory records the new working copy created after the user's read-only regression audit.
The desktop handoff original and the earlier audit working copy remain untouched.

## Source and copy boundary

- Source snapshot: `D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-audit-20260808`
- Confirmed working copy: `D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-confirmed-20260809`
- Copy time: 2026-08-09 10:51 Asia/Shanghai
- Excluded build/runtime caches: `bin`, `obj`, `TestResults`, `logs`, `.git`, `.vs`
- Copy result: 9,849 files copied; no copy failures.

## Production source boundary

The confirmed copy differs from the earlier audit copy in exactly one production file:

- `src\CurrencyWarsAssistant.App\gen_report.py`
  - earlier audit copy: `086DFC5B40091DFC1AFF446A91C9F9AEC1C9545C626DF054AAA77BC08F5EC7AE`
  - confirmed baseline: `B7A4B6D1761280AD0D590438D01447795208440C23D460FF21F6D80A41D00AD3`

The confirmed hash is the independently approved ISSUE-008 renderer. It includes Population display but excludes the not-yet-approved ISSUE-016 ambiguous-equipment rendering change.

The shared operational test file was also restored to its last approved pre-ISSUE-016 hash:

- `tests\CurrencyWarsAssistant.Tests\Phase2OperationalCollectionTests.cs`
  - approved baseline: `283CF3983CBDF57821BA986143F0972A7566B1A8503162CEE69FDFFB886FC7EB`

The other seven changed production files retain their independently approved final hashes:

| File | SHA-256 |
|---|---|
| `src\CurrencyWarsAssistant.Vision\UiDigitSequenceRecognition.cs` | `0C24127EB4FE979C859DB9BC9F8E3F671184F39EE908E5E9A9013FF3C7F1AEBE` |
| `src\CurrencyWarsAssistant.Vision\CharacterCardRecognition.cs` | `CCA7358047EFC0899026A19002CC6EF784453C846A244788A052279EBA348E78` |
| `src\CurrencyWarsAssistant.Tasks\Phase2OperationalScreenshotAnalyzer.cs` | `099F2AB550276B24583C1D62D074F5D98F2918059F5D3F88B54BBEC3C5866E69` |
| `src\CurrencyWarsAssistant.Tasks\Phase2RecognitionRegions.cs` | `5CE38E7952AD2437F284906BFF0ADA60D51963CD5AE468E7791DF403D9133758` |
| `src\CurrencyWarsAssistant.Tasks\Phase2RealtimeRecognitionPipeline.cs` | `BD1666448830B6C569ABD7453C3E0F61CD74B374CF334DF7F2F963620F475DB2` |
| `src\CurrencyWarsAssistant.App\DetailedHistoryWindow.xaml.cs` | `16B171030433AA06710481D557186F002C57F8C12CBB78FF2C3C9AB1F246971C` |
| `src\CurrencyWarsAssistant.App\HistoricalUiFieldCoverage.cs` | `7C40E3FB539F88464F375D5E60800894FF983C66D6FB812C01D9D0E94A4EF1A4` |

## Current issue boundary

ISSUE-016 is the only active code issue. The earlier audit copy retains its reproduction test and evidence. After this baseline passes its independent hash and regression gates, that single test will be reintroduced into this confirmed copy to establish a fresh before-red result prior to any production modification.

No build, test, package, or UI claim is attached to this baseline until the independent reviewer validates the hashes and the targeted reproduction is rerun from this directory.

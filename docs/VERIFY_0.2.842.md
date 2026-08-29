# Verify the 0.2.842 runnable package

1. Fully exit every older `CurrencyWarsAssistant.App` process.
2. Extract the ZIP to a new directory; do not overwrite an older version.
3. Run `CurrencyWarsAssistant.App.exe`. Approve UAC when the game is elevated.
4. The program requires the .NET 8 Desktop Runtime and Microsoft Edge WebView2 Runtime. Historical HTML uses the bundled Python runtime and does not use system Python or PATH.
5. Confirm the executable product/file version is `0.2.842`.

The `verification` directory contains the exact Release build log, full TRX, privilege-focused TRX, source screenshots with provenance, and the final packaged App/DI replay report. `FILE_SHA256_MANIFEST.txt` covers every other file in the extracted package.

Automated status: build 0 warnings/0 errors; privilege focused 8/8; full suite 868 passed, 6 timing-budget failures, 2 explicitly skipped legacy performance benchmarks. Functional privilege assertions are green, but a real 16:9 screenshot of a character wearing a privilege item is still required for the 45-55 pixel live-icon acceptance check.

@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Capture-Phase2Dataset.ps1" -DurationMinutes 5 -FramesPerSecond 5
echo.
pause

# 真实退出验证脚本（核对报告要求 8：启动 exe → 关闭 → 进程消失）
# 用法：pwsh -File verify-exit.ps1
$ErrorActionPreference = "Continue"

$exe = "C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\CodexHandoff-20260802\CurrencyWarsSmartRaccoon-CodexHandoff-20260801\artifacts\CurrencyWarsSmartRaccoon-0.2.788-win-x64-portable\CurrencyWarsAssistant.App.exe"
$appName = "CurrencyWarsAssistant.App"

function Get-AppProcess {
    Get-Process -Name $appName -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Test-Exit([string]$name, [scriptblock]$closeAction, [int]$waitSec = 12) {
    $p = Get-AppProcess
    if (-not $p) { Write-Host "[$name] SKIP：应用未运行"; return "skip" }
    $pidBefore = $p.Id
    Write-Host "[$name] 开始：PID=$pidBefore"
    & $closeAction
    $deadline = (Get-Date).AddSeconds($waitSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-AppProcess)) { Write-Host "[$name] PASS：进程已消失"; return "pass" }
    }
    Write-Host "[$name] FAIL：进程仍在（PID=$(Get-AppProcess).Id）"
    return "fail"
}

# 场景 0：应用是否在运行（不在则先启动）
if (-not (Get-AppProcess)) {
    Write-Host "应用未运行，启动它……"
    Start-Process $exe
    Start-Sleep -Seconds 10
    if (-not (Get-AppProcess)) { Write-Host "FATAL：应用启动失败（UAC 未确认？）"; exit 1 }
    Write-Host "应用已启动"
}

# 场景 1：关闭按钮（CloseMainWindow 等效 WM_CLOSE）
Test-Exit "关闭按钮(WM_CLOSE)" {
    $p = Get-AppProcess
    $null = $p.CloseMainWindow()
} | Out-Null
Start-Sleep -Seconds 3

# 场景 2：再次启动 → Alt+F4（WM_SYSCOMMAND SC_CLOSE）
if (-not (Get-AppProcess)) {
    Start-Process $exe; Start-Sleep -Seconds 10
}
Test-Exit "Alt+F4(SC_CLOSE)" {
    $p = Get-AppProcess
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W32 {
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
}
"@
    $null = [W32]::PostMessage($p.MainWindowHandle, 0x0112, [IntPtr]0xF060, [IntPtr]0)  # WM_SYSCOMMAND SC_CLOSE
} | Out-Null
Start-Sleep -Seconds 3

# 场景 3：再次启动 → Esc 键（WM_KEYDOWN VK_ESCAPE）
if (-not (Get-AppProcess)) {
    Start-Process $exe; Start-Sleep -Seconds 10
}
Test-Exit "Esc键(WM_KEYDOWN)" {
    $p = Get-AppProcess
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W32k {
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
}
"@
    $null = [W32k]::PostMessage($p.MainWindowHandle, 0x0100, [IntPtr]0x1B, [IntPtr]0)  # WM_KEYDOWN VK_ESCAPE
} | Out-Null
Start-Sleep -Seconds 3

# 场景 4：多次开关（打开→关闭→打开→关闭，窗口复用路径）
if (-not (Get-AppProcess)) {
    Start-Process $exe; Start-Sleep -Seconds 10
}
Test-Exit "多次开关-第1次" { (Get-AppProcess).CloseMainWindow() } | Out-Null
Start-Sleep -Seconds 2
if (-not (Get-AppProcess)) { Start-Process $exe; Start-Sleep -Seconds 10 }
Test-Exit "多次开关-第2次" { (Get-AppProcess).CloseMainWindow() } | Out-Null

Write-Host "=== 验证完成 ==="

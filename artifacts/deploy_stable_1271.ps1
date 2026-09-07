# deploy_stable_1271.ps1 - 1.2.71 发布脚本（本机专用，路径硬编码）
# 流程：删 exit 残留 → robocopy /E 覆盖 → 原生 dll 核对 → 版本核对 → 端到端 STATUS 验收

$Src = 'C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826\src\CurrencyWarsAssistant.App\bin\Debug\net8.0-windows10.0.19041.0'
$Dst = 'C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前'

$exitPath = Join-Path $Dst '指令测试-exit.txt'
if (Test-Path $exitPath) {
    Remove-Item $exitPath -Force
    Write-Output '已删除 exit.txt 残留'
}

# 0.5) 停旧实例（必须先于 robocopy——1.2.97 实测：旧实例运行时锁定 App.dll，
# robocopy 无 /R 参数=默认百万次×30 秒重试，发布挂死 30 分钟）。
Set-Content -Path $exitPath -Value 'stop' -Encoding UTF8
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 2
    if (-not (Get-Process -Name 'CurrencyWarsAssistant.App' -ErrorAction SilentlyContinue)) { break }
}
if (Test-Path $exitPath) { Remove-Item $exitPath -Force }
if (Get-Process -Name 'CurrencyWarsAssistant.App' -ErrorAction SilentlyContinue) {
    Write-Output 'DEPLOY-FAIL: 旧实例 40 秒未退出，中止（勿带锁覆盖）'
    exit 6
}
Write-Output '旧实例已退出'

robocopy $Src $Dst /E /NJH /NFL /NDL /R:5 /W:2
$rc = $LASTEXITCODE
if ($rc -ge 8) {
    Write-Output "DEPLOY-FAIL: robocopy rc=$rc"
    exit $rc
}
Write-Output "robocopy 完成 rc=$rc"

$natives = @('OpenCvSharpExtern.dll', 'WebView2Loader.dll', 'onnxruntime.dll',
    'onnxruntime_providers_shared.dll', 'opencv_videoio_ffmpeg4100_64.dll')
foreach ($n in $natives) {
    # 09-08 修正：新版 SDK 原生库布局在 runtimes\win-x64\native\（顶层扁平布局已不再生成），
    # 两种布局任一存在即通过；真伪由端到端验收的预热事件兜底（预热=OpenCV/ONNX 真实加载）。
    $topLevel = Test-Path (Join-Path $Dst $n)
    $ridNative = Test-Path (Join-Path $Dst "runtimes\win-x64\native\$n")
    if (-not ($topLevel -or $ridNative)) {
        Write-Output "DEPLOY-FAIL: 缺原生库 $n（顶层与 runtimes 布局均无）"
        exit 5
    }
}

$ver = (Get-Item (Join-Path $Dst 'CurrencyWarsAssistant.App.dll')).VersionInfo.ProductVersion
Write-Output "部署版本：$ver"

$verify = 'C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826\artifacts\verify_deployment.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $verify -TargetDir $Dst
$vr = $LASTEXITCODE
if ($vr -ne 0) {
    Write-Output "DEPLOY-FAIL: 验收未通过 rc=$vr"
    exit $vr
}

Write-Output 'DEPLOY-OK: 发布+验收全部通过'
exit 0

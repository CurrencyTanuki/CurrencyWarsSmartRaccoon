# ISSUE-012 复现：非生产性能测试误阻塞全量回归

## 修前问题

`RecognitionPerformanceTests.MeasureSingleFrameRecognitionTimeAndMemory` 在 ISSUE-011 全量回归中失败：18.52、24.07、8.19 秒，平均 16.93 秒，最快 8.19 秒。该全量 TRX 的 SHA-256 为 `11105456A9E3E5DF2E6AE87144C6A69F7452A31AE176A8142260EA188195D0AF`。

本项又在隔离环境中按原测试单独复现：

```powershell
dotnet test .\tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj `
  -c Release --no-build --no-restore --nologo `
  --filter 'FullyQualifiedName=CurrencyWarsAssistant.Tests.RecognitionPerformanceTests.MeasureSingleFrameRecognitionTimeAndMemory'
```

结果为 0/1，三次 6.03、11.95、5.16 秒，平均 7.71 秒，最快 5.16 秒。原始 TRX：

- `test-results/ISSUE-012-before-old-benchmark.trx`
- SHA-256：`4C3356C3F9CA5EE2B63231AF658D4DBF78FDBBC4F59DFE871EC3F52B18843C55`

修前测试文件与桌面交接原件逐字节一致：4158 字节，SHA-256 `6013E99150F989E8AD6ACD76F494BA973892A4B089D9B7C6597F87FA21B48A1C`。

## 旧测试为何不能代表正式程序

旧测试手工创建两个默认 `WindowsOfflineOcr`，使用 Robust、单 lane 路径；没有正式 PpOCR、中文/英文 Windows Fast fallback、正式并发 lane、页面分类器或 App DI；`enableRobustFallback` 也与正式 App 不同。它显式传入页面 ID，并在同一 analyzer、同一 `RunId="perf"` 上连续测试，仅 `evidenceSourceId` 不同。测试说明按平均 2 秒解释结果，实际断言却只检查最快一次小于 3 秒。

因此它只能作为历史 Windows OCR 手工基准，不能作为最终软件的生产性能门。

## 正式同构复现入口

正式 App 已提供不修改代码即可运行的入口：

```powershell
$env:CURRENCY_WARS_PHASE2_TIMING='1'
dotnet .\src\CurrencyWarsAssistant.App\bin\Release\net8.0-windows10.0.19041.0\CurrencyWarsAssistant.App.dll `
  --phase2-batch-test --input <input-dir> --output <output-dir> --no-annotations [--continuous-sequence]
```

该入口在完整配置和 DI 建立后先执行 `Phase2RecognitionWarmUpService`，再调用与实时管线相同的 `ISituationScreenshotAnalyzer`。正式配置为 PpOCR 8 lanes、中文/英文 Windows Fast fallback 各 4 lanes、正式页面分类器和 `enableRobustFallback:false`。当前 PpOCR 源码明确使用 CPU session，不能把本轮结果描述为 GPU 推理。

批测计时覆盖文件解码和正式分析；`perf:situation total` 是实际识别分析耗时。它不覆盖桌面捕获、实时队列、状态持久化和界面刷新，不能外推为完整实时链端到端性能。

## 输入

共使用 13 个唯一文件哈希，其中 12 个为有效 16:9 输入；通过复制同一原图和重复进程得到 37 次有效测量。

| 文件 | 尺寸 | SHA-256 | 结果 |
|---|---:|---|---|
| phase2/125924.png | 2559×1439 | `ACE7C486E946F7AE60CF6C863472D11F2DF3B0873C726FFF0107FE39328946AA` | 有效 |
| phase2/130104.png | 2559×1439 | `4C5284609135B81076A4B292E4BCEFA9F8DF539DADECFACF4849089FEC0695D7` | 有效 |
| phase2/130112.png | 2559×1439 | `1128E3F8E391E3DDA57A9F23683490876E1C2E5D68BA791FC8A23E722F710B70` | 有效 |
| phase2/130123.png | 2559×1439 | `AC8BAFD2889BB904FD763BA120B86BF96C6A90D6E2138B4A220EF926B113D325` | 有效 |
| phase2/132307.png | 2559×1439 | `9EFC824CAF0CFDA636537300E3D15141FC7D9A9C633590ACCC2C70C2AC8E3AE4` | 有效 |
| phase2/132328.png | 2559×1422 | `B929FD1A7F5C26C1AB6DCC42C21B038495F6003C3B8B25C4A24F7D552D42176D` | 非 16:9，正确拒绝，不计性能 |
| prep_3_7_142723217.png | 2560×1440 | `5ABD13EBB47B3D4C897F7E027D8357ABE1C387AFEA589C2BA02CC6B1F878FE96` | 有效；与旧测试同图 |
| video_prep/prep_0.png | 1920×1080 | `F1A0F57BEE48D716B83210C87103D0F4EF2630E93C7FFDBFD9C0AEE4C9C0DA87` | 有效 |
| video_prep/prep_1.png | 1920×1080 | `6B9F2438FB93692040D3907AFC504BE11064BE459711BC1A4202D3CB7F1C7831` | 有效 |
| video_prep/prep_2.png | 1920×1080 | `628CE46D405FFEA1297110AA7A418703314DD570450373596A0D7AA1A57DCE50` | 有效 |
| video_prep/prep_3.png | 1920×1080 | `48F08C62ADC43A65163705BBEE87A9CB47CB25AF28C4BAE761D68E5AE500F7A8` | 有效 |
| video_prep/prep_4.png | 1920×1080 | `6ADCECBAEAE367DAFB530B67C7CF33C0958AEF99D635C83119F6BD6D483BCDF7` | 有效 |
| video_prep/prep_5.png | 1920×1080 | `EB014E89879B7018628AE8D45AC3FA66D7D82DFC2C272D7454A46AA063C10D66` | 有效 |

`132328.png` 的宽高比与 16:9 相差约 0.0218，超过代码 0.01 容差，程序返回明确 `InvalidDataException`；它是无效素材，不是性能故障。

# ISSUE-012 验证记录

## 运行环境

- CPU：13th Gen Intel Core i5-13400F，10 核 / 16 逻辑处理器；
- 内存：31.8 GB；
- 操作系统：Windows 11 专业版 10.0.26200（build 26200）；
- .NET SDK：8.0.423；
- PowerShell：5.1.26100.8875；
- Release App DLL：664576 字节，SHA-256 `E60E3E355D57E44F572174536AD0E656BB2017B15C9F1210E5676B9B13D57A24`；
- 环境变量：每个批测进程启动前设置 `CURRENCY_WARS_PHASE2_TIMING=1`；
- PpOCR：源码使用 CPU ONNX session（`usesGpu=false`）。

本轮为同一台机器上的顺序运行，不代表其他硬件；运行时没有把桌面捕获、实时队列和持久化纳入批测计时。

## 七轮正式 App 批测

每轮均使用 Release App DLL、`--no-annotations`、独立输出目录；进程退出码均为 0，startup 日志均到达 `batch-analysis-completed`。完整输出哈希见 `artifact-sha256.md`。

公共命令前缀为：

```powershell
$env:CURRENCY_WARS_PHASE2_TIMING='1'
dotnet <Release-App-DLL> --phase2-batch-test --input <input> --output <output> --no-annotations
```

`<Release-App-DLL>` 的实路径为工作副本根下 `src/CurrencyWarsAssistant.App/bin/Release/net8.0-windows10.0.19041.0/CurrencyWarsAssistant.App.dll`。下表 run-01 的 `tests/...` 以工作副本根为基准；run-02～07 的 `inputs/...` 与 `outputs/...` 以本 ISSUE-012 目录为基准。原始 report 同时保存了展开后的绝对 source/output 路径。

各轮参数如下；run-06、run-07 在公共参数后额外传入 `--continuous-sequence`：

| 轮次 | `--input` | `--output` | 额外参数 | 退出码 |
|---|---|---|---|---:|
| run-01 | `tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-2026-07-28` | `outputs/run-01-phase2-six` | 无 | 0 |
| run-02 | `inputs/phase2-valid-five` | `outputs/run-02-phase2-valid-five` | 无 | 0 |
| run-03 | `inputs/phase2-valid-five` | `outputs/run-03-phase2-valid-five` | 无 | 0 |
| run-04 | `inputs/prep3_7-five-copies` | `outputs/run-04-prep3_7-five-copies` | 无 | 0 |
| run-05 | `inputs/video-prep-native-1920` | `outputs/run-05-video-prep-native-1920` | 无 | 0 |
| run-06 | `inputs/prep3_7-five-copies` | `outputs/run-06-prep3_7-continuous` | `--continuous-sequence` | 0 |
| run-07 | `inputs/video-prep-native-1920` | `outputs/run-07-video-prep-continuous` | `--continuous-sequence` | 0 |

| 轮次 | 输入 | continuous | 有效/拒绝 | 说明 |
|---|---|---:|---:|---|
| run-01 | 原 phase2 六图 | 否 | 5/1 | 132328 非 16:9 被明确拒绝 |
| run-02 | phase2 有效五图副本 | 否 | 5/0 | 独立进程重复 |
| run-03 | phase2 有效五图副本 | 否 | 5/0 | 独立进程重复 |
| run-04 | prep3_7 同图五份 | 否 | 5/0 | 与旧测试同一图片哈希 |
| run-05 | 原生 1920×1080 video_prep 六帧 | 否 | 6/0 | 原生 1080P 图组 |
| run-06 | prep3_7 同图五份 | 是 | 5/0 | 同一 run 连续序列 |
| run-07 | 原生 1920×1080 video_prep 六帧 | 是 | 6/0 | 同一 run 连续序列 |

所有 37 次有效测量只对应 12 个唯一图片哈希，不把复制和复跑描述成独立截图。

### 统计

| 范围 | n | min | median | mean | p95 | max | >1.5s | >2s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| batch 总耗时 | 37 | 592.6 | 1439.3 | 1330.5 | 1926.8 | 2107.0 | 14 | 1 |
| `perf:situation total` | 37 | 526.1 | 1389.1 | 1281.0 | 1869.1 | 2062.3 | 9 | 1 |
| battle analyzer | 9 | 526.1 | 603.0 | 664.1 | 891.7 | 891.7 | 0 | 0 |
| preparation analyzer | 28 | 1156.9 | 1455.4 | 1479.3 | 1869.1 | 2062.3 | 9 | 1 |

唯一 analyzer 超 2 秒样本：run-07 `prep_0.png`，2062.3 ms，SHA-256 `F1A0F57B...0DA87`；后五帧全部 1156.9–1460.8 ms。结论是“没有持续超 2 秒，稳态可用，但冷首帧仍有未优化余量”，不是“所有帧均达标”。

## 测试契约修正

- 生产文件修改：0；
- 测试修改：`RecognitionPerformanceTests.cs` 仅一行 `[Fact]` 改为带理由的静态 Skip；
- 测试主体和断言未改；
- App DLL 修前后 SHA-256 均为 `E60E3E...57A24`。

修后定向测试：0 失败、0 通过、1 NotExecuted；Skip 理由明确指出它是 legacy non-production Windows OCR benchmark。TRX：

- `test-results/ISSUE-012-after-old-benchmark-explicit-final.trx`
- SHA-256：`5561351B2C4F4C007B73D552BFBBD2317BDEE4AE782B637FD4DF659DF8171AF7`

这不是“测试通过”，而是将错误分类的性能门从自动回归中移除；正式性能证据来自上述 App batch 七轮实测。

## 关联回归

过滤范围包含批测入口、PpOCR、Phase2EvidenceReview 和识别队列测试：44/44 通过。

- TRX：`test-results/ISSUE-012-after-related.trx`
- SHA-256：`DF1E13967B118C023CC15F79C804711FC61DE9C027B53E1222BFF47A17C27110`

## 全量回归

- 最终：807 项 = 804 Passed / 1 Failed / 2 NotExecuted；
- TRX：`test-results/ISSUE-012-after-full.trx`；
- SHA-256：`EF4A48B69D0372E7E15B23B112D11232AFC48425A843C9550229EFB39B617B17`；
- ISSUE-011 基线：807 项 = 804 Passed / 2 Failed / 1 NotExecuted，SHA-256 `11105456...5D0AF`；
- 逐唯一 testName 对比：807 对 807，新增 0、缺失 0、结果变化 1；唯一变化为本项 `RecognitionPerformanceTests.MeasureSingleFrameRecognitionTimeAndMemory` 从 Failed 变为 NotExecuted；其他 806 项结果不变。

剩余 1 个失败是既有 `HistoricalUiFieldCoverageTests.Registry_CoversEveryPublicPropertyOfFinalDataTypes`，与本项无关；另一条 `Phase2RecognitionPerformanceTests` 仍为原有静态 Skip，不能算通过。

## Release 构建

- solution Release build：退出码 0；
- 0 警告、0 错误；
- log：`build/ISSUE-012-release-build.log`，SHA-256 `1EB71406728C3C53E08F00AA31C7C85935F63E053C25D4A38831AAEBB48C4FB5`；
- binlog：`build/ISSUE-012-release-build.binlog`，SHA-256 `917E8B0555DFED05F89C2B322FBC3CA33A2C8112B081BCDB4951661A27DBBEDE`。

## 验收边界

本项只证明：正式识别分析没有稳定超 2 秒，稳态满足当前节律；历史非生产测试不再误阻塞全量回归。不得据此宣称：

- 冷首帧绝对低于 2 秒；
- 桌面捕获、实时队列、状态合并、持久化和 UI 的端到端性能已经验证；
- 两条静态 Skip 性能测试已通过；
- 全项目已完成。

冷首帧 2.062 秒余量和最终实时链实机延迟继续标记为未优化/待最终实机确认。

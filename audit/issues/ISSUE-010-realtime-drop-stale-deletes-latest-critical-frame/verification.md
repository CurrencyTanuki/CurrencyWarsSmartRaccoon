# ISSUE-010 验证记录

当前状态：已完成并获独立只读审查员批准。

## 修前基线

- 生产文件 SHA-256：`2E967F695DFDBC011CE7BAA51001987B7198B9C009D83AA7D022485FF8BD147D`
- 原测试文件 SHA-256：`DDC89F1B742106EE6765C383D6891829948C10412516EA26CCF4527E5E830701`
- 加入独立回归测试后的测试文件 SHA-256：`FBE5E023F541C6D9FB18C9D85EE3648C3CDBB3D4AE14789FBBEB90014FD525A2`
- ISSUE-009 全量基线：806 项，798 通过、7 失败、1 NotExecuted。

## 修前失败

- 命令：`dotnet test tests/CurrencyWarsAssistant.Tests/CurrencyWarsAssistant.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Phase2RecognitionQueueTests.DropStaleFramesWithMultipleCriticalFramesKeepsNewestCriticalBeforeLatestFrame`
- 结果：0/1，退出码 1；
- 原始结果：`test-results/ISSUE-010-before-focused.trx`
- SHA-256：`F41FB57EFC60DA96C7D3BEAB3A92CF11FCC28E5441F4A20C1433D5166C5767E8`
- 失败位置：`Phase2RecognitionQueueTests.cs` 第 26 行；
- 失败内容：第一项 `IsCritical` 期望 `true`、实际 `false`；当前实现把应保留的最新关键帧删掉，首项已是最后普通帧。
- 运行时生产文件 SHA-256 仍为交接基线 `2E967F695DFDBC011CE7BAA51001987B7198B9C009D83AA7D022485FF8BD147D`。

## 最终修改范围

唯一生产文件：

- `src/CurrencyWarsAssistant.Tasks/Phase2RealtimeRecognitionPipeline.cs`
- 修前/交接 SHA-256：`2E967F695DFDBC011CE7BAA51001987B7198B9C009D83AA7D022485FF8BD147D`
- 修后 SHA-256：`BD1666448830B6C569ABD7453C3E0F61CD74B374CF334DF7F2F963620F475DB2`
- 差异仅为：删除无效 `keepCritical` 标志，命中 `lastCritical` 时从无条件删除改为 `break`。

唯一测试文件：

- `tests/CurrencyWarsAssistant.Tests/Phase2RecognitionQueueTests.cs`
- 交接 SHA-256：`DDC89F1B742106EE6765C383D6891829948C10412516EA26CCF4527E5E830701`
- 最终 SHA-256：`FBE5E023F541C6D9FB18C9D85EE3648C3CDBB3D4AE14789FBBEB90014FD525A2`
- 只新增一条进入 `items.Count > 2` 缺陷分支的回归测试；既有测试未修改。

## 修后自动化验证

1. 针对性测试：
   - `test-results/ISSUE-010-after-focused.trx`
   - SHA-256：`550AEA7E23E0B539BFDEBC06204CE9EE6F9920E5443F6C4D9AA52D1DA7348697`
   - 1/1 通过。
2. 全队列测试类：
   - `test-results/ISSUE-010-after-queue-class.trx`
   - SHA-256：`40D41215ED9F35970953F69353F15D55996178DB9EA49FFFF3A1914CC19CDEC8`
   - 4/4 通过。
3. 关联回归：`Phase2RecognitionQueueTests`、`Phase2RecognitionFeedTests`、`Phase2RealtimeFrameBufferTests`
   - `test-results/ISSUE-010-after-related.trx`
   - SHA-256：`83C9FC8416A8F6A6D1A517CEB79BCAFB4C706080C5E88B3CEDA7B65208539C7B`
   - 46/46 通过；覆盖关键帧拥塞、普通帧 churn、页面边界前驱、单帧 challenge success 锁定和后台实时捕获。
4. 全量回归：
   - `test-results/ISSUE-010-after-full.trx`
   - SHA-256：`7A6FC352C2A7589014999D9697FDB25AC2BD7DF30AEE092788E75ED870F23799`
   - 807 项：799 通过、7 失败、1 未执行/跳过；命令退出码 1。
   - 与 ISSUE-009 基线 `B4820D20BDC7C31EFB18817F5BD028256FB646F50A8DEB8251EE98D53EC82117` 逐测试名比较：只新增本项一条测试且通过；既有测试结果变化 0、缺失 0、新增失败 0。
   - 7 个失败集合完全不变：5 个 `EquipmentDataPipelineTests`（缺工具资产）、1 个 `HistoricalUiFieldCoverageTests`、1 个 `RecognitionPerformanceTests`。硬编码跳过的 `Phase2RecognitionPerformanceTests` 仍未验证。本项不宣称修复这些问题。

## Release 构建

- 命令：`dotnet build CurrencyWarsAssistant.sln -c Release --no-restore`
- 退出码 0，0 警告、0 错误。
- binlog：`test-results/ISSUE-010-release-build.binlog`
  - SHA-256：`007284336B5A703B46FFA1E309F6A0E898A0E502BDB16797BA26B03BC1C3BFCA`
- 文本日志：`test-results/ISSUE-010-release-build.log`
  - SHA-256：`A8753A912A8F00693DD98223EFD00F7DF30685E0DCC35864DFF59658541E270F`
- 最终 `CurrencyWarsAssistant.Tasks.dll` SHA-256：`DC86E5F9A36BF7814A5260BA8EA078E7E30FB4E5D81C2B8C461C017EE239E179`；程序集时间晚于生产源码，以上针对性、关联和全量 TRX 又晚于该程序集。

## 验收边界

证据证明队列在 `[旧关键帧, 最新关键帧, 最新普通帧]` 时不再静默删除最新关键帧，且剩余项与信号量计数一致。它不证明 OCR 达到性能预算，也不替代真实游戏中全部结算/终局页面的端到端逐图验收。

## 独立审查结论

独立只读审查员最终结论：`APPROVE ISSUE-010`。

审查员独立核对了交接原件与修前哈希、生产/测试逐行差异、修前 0/1 原始 TRX 及时间顺序、修后 1/1、4/4、46/46、807 项全量逐测试名差分、Release 构建、队列不变量、信号量计数和生产调用可达性。批准严格限于“陈旧帧清理保留最新关键帧”；识别性能预算、7 个既有失败和真实游戏终局 E2E 仍未完成。

# ISSUE-014 验证记录

状态：当前无法复现；独立审查已批准此限定处理。

## 修改边界

- 当前生产代码修改：0
- 当前测试代码修改：0
- 仅新增本问题的审计文档和后续原始测试产物。

## 当前基线

- ISSUE-013 全量：807 项，805 Passed、0 Failed、2 NotExecuted。
- 目标测试在 ISSUE-013 全量中 Passed，耗时 6.426 s。

## 当前复现与压力诊断

命令主体：

```powershell
dotnet test tests/CurrencyWarsAssistant.Tests/CurrencyWarsAssistant.Tests.csproj `
  -c Release --no-build --no-restore --nologo --verbosity quiet `
  --filter FullyQualifiedName=CurrencyWarsAssistant.Tests.RewardShopCloseIntegrationTests.DisabledRefreshShopIsRecognizedClickedAndVerifiedClosed `
  --results-directory <unique-output> `
  --logger "trx;LogFileName=<unique-name>.trx"
```

| 组 | Passed | Failed | 最短 | 最长 | 中位数 |
|---|---:|---:|---:|---:|---:|
| isolated-repeat | 12 | 0 | 1.847 s | 1.908 s | 1.875 s |
| concurrent-8 | 8 | 0 | 3.361 s | 3.558 s | 3.488 s |
| concurrent-16 | 16 | 0 | 5.028 s | 5.756 s | 5.533 s |
| concurrent-24 | 24 | 0 | 6.002 s | 8.474 s | 7.352 s |

- 合计：60 Passed、0 Failed、0 NotExecuted。
- 运行器在本轮执行期间观察到所有进程退出码为 0；逐进程退出码没有另建独立清单，原始 TRX 全部为 Passed 且正常结束。
- TRX 清单：`test-results-manifest.csv`。
- 清单 SHA-256：`0B58078BCE890CAD676F6D0032ACCC6B3B3A72B3A179F2397102D0F62CDAD11E`。
- ISSUE-013 最新全量仍是适用基线，因为本项没有修改生产代码、测试代码、项目文件或运行资产：807 项，805 Passed、0 Failed、2 NotExecuted；目标测试 Passed。

## 验收边界

- 当前没有可重复失败，因此没有进行代码修改，也没有宣称“已修复”。
- 当前证据支持的结论仅为：历史时序红绿在最终工作副本上无法复现，正常隔离与最高 24 进程受控并发均通过。
- 此测试的页面图片和分类器是真实的，但点击与画面切换是测试替身；真实游戏商店关闭仍标记“未验证”。
- 两条静态性能测试仍为 NotExecuted，不计作通过。

## 独立审查

- 独立只读审查员结论：`APPROVE ISSUE-014（限定范围）`。
- 批准内容仅为“零代码/测试修改、当前无法复现”；队列必须保留“真实游戏关店未验证，未来复发时重开”。
- 未批准“真实游戏关店已验证”“历史根因已修复”或“产品功能已完成”。

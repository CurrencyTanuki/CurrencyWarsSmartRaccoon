# ISSUE-014：商店关闭集成测试历史红绿（复现记录）

状态：当前版本无法复现；未修改生产代码或测试代码。

## 范围

- 测试：`CurrencyWarsAssistant.Tests.RewardShopCloseIntegrationTests.DisabledRefreshShopIsRecognizedClickedAndVerifiedClosed`
- 生产路径：`RewardStageAutomationController.CloseShopAsync`、`ReadStablePageAsync`、`WaitForPageAsync`
- 输入：项目现有 `reward_shop_after_two_purchases_2048x1152.png` 与 `preparation_1_1_after_shop_batch_2048x1152.png`
- 该测试使用真实页面分类器和真实截图，但捕获切换及点击是测试替身；不能外推为真实游戏窗口端到端验收。

## 原件一致性

2026-08-09 核对工作副本与 20260808 交接原件：

| 文件 | SHA-256 | 结果 |
|---|---|---|
| `RewardStageAutomation.Shop.cs` | `2E96F07A59CEAA7D356905857392605931BAEB95DF150AAB00A0DEB2EBB6406D` | 一致 |
| `RewardStageAutomation.cs` | `93434D742689C5582E87C66DE01D206F0D646AB0FC51A0349C38AA3EA0E2C77B` | 一致 |
| `RewardStageShopPurchase.cs` | `EB8FC38DBD5EC631785A2BFE4E9A4289EEEDE93F0986588DD75120B80096E535` | 一致 |
| `RewardShopCloseIntegrationTests.cs` | `BCA5FFE436043D4493545EFA2FEFF5008AEAF8D477DEEC66C66EB55656C46E29` | 一致 |

因此历史红绿不是本轮 ISSUE-001 至 ISSUE-013 对上述四个文件的直接修改造成。

## 已保存历史结果

| 基线 | 结果 | 耗时 |
|---|---:|---:|
| ISSUE-002 全量 | Failed | 31.535 s |
| ISSUE-003 全量 | Failed | 28.020 s |
| ISSUE-004 全量 | Passed | 6.452 s |
| ISSUE-005 全量 | Failed | 31.125 s |
| ISSUE-005 随即隔离复跑 | Passed | 1.984 s |
| ISSUE-006 至 ISSUE-013 保存的全量运行 | 全部 Passed | 4.617–7.806 s |

三个失败均停在测试第 57 行 `Assert.True(closed)`；没有更细的事件输出。相同源码在 ISSUE-005 全量失败后隔离复跑立即通过，说明现有证据尚不能确定为稳定产品缺陷。

## 当前复现结果

使用最终 ISSUE-013 Release 测试程序集、`--no-build --no-restore`、完整限定名过滤器运行。每个样本均由独立 `dotnet test` 进程执行并保存独立 TRX：

| 运行方式 | 结果 | 测试耗时范围 |
|---|---:|---:|
| 顺序隔离进程 | 12/12 Passed | 1.847–1.908 s |
| 同时 8 个进程 | 8/8 Passed | 3.361–3.558 s |
| 同时 16 个进程 | 16/16 Passed | 5.028–5.756 s |
| 同时 24 个进程 | 24/24 Passed | 6.002–8.474 s |
| 合计 | 60/60 Passed | 1.847–8.474 s |

并发运行明显放大耗时，但直到 24 个同时进程仍未产生失败。所有 TRX 均保存在 `test-results/`；逐文件结果、耗时和 SHA-256 在 `test-results-manifest.csv`，清单 SHA-256 为 `0B58078BCE890CAD676F6D0032ACCC6B3B3A72B3A179F2397102D0F62CDAD11E`。

结论只说明当前最终测试程序集无法复现历史红灯。真实游戏窗口、真实点击、动画过渡和焦点切换没有由该测试覆盖，继续标记未验证。

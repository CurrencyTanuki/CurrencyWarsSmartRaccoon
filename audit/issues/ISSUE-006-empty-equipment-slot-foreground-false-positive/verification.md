# ISSUE-006 验证记录

## 结论边界

本项只修复一个已用真实准备页截图稳定复现的问题：相邻装备图标的边缘进入空槽裁剪后，旧前景门禁把空槽判成有内容，随后产生错误的 `Unknown`、候选装备和 pending。

本项不宣称完成以下事项：

- 商店截图应为候选 `066/105`、实际仍为 `080/119` 的独立裁剪/匹配问题；
- 1920 商店布局的 Back 行装备区域问题；
- 原生 2048×1152 截图验收（当前 fixture 中不存在该分辨率的原生截图）；
- 全量装备图标—名称—ID—分类的专项核对；
- 全项目性能、历史界面、数据工具链和最终软件包验收。

## 根因与最小生产修改

真实输入：

- `inputs/preparation-1-7-user.png`
- SHA-256：`52D3183D7090B4C06068BF582E2A700D31CBD8015AFFD108245B91B155161D0E`
- 与项目原 fixture 逐字节相同。

生产文件：

- `src/CurrencyWarsAssistant.Tasks/Phase2OperationalScreenshotAnalyzer.cs`
- ISSUE-006 修改前（已批准的 ISSUE-005 最终版本）SHA-256：`E8A159177C1C3C5D13FA5D9F5B286D2EAC6730A7A75EE7D5E854E367DAA283E8`
- 修改后 SHA-256：`099F2AB550276B24583C1D62D074F5D98F2918059F5D3F88B54BBEC3C5866E69`

按生产代码完全相同的亮度标准差和相邻像素跳变算法，实际 58×60 装备槽得到：

| 槽 | 全宽 stddev / transition | 中央 70% stddev / transition | 画面真值 |
|---|---:|---:|---|
| 0 | `20.7634 / 0.025285` | `7.5821 / 0.008510` | 空 |
| 1 | `48.1575 / 0.219088` | `51.7152 / 0.266380` | 有装备 |
| 2 | `7.3914 / 0.007746` | `7.9456 / 0.008510` | 空 |

槽 0 只有最右五分之一标准差升到 `35.295`；该位置正是相邻槽 1 红色图标的泄漏边缘。旧门禁对整槽计算，跳变率恰好超过 `0.025`，因此误判有前景。

最终生产修改只有：

1. 新增装备槽专用 `HasCenteredEquipmentForeground`，门禁计算时左右各内缩 15%；
2. `RecognizeEquipmentSlots` 只把装备前景预过滤改用该门禁；
3. 全局 `HasDetailedForeground`、阈值 `14 / 0.025`、所有其他调用者不变；
4. 门禁通过后仍把原始完整装备槽交给图标匹配器，区域、模板、候选排序和置信度计算不变。

不能粗暴恢复旧全局阈值 `0.035`，因为交接材料和既有真图已证明该阈值会漏掉 Back 行小图标。

## 修复前复现

既有真实端到端测试：

- `test-results/ISSUE-006-before-existing.trx`
- SHA-256：`AED182CA38CC8A960231437E221F9D267B0AB2832B38AFBCAC92B8779DFA5BC5`
- 0/1；角色 24 槽 0 期望 `Empty`，实际 `Unknown`，错误候选 `[073,112]`；真实槽 1 为 `Unknown [061,100]`。

新增最小真实截图契约测试后、修改生产代码前：

- 测试：`EmptyPreparationEquipmentSlotDoesNotCreateUnknownOrPendingEvidence`
- `test-results/ISSUE-006-before-focused.trx`
- SHA-256：`33E99EED83122EDA69A264F203CE1C1088F57AC27858E5FEF34206F0EE9D5F31`
- 0/1；首个断言仍为槽 0 期望 `Empty`、实际 `Unknown`。

该测试约束完整用户影响：空槽无 ID/候选/pending；相邻真实歧义装备仍为 `Unknown`、不保存具体 ID、保留 `061/100`，且只为真实装备槽生成 pending。

## 修复后真实截图和定向测试

### 问题图端到端

- `test-results/ISSUE-006-after-focused.trx`：1/1，SHA-256 `114AAF73876C580B01936B0A551BD84B5767A79611D73D954C4AB25138F72CF7`
- `test-results/ISSUE-006-after-existing.trx`：1/1，SHA-256 `71D16DCFB194BA28E15FFAA503E37C3BB9F2243D9D5054257BEC262714A8FD3A`
- 最终输出：槽 0 `Empty`、槽 1 `Unknown [061,100]`、槽 2 `Empty`；其余页面、节点、难度、经济、阵容、库存输出保持可用。

### 原生分辨率与尺度边界

最终门禁测试：

- `test-results/ISSUE-006-after-centered-gate-native-and-smoke.trx`
- SHA-256：`C6B6BF8EC51BDE3DEF23120700B83786B4BF9544EB7E60BF61FC104BDF91C016`
- 4/4。

覆盖内容：

1. 原生 1920×1080 用户录屏帧 `video_prep/prep_5.png`，SHA-256 `EB014E89879B7018628AE8D45AC3FA66D7D82DFC2C272D7454A46AA063C10D66`：按生产 `RewardShopCharacterSlots1920` 和 Front compact 裁剪，Front0 为 `[真,真,真]`，Front1/2/3 均为 `[假,真,假]`；该标注经目视及连续六帧像素稳定性核对。
2. 原生 2560×1440 `prep_3_7_142723217.png`，SHA-256 `5ABD13EBB47B3D4C897F7E027D8357ABE1C387AFEA589C2BA02CC6B1F878FE96`：Front0 三件真装备全部保留；按生产动态 `BackCharacterSlots1920(6)` 的 Back 6 三件、Back 7/8 中间装备全部保留。
3. 将真实截图插值到 1920×1080 和 2048×1152 后的尺度 smoke，以及 `user_ref_000032_prep22.png` 可见图标保留。此项只证明插值/尺度鲁棒性，不冒充原生分辨率真值。

项目现有 fixture 中没有原生 2048×1152 游戏截图，因此原生 2048 验收明确记为 **未验证**，需要后续新增实机截图。

首次多分辨率测试错误地把 2559×1439 问题图插值缩放后套静态槽位，并断言空槽真值；插值会移动相邻边缘，导致 1920/2560 断言失败。原始失败证据已保留为 `ISSUE-006-after-centered-gate-multires.trx`（SHA-256 `0A997B2C8BC9424BB6F9091CD82AE5C08CEA638F33C443BDCB7BEC2204DEC688`）。该无效构造已撤回，没有为使其变绿而放宽生产逻辑。

## 关联与边界回归

最终关联回归：

- `test-results/ISSUE-006-after-related-native-final.trx`
- SHA-256：`3A18B11D841B7E8E68CCE1DDE6C112D1905384D8D5B4F27EFE02BD62744847B4`
- 19/19。

覆盖：

- 问题图最小契约和既有完整端到端；
- 原生 1920/2560 与尺度 smoke；
- ISSUE-005 歧义身份保护；
- `prep_3_7` 前/后台真装备；
- `125924.png`、`132307.png` 的 `AdvancedEquipment` pending；
- 图标目录显式歧义；
- 装备状态合并/持久化指纹；
- `user_ref_000032/035/036` 既有装备回归。

独立边界红灯：

- `test-results/ISSUE-006-after-shop-boundary.trx`
- SHA-256：`A86CBFEFED2B6974A6C057D5F52E12D00C2408C944A44BDA76E9E839CDDB967A`
- 0/1；状态已正确为 `Unknown`，但候选仍为 `080/119`，期望 `066/105`。这是另一个已隔离问题，未在本项修改匹配逻辑。

## 最终全量回归

命令：

```powershell
dotnet test .\CurrencyWarsAssistant.sln -c Release --no-restore `
  --logger "trx;LogFileName=...\ISSUE-006-after-full-native-final.trx"
```

结果：

- `test-results/ISSUE-006-after-full-native-final.trx`
- SHA-256：`724D0AF934FFD3A68548C5A1D077766BC36A63F51831EFE0A5E8620547373C24`
- 788 项：779 通过、8 失败、1 跳过；退出码 1，因此不宣称全项目回归通过。
- 相对 ISSUE-005 全量失败集合：新增失败 0。
- 本项原稳定失败 `LiveCapturedPreparationProducesVisibleCoreStateWithinRealtimeBudget` 已转为通过。
- 相对本项此前 787 项的中间全量，8 个失败测试名完全一致；新增的最终原生/尺度测试已通过。

仍失败的 8 项全部已存在于本项修改前：

- 5 个 `EquipmentDataPipelineTests`：工作副本缺 `tools/Invoke-EquipmentDataPipeline.ps1` 及相关 source-package 资产；
- 1 个 `HistoricalUiFieldCoverageTests`：注册表缺 `Health`，进一步审计还发现 `Population`/HTML 展示缺口；
- 1 个 `RecognitionPerformanceTests`：最快单帧 `7.89s`，超过 2 秒预算；
- 1 个商店装备候选错组：`066/105` 被识别为 `080/119`。

跳过项：`Phase2RecognitionPerformanceTests.MeasureFullAnalysisTimePerFrame`，保持 `NotExecuted`，不得算通过。

## 构建

命令：

```powershell
dotnet build .\CurrencyWarsAssistant.sln -c Release --no-restore --nologo `
  -bl:.\audit\issues\ISSUE-006-empty-equipment-slot-foreground-false-positive\build-release.binlog
```

结果：成功，0 警告、0 错误。

- `build-release.binlog` SHA-256：`3DE805ABE948C1080EB04210D4BFAE0470D90A8062C0EAC322BC93A811813089`
- `CurrencyWarsAssistant.Tasks.dll` SHA-256：`D8F59E5E4B50702CFEC9CFC954DC7FEEE941C004C6C55585FA4C04751FF4E52A`
- `CurrencyWarsAssistant.Tests.dll` SHA-256：`8C183C52AAF2C3A61965FC553FBBBFA463154FCB0D569DF86C1F2D080AEC37F4`
- 最终测试程序集生成时间晚于测试文件，最终全量 TRX 又晚于程序集，确认运行的是最终二进制。

## 测试代码范围

- `Phase2OperationalCollectionTests.cs`：交接原件 SHA-256 `E7CDB0DCFEA00DB11FD951455975D851767FC87044018A0A8CFFAEEC994E80A5`；当前 SHA-256 `283CF3983CBDF57821BA986143F0972A7566B1A8503162CEE69FDFFB886FC7EB`。除已批准 ISSUE-005 测试外，本项只新增真实问题图契约。
- `EquipmentRecognitionDiagnosisTests.cs`：交接原件 SHA-256 `A47415D44C2EEACA28A90E5574D67FE7E471706926F15BBDB086154F541639AF`；当前 SHA-256 `AD86A724ADF7B9859DE969538C0D24DF3BF070D1AF096901F4D078C28233DEC8`。本项只新增门禁的原生 1920/2560 和尺度 smoke 回归，不修改既有断言。

本项没有修改截图、fixture、装备模板、数据文件或其他测试类。

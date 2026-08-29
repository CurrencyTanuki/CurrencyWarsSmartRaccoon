# ISSUE-007 验证：商店装备候选组错配

## 结论边界

本项只修复 reward-shop compact Front 装备子槽的纵向裁剪几何，使真实
`preparation-shop-1-4.png` 中角色 44 的可见装备从错误共享组
`080/119` 恢复为正确共享组 `066/105`。

修复后仍然安全降级为：

- `Occupancy=Unknown`；
- `EquipmentId=null`；
- `CandidateIds=[066,105]`；
- 不可驱动业务决策，并进入 pending。

没有宣称能在普通/特权共用同一 PNG 时猜出唯一 ID。

## 输入与修复前证据

- 审计输入：`inputs/preparation-shop-1-4.png`；
- 原 fixture：
  `tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-live-2026-07-29/preparation-shop-1-4.png`；
- 两者 SHA-256：
  `84401946415672084A0BE47ADB39B69A6C28D5710FC44E18878C31BCD3458A24`。

修复前既有端到端测试：

- TRX：`test-results/ISSUE-007-before-existing.trx`；
- SHA-256：
  `1ADB2CD94B7F280FB80EAE5800CDACC35E9B10FD96B2027E24675381E3C2C2F2`；
- 0/1，预期 `[066,105]`，实际 `[080,119]`。

修复前新增直接识别契约：

- TRX：`test-results/ISSUE-007-before-focused.trx`；
- SHA-256：
  `0712620BC7B0CD59A9AB8C7A8C7D5E500D11061EC4835A924993856B5A1F426E`；
- 0/1；当前裁剪最高为错误组 `080/119`。

根因单变量和二维网格原始证据见 `root-cause.md`。最终选择
`Y=0.83H / Height=0.26H`：它不是唯一“门禁零误差”组合，而是在“问题图
正确组排第一 + 六张原生 1080P 连续帧 72 个子槽门禁零误差”的组合中，
正确组得分唯一最高（`0.851910`）。

## 最小生产修改

唯一属于 ISSUE-007 的生产文件：

- `src/CurrencyWarsAssistant.Tasks/Phase2RecognitionRegions.cs`
  - compact Front Y：`0.90 → 0.83`；
  - compact Front Height：`0.30 → 0.26`；
  - 同步更正错误的历史注释。

交接原件该文件 SHA-256：
`DC5C79155DFF92B8AFA8A048AE2B83198F9CFF5AEE4DD451BD3DB59ECCAAB69A`。

修复后 SHA-256：
`5CE38E7952AD2437F284906BFF0ADA60D51963CD5AE468E7791DF403D9133758`。

逐行 diff 只有上述注释和两个 compact 数值；标准 Preparation、Back、X、
宽度、inventory、matcher、阈值、模板、ID 映射、前景门禁和保存语义均未改。

新增测试文件（交接原件不存在）：

- `tests/CurrencyWarsAssistant.Tests/ShopEquipmentCandidateRecognitionTests.cs`；
- SHA-256：
  `43F262F44551F092CF8CFD5EDC66C1791CDBAA30B7829CB29358D243DEB27BE9`。

临时诊断测试在保存 before TRX 后已删除；最终测试只保留可重复的行为契约。

## 针对性测试

### 问题图直接识别 + 既有生产端到端

- TRX：`test-results/ISSUE-007-after-focused.trx`；
- SHA-256：
  `3DA88A40F217612975CB258FAC4C631748A8C83DF6E9998D03560267D8E83BBF`；
- 2/2 通过。

关键原始输出：

```text
currency_wars_equipment_066: 0.851910; exact=False;
candidates=[currency_wars_equipment_066|currency_wars_equipment_105]
Front[0]=currency_wars_character_44;
slot1:Unknown/-/0.852/[currency_wars_equipment_066|currency_wars_equipment_105]
```

相邻槽仍为 `Empty`，阵容、Front44/Back43、库存等既有断言全部越过并通过。

### 原生商店多图保护

- TRX：`test-results/ISSUE-007-after-native-shop-regression.trx`；
- SHA-256：
  `7DFC01DE6503BAC04B0193818BCEDB31A206E1BA784CFD4D31ABBD5508FD32CF`；
- 6/6 通过。

覆盖：

- `video_prep/prep_0..5.png` 六张原生 1920×1080 连续帧，4×3 槽的
  `TTT / FTF / FTF / FTF` 前景真值逐帧稳定；
- `shop_open_1_1.jpg`、`shop_open_1_2.jpg`、
  `reward_shop_after_two_purchases.jpg` 和实际为 2559×1439 的
  `reward_shop_after_two_purchases_2048x1152.png`；
- 只检查画面中真实存在角色的卡位，与生产调用路径一致；这些角色的三个
  装备槽均保持空。

一次过宽的测试契约及其修正理由完整保存在
`test-results/ISSUE-007-test-contract-correction.md`，未将测试修正伪装成产品修复。

## 关联回归

### 核心保护集

- TRX：`test-results/ISSUE-007-after-related-core.trx`；
- SHA-256：
  `7497F8C056C996E97FE2F01B997CC796E8030F4B01C0D6DAC02A120AB45B3E31`；
- 44/44 通过。

覆盖 ISSUE-005 模糊装备 `Unknown/pending`、ISSUE-006 空槽、原生装备门禁、
用户参考图、reward-shop 阵容隔离、购买几何以及两张 preparation reference。

### 整组 Phase2 Operational

- TRX：`test-results/ISSUE-007-after-phase2-operational.trx`；
- SHA-256：
  `6B63754134B6F2DD8A5C8D012A3E6231BDE0710A5537D9AE5C929E1F82BD68E8`；
- 175/175 通过。

## 全量回归

- TRX：`test-results/ISSUE-007-after-full.trx`；
- SHA-256：
  `B5C373E43BC54CECB509D4582CDD6556847D1BB824056AE992A33011AAACDF65`；
- 总计 794：786 通过、7 失败、1 跳过；退出码 1。

与 ISSUE-006 最终基线（788：779/8/1；TRX SHA-256
`724D0AF934FFD3A68548C5A1D077766BC36A63F51831EFE0A5E8620547373C24`）
逐测试名比较：

- 新增失败：0；
- 消失失败：
  `Phase2OperationalCollectionTests.ExpandedShopAnalyzerKeepsVisibleFormationWithCompactEvidenceRegions`；
- 新增 6 个 ISSUE-007 测试全部通过；
- 通过数净增 7，正好是 1 个旧失败修复 + 6 个新契约。

剩余 7 个失败均已存在于基线，且调用路径未被本项修改：

1. `EquipmentDataPipelineTests` 5 项：工作副本缺
   `tools/Invoke-EquipmentDataPipeline.ps1`；
2. `HistoricalUiFieldCoverageTests` 1 项：字段覆盖缺 `Health`（并已知还涉及
   `Population`）；
3. `RecognitionPerformanceTests` 1 项：真实单帧最快 8.91 秒，超过 2 秒门槛。

这些必须分别立项；本项没有据此宣称全项目回归通过。

## Release 构建

命令：

```powershell
dotnet build .\CurrencyWarsAssistant.sln -c Release --no-restore
```

结果：退出码 0，0 警告，0 错误。

- binlog：`test-results/ISSUE-007-after-build.binlog`，SHA-256
  `6189CFF7F3E220257BB68559E0043114B53EBBDD37FA620AF372B9A85873315A`；
- 文本日志：`test-results/ISSUE-007-after-build.log`，SHA-256
  `4D1A55EB6C989325DA52C97DD147D8E336932159A69D2B31F42E4D65841164CF`。

## 尚未由本项验证

- 所有 157 项装备的完整图标—名称—ID—分类专项核对；
- 未收集到的其他 UI 比例、非 16:9 或未来版本 reward-shop 外观；
- 两个历史界面的装备文字展示；
- 最终发布包的真实启动、可见界面和正常退出。

这些边界不影响 ISSUE-007 单项验收，但继续保留在项目总审计队列中。

# ISSUE-016 复现：历史 HTML 丢弃已保存的歧义装备候选

状态：修前红测已稳定复现；尚未修改生产代码。

## 用户影响

真实商店截图中的角色装备槽已被识别为“存在装备，但普通/特权共享同一图标，不能唯一确定具体 ID”。识别器和归档正确保留候选 `[066,105]`，共享历史 renderer 却把该槽画成空框。玩家会把“识别到但待确认”误读为“没有装备”，并且两个历史界面都受影响。

## 真实输入与既有真值

- 输入：`tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-live-2026-07-29/preparation-shop-1-4.png`
- 尺寸：2559×1439
- SHA-256：`84401946415672084A0BE47ADB39B69A6C28D5710FC44E18878C31BCD3458A24`
- 已批准 ISSUE-007 真实识别结果：角色44装备槽2为 `Unknown/null/[currency_wars_equipment_066,currency_wars_equipment_105]`，不可驱动决策；对应中文名为“火力风暴潮 / 火力风暴潮•特权”。

## 修前代码证据

- `Phase2OperationalContracts.cs` 的 `CharacterEquipmentSlotState` 明确保留 `EquipmentId`、`CandidateEquipmentIds`、置信度、失败原因和 `CanDriveDecisions`。
- `CompletedRunNodeRecord.FinalPreparationState` 原样进入 canonical archive；`AdvisorJson` 会保存候选。
- 两个实际 WebView2 历史入口共用 `gen_report.py`。
- 修前 `gen_report.py` 只在 `occupancy=equipped` 且存在单一 `EquipmentId` 时画图；其余状态统一输出空框，完全不读取 `CandidateEquipmentIds`。

修前 SHA：

- `src/CurrencyWarsAssistant.App/gen_report.py`：`B7A4B6D1761280AD0D590438D01447795208440C23D460FF21F6D80A41D00AD3`
- `tests/CurrencyWarsAssistant.Tests/Phase2OperationalCollectionTests.cs`：`283CF3983CBDF57821BA986143F0972A7566B1A8503162CEE69FDFFB886FC7EB`

## 复现门槛

新增一条只增加断言、不改既有断言的真实图端到端测试：

1. 运行正式分析器处理上述截图；
2. 断言角色44槽2仍为 Unknown/null/[066,105]、pending存在且不可决策；
3. 用 `CompletedRunRecord + AdvisorJson` 写 canonical `completed-run.v1.json`；
4. 先断言 JSON 同时含 `candidateEquipmentIds` 与 `candidateTemplateIds`；
5. 通过正式 `ReportHtmlRenderer.GenerateFromAsync` 生成 HTML；
6. 期望 HTML 以玩家可读形式显示“待确认：火力风暴潮 / 火力风暴潮•特权”，且相邻两个真实空槽仍为空。

## 修前结果

步骤1–4通过，步骤6按预期失败：

- 定向：0/1，失败于缺少 `class="equip-slot equip-ambiguous"`；
- 标准输出：`persisted candidates=[currency_wars_equipment_066,currency_wars_equipment_105]; html readable=False`；
- TRX：`test-results/ISSUE-016-before-evidence.trx`，SHA-256 `269D0E2CECE7169ADDC193B76C9DC180C1236218C2A7A950C8E5F959615FAD7D`；
- 输入副本：`inputs/preparation-shop-1-4.png`，SHA与原fixture相同；
- canonical archive：`evidence/before-completed-run.v1.json`，176,295 bytes，SHA-256 `E96893386B013DA0396C2E5348F5F36B8952E2E0F289DD68EE60A6D40DB3344F`；JSON含 `candidateEquipmentIds`、`candidateTemplateIds`、066和105；
- HTML：`evidence/before-history-report.html`，11,706 bytes，SHA-256 `68EAC2E4CD4A7E09EADDB3B270F74A7CC4169FFAA9E593036BE47B35C5703BC9`；角色44的三个装备槽全部为相同空框，无“火力风暴潮”、无“待确认”。

因此根因已限定在共享 HTML renderer，不在截图识别、候选形成、pending、canonical archive或 JSON 序列化。

# ISSUE-011 修前复现：交接源码缺装备数据管线脚本与 schema

## 用户影响

交接源码和确认工作副本均缺少装备数据转换脚本及其五份 schema/map。项目已有的装备管线测试无法运行，因而无法从原始 4.4 装备包稳定重建 runtime、验证 schema 拒绝路径或生成可追溯 conversion report。这直接阻断用户要求的“装备图标—名称—本地 ID—来源—核对结果”专项审计。

## 修前文件状态

工作副本不存在：

- `tools/Invoke-EquipmentDataPipeline.ps1`
- `schemas/game-data/1.0.0/equipment/equipment-icon-manifest.schema.json`
- `schemas/game-data/1.0.0/equipment/equipment-raw.schema.json`
- `schemas/game-data/1.0.0/equipment/equipment-runtime.schema.json`
- `schemas/game-data/1.0.0/equipment/raw-package.schema.json`
- `schemas/game-data/1.0.0/equipment/transform-map.json`

桌面 20260808 交接源码同样缺 `tools` 和 `schemas/game-data`；交接程序包也不包含这套源码辅助管线。

## 可追溯来源

上述六个文件在以下两套独立历史材料中各存在一份，逐文件 SHA-256 完全相同：

- `D:/Codex-2`
- `D:/Codex-2/CurrencyWarsSmartRaccoon-CodexHandoff-20260801`

哈希：

- `Invoke-EquipmentDataPipeline.ps1`：`AF0B6BB9969CD81AA8ED763105CDB112B73A49E476C220F4E045810CF834AC93`
- `equipment-icon-manifest.schema.json`：`0448E0F7029C39543CDBB20ED256DAB1C72C5B5D7945E055329CEFF07A5855D6`
- `equipment-raw.schema.json`：`46107B8A2CF5938DE072C872F1A4A2A033BBE744F17B0B09EB04D92D54ADED6D`
- `equipment-runtime.schema.json`：`0D5402680637DF97B20947E38F980D5D16C82335FBC7A82F70EAB8D1BF61F63E`
- `raw-package.schema.json`：`C80BE5DC1C6F75B15C8A3A0C0E8CEAED2AFD5AD6D9D994A6C69A9E93C443CCA8`
- `transform-map.json`：`410D5F16E7DF43D5EF25B4BC6B3E6034D7EFB4FB97C73639ACC9DABE1826D87E`

## 数据版本兼容性

- 当前工作副本与 20260801 历史材料的 `data/raw/4.4/equipment` 均为 160 个文件，逐路径 SHA-256 差异 0；
- 当前工作副本与历史材料的 `data/runtime/1.0.0/4.4/equipment` 均为 159 个文件，逐路径 SHA-256 差异 0；
- 当前 `records.json`、`icon-manifest.json`、runtime `equipment.json` 等关键文件与历史快照逐哈希一致；
- 脚本读取的 schema 版本与当前 raw package 声明均为 `1.0.0`。

因此来源不是“拿旧工具猜测套用新数据”，而是恢复与当前同一数据快照配套、被交接源码遗漏的管线资产。

## 修前测试

已在六个文件仍不存在时单独运行全部 `EquipmentDataPipelineTests`：

- 结果：0/5，退出码 1；
- 原始 TRX：`test-results/ISSUE-011-before-focused.trx`；
- SHA-256：`BCC9D3637ABCC989CC085F9088D265158654D391BED794B3B8BDADAA8BFC55B6`；
- 成功路径直接报告 `tools/Invoke-EquipmentDataPipeline.ps1` 不存在；
- 四个拒绝路径因为脚本未启动，无法生成预期的 `conversion-report.json`，均在拒绝报告断言处失败。

复现后再次逐路径检查，六个目标文件均仍不存在。

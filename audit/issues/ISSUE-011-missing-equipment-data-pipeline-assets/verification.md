# ISSUE-011 验证记录

当前状态：已完成并获独立只读审查员批准。

## 修前基线

- ISSUE-010 全量：807 项，799 通过、7 失败、1 跳过；
- 其中 5 个失败均属于 `EquipmentDataPipelineTests`；
- 工作副本和桌面 20260808 交接源码均缺本项六个文件；
- 两套 20260801/历史原项目来源逐文件哈希一致；
- raw 160 文件和 runtime 159 文件与历史配套快照逐哈希差异 0。

## 修前失败

- 命令：`dotnet test tests/CurrencyWarsAssistant.Tests/CurrencyWarsAssistant.Tests.csproj -c Release --no-restore --no-build --filter FullyQualifiedName~EquipmentDataPipelineTests`
- 结果：0/5，退出码 1；
- TRX：`test-results/ISSUE-011-before-focused.trx`；
- SHA-256：`BCC9D3637ABCC989CC085F9088D265158654D391BED794B3B8BDADAA8BFC55B6`；
- 成功路径错误明确为脚本路径不存在；四个拒绝路径未得到规范 conversion report；
- 运行后六个目标资产仍全部不存在。

## 恢复文件与来源哈希

六个文件均通过 `apply_patch` 恢复；最终字节数和 SHA-256 与 `D:/Codex-2` 及 `D:/Codex-2/CurrencyWarsSmartRaccoon-CodexHandoff-20260801` 两份来源完全相同：

- `tools/Invoke-EquipmentDataPipeline.ps1`：25432 字节，`AF0B6BB9969CD81AA8ED763105CDB112B73A49E476C220F4E045810CF834AC93`
- `schemas/game-data/1.0.0/equipment/equipment-icon-manifest.schema.json`：1405 字节，`0448E0F7029C39543CDBB20ED256DAB1C72C5B5D7945E055329CEFF07A5855D6`
- `equipment-raw.schema.json`：4847 字节，`46107B8A2CF5938DE072C872F1A4A2A033BBE744F17B0B09EB04D92D54ADED6D`
- `equipment-runtime.schema.json`：3493 字节，`0D5402680637DF97B20947E38F980D5D16C82335FBC7A82F70EAB8D1BF61F63E`
- `raw-package.schema.json`：1518 字节，`C80BE5DC1C6F75B15C8A3A0C0E8CEAED2AFD5AD6D9D994A6C69A9E93C443CCA8`
- `transform-map.json`：2077 字节，`410D5F16E7DF43D5EF25B4BC6B3E6034D7EFB4FB97C73639ACC9DABE1826D87E`

20260801 旧交接的 `FILE_SHA256_MANIFEST.csv` 也登记了这六项。没有编辑脚本/schema 内容，没有增加 `Stage-EquipmentRaw.ps1`，也没有修改 C#、测试、raw、runtime、docs 或 csproj。

## 修后自动化验证

1. 针对性 `EquipmentDataPipelineTests`：
   - `test-results/ISSUE-011-after-focused.trx`
   - SHA-256：`6C3480306989D44B250D927CDE0586155B04F1224FCB59EADF96B9490F8BC4DB`
   - 5/5 通过；成功路径两次生成字节稳定 runtime/report、157 条记录并保留未知字段；四个错误输入均拒绝且不写 runtime。
2. 关联 `EquipmentDataPipelineTests|GameDataCatalogTests`：
   - `test-results/ISSUE-011-after-related.trx`
   - SHA-256：`5C46D6C7114F5854F319D68391D6238FDBE83E43B2F795AA664826F396C297E0`
   - 8/8 通过。
3. 全量：
   - `test-results/ISSUE-011-after-full.trx`
   - SHA-256：`11105456A9E3E5DF2E6AE87144C6A69F7452A31AE176A8142260EA188195D0AF`
   - 807 项：804 通过、2 失败、1 跳过；退出码 1。
   - 相对 ISSUE-010 全量 `7A6FC352C2A7589014999D9697FDB25AC2BD7DF30AEE092788E75ED870F23799` 逐测试名比较：恰好五个 `EquipmentDataPipelineTests` 从 Failed 变为 Passed；新增、缺失或其他结果变化均为 0。
   - 剩余两项为既有 `HistoricalUiFieldCoverageTests` 与非生产同构 `RecognitionPerformanceTests`；硬编码跳过性能项仍未验证。

## 独立显式管线运行

使用显式审计输出目录运行恢复后的脚本，未使用会覆盖检入数据的默认路径：

- 状态 `accepted`，输入/输出均为 157 条；
- 生成 `equipment.json` SHA-256：`4B525D125B38B86EF68852C3D06BB23A28A7190A3389D9821AB814F7CBCB3554`；
- 生成 `manifest.json` SHA-256：`643023CD40A00DAD5B8828A38B83F839C197CC62DD095210A643E81F18868299`；
- `outputs/generated-runtime` 与检入的 `data/runtime/1.0.0/4.4/equipment` 均为 159 文件/5343432 字节，逐路径 SHA-256 差异 0；
- `outputs/generated-report/conversion-report.json` SHA-256：`48A650645C0AACFF940BFA5E3F6744EA5480889B0F5E62ABF266F9E432DCED27`，状态 accepted、output_written=true、rejected_records 为空。
- 测试后工作副本 raw 仍为 160 文件/5086050 字节、runtime 仍为 159 文件/5343432 字节，并与历史快照逐路径差异 0。

## Release 构建与发布边界

- `test-results/ISSUE-011-release-build.binlog` SHA-256：`A3A9C5C98AEA7C83FE3B18EAC1E02D77E63E18323CB74B2ADA273179AA8B0C85`
- `test-results/ISSUE-011-release-build.log` SHA-256：`9ACC593A2BDBDA28BDCAFE5139A252B49D31ED7AA7836FBF5435F459F3CF4AEF`
- `dotnet build CurrencyWarsAssistant.sln -c Release --no-restore` 退出码 0，0 警告、0 错误。
- App csproj 只复制 `config/**`、`data/**` 与 `schemas/advisor/**`；最终 App 输出中没有 `tools` 或 `schemas/game-data`。本项恢复的是源码审计/转换资产，不改变可执行包内容。

## 未包含的独立缺口

- `tools/Stage-EquipmentRaw.ps1` 仍缺，完整 staging 上游尚未恢复；
- 工作副本根 `schemas/advisor` 仍缺，虽然 20260808 程序包含该资产；应另立问题核对；
- 157 项图标—名称—ID—网络来源专项尚未执行；
- 正式生产同构性能批测尚未执行。

## 独立审查结论

独立只读审查员最终结论：`APPROVE ISSUE-011`。

审查员独立核对了原件六路径缺失、修前 0/5 时间顺序、两套历史来源和 `FILE_SHA256_MANIFEST.csv`、六文件三方逐字节一致性、raw 160 与 runtime 159 全树、5/5 与 8/8、807 项全量逐测试名差分、显式生成 runtime、Release 构建和 App 输出排除范围。未发现阻断点。批准严格限于恢复转换脚本及五份 equipment schema/map；staging、advisor schema、157 项视觉/网络核对和正式软件包均未完成。

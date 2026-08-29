# 数据与攻略知识库导入边界

## 模块目标

把另一对话生成的角色、投资环境、投资策略、攻略流派等 JSON 当作上游原始数据，通过版本化 schema、字段/枚举/ID/引用校验和可重复转换，生成运行时可安全消费的规范化数据与转换报告。

## 不属于本模块的内容

不联网重新采集资料（由上游数据对话负责）、不修改自动化状态机、不定义 UI 交互、不直接覆盖运行时目录、不删除上游未知字段，也不以手工复制代替转换。

## 当前真实状态

运行时 `GameDataCatalog` 仍加载只读的 `data/4.4/`，现有数据约含 83 个投资环境、334 个投资策略、51 个词条、20 个竞品/阵营和 71 个角色。装备数据已经完成第一条独立纵向样板：上游JSON与图标manifest经内容寻址raw快照、JSON Schema、结构/枚举/ID/引用校验、显式转换后进入独立`data/runtime/1.0.0/4.4/equipment/`。该输出尚未接入`GameDataCatalog`，因此不会改变现有生产运行时。

## 已实机验证

数据导入本身没有独立游戏实机完成证据，也不应仅靠界面能显示名称就判定整套数据正确。当前状态：**尚未确认**。

## 已实现但未实机验证

- `GameDataCatalog` 和加载测试可读取当前 4.4 运行时 JSON。
- `tools/Import-GameData.ps1` 可从四份既定 Markdown 报告生成部分运行时数据。
- 当前构建包含 `data/4.4/`，自动测试能验证既定数量和基本枚举。
- `tools/Stage-EquipmentRaw.ps1`会在确认上游文件稳定后，把装备记录、图标manifest和157张图标脚本化写入内容寻址raw快照；相同输入重复执行复用同一目录。
- `tools/Invoke-EquipmentDataPipeline.ps1`完成装备raw package、schema_version/game_version、字段结构、枚举和值域、稳定ID唯一性、合成/特权基础装备引用、图标manifest引用及图标哈希校验。
- 装备转换显式把15种上游类型映射为14种runtime枚举，把合成名称和基础装备名称解析为稳定ID；未知字段原样保存在`source_extensions`并写入转换报告。
- 当前真实样本为157条装备、36个特权基础装备引用、54个合成引用和157个图标引用；重复执行后160个runtime/report文件的SHA-256全部不变。
- `EquipmentDataPipelineTests`共5项，覆盖确定性、未知字段保留、版本拒绝、枚举拒绝、重复ID拒绝和缺失引用拒绝；不兼容输入不会生成`equipment.json`。

## 部分实现或占位功能

- 现有导入脚本依赖固定 Markdown 格式和硬编码计数，不支持任意上游 JSON。
- 正式JSON Schema和转换器目前只覆盖装备一类；投资环境、投资策略、敌人词条、竞争对手、角色、羁绊和攻略方案仍待逐类迁移。
- 除装备样板外尚未完成raw与runtime目录隔离；`currency-wars-characters.json`仍保留大量运行时未消费的上游字段。
- 旧`data/4.4`尚未迁移；`GameDataCatalog`也尚未切换到新的runtime目录。当前装备样板故意保持独立，接口影响为零。

## 核心代码入口

- `src/CurrencyWarsAssistant.Game/GameDataCatalog.cs`
- `data/4.4/`
- `tools/Import-GameData.ps1`
- `tools/Stage-EquipmentRaw.ps1`
- `tools/Invoke-EquipmentDataPipeline.ps1`
- `schemas/game-data/1.0.0/equipment/`
- `data/raw/4.4/equipment/890ae486642e979b/`
- `data/runtime/1.0.0/4.4/equipment/`
- `reports/data-import/4.4/equipment/conversion-report.json`
- `tests/CurrencyWarsAssistant.Tests/EquipmentDataPipelineTests.cs`
- `tests/CurrencyWarsAssistant.Tests/GameDataCatalogTests.cs`
- `docs/DATA_IMPORT_4.4.md`（必须保留，不覆盖）
- `docs/handoffs/DATA_KNOWLEDGE_BASE.md`（既有详细审计补充）

## 输入与输出

输入：上游 raw JSON、来源、抓取时间、游戏版本、上游 schema/字段说明。输出：schema 校验报告、语义/引用校验报告、规范化运行时 JSON、未知字段保留记录、转换 manifest 和稳定 ID 映射表。

当前装备样板命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Stage-EquipmentRaw.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Invoke-EquipmentDataPipeline.ps1 -RawDirectory data/raw/4.4/equipment/890ae486642e979b
```

raw目录名由输入记录和manifest的SHA-256决定。上游内容变化后会生成新的快照目录，不覆盖旧快照；转换结果只写入独立runtime和reports目录。

## 依赖的其他模块

依赖上游“数据与攻略知识库”对话提供 raw；依赖 Game 定义运行时最小模型和枚举；依赖 Testing/Release 验证确定性、引用完整性和构建复制；总控协调同文件写入。

## 不允许破坏的既有规则

- 上游 JSON 永远先视为 raw，不得直接塞入运行时。
- 不得擅自删除上游字段；运行时不用的字段仍保留在 raw 层并写入报告。
- 所有转换必须可重复执行，禁止手工复制和隐式修正。
- 投资环境只支持正选；阵营/负面词条支持 Require/Reject；特殊组合边界必须保留。
- 上游对话正在写文件时，本模块不得并发修改同一文件。

## 已知问题

- 现有`GameDataCatalog`仍以名称/计数为主，尚未消费装备样板的稳定引用输出。
- 数据版本固定 4.4，没有对游戏更新的版本门禁或迁移策略。
- 旧数据仍存在raw与runtime语义混放风险；只有装备样板已经完成物理与契约隔离。
- 既有脚本只处理四份 Markdown，不满足当前 JSON 上游协作模式。

## 下一步任务

1. 沿用已冻结的`data/raw`、`schemas`、`data/runtime`、`reports`边界，下一类优先选择现有运行时最小模型之一，不一次迁移全部数据。
2. 为投资环境、投资策略、敌人词条、竞争对手、角色与羁绊逐类定义schema和引用图；投资环境只保留正选语义，阵营/词条保留Require/Reject，特殊组合不得扁平化，多方案不得混合。
3. 在所有旧运行时数据完成等价转换和回归前，不修改`GameDataCatalog`的生产目录；确需切换时先提交接口影响说明。
4. 上游每次更新先重新做写入冲突检查，再生成新的内容寻址raw快照；不得修改上游同名文件。

## 验收标准

- 同一 raw 输入重复转换得到字节级或语义级稳定输出。
- 所有 ID 唯一，枚举合法，跨文件引用完整，版本一致。
- 未使用字段仍可从 raw 和报告追溯；没有静默丢弃。
- 不兼容记录会明确失败/隔离，不会进入生产运行时。
- 数据加载测试、转换快照测试和构建复制检查通过；游戏使用正确名称仍需另做 L4 验证。

## 测试与构建命令

```powershell
.\.tools\dotnet\dotnet.exe test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj -c Debug --no-restore --nologo --filter "GameDataCatalogTests"
.\.tools\dotnet\dotnet.exe test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj -c Debug --no-restore --nologo --filter "EquipmentDataPipelineTests"
.\.tools\dotnet\dotnet.exe build CurrencyWarsAssistant.sln -c Release --no-restore --nologo
```

`tools/Import-GameData.ps1` 会写入数据文件，只能在确认输入、输出和上游写入状态后执行；本次审计未运行该脚本。

## 最近可用构建

**尚未确认。** `artifacts/live-test-30` 打包了当前 4.4 运行时数据，但这不能证明上游 JSON 已兼容，也不能证明游戏中所有引用正确。

## 最后更新时间

2026-07-26（Asia/Shanghai）

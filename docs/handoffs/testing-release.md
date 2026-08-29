# 测试、证据与发布

## 模块目标

建立可重复的构建、测试、离线视觉和游戏实机四级证据，维护候选产物 manifest、版本/源指纹、日志/截图绑定和发布门禁，防止目录名或单元测试被误当成实机完成。

## 不属于本模块的内容

不修改业务规则来让测试变绿、不自行启动游戏或桌面、不决定视觉/任务算法、不恢复或重建 Git 历史而不先确认用户现有历史，也不把旧产物证据迁移给新产物。

## 当前真实状态

2026-07-26 使用仓库内 .NET SDK 执行完整 Debug 测试：7 个项目编译成功，149 个测试通过、0 失败、0 跳过；Release 构建 0 警告、0 错误。`artifacts/live-test-35` 由当前 Release 直接发布，核心 DLL 与 Release 输出 SHA-256 一致，并包含候选构建 manifest。仓库仍没有有效 Git 元数据、正式版本号或 CI 报告。

## 已实机验证

较早构建有正向导航和恢复证据；2026-07-26 用户说明使用 `live-test-34` 的日志已推进两次秒杀结算，但暴露投资策略后置页超时和商店第 4 槽重复未知提前稳定。日志没有记录候选包指纹，且 `live-test-35` 尚未实测。当前候选构建的 L4：**尚未确认**。

## 已实现但未实机验证

- 149 项单元/组件/离线回放测试当前通过。
- 页面 fixture 有 25 个正向回放样本；角色卡和部分 OCR 使用真实截图 fixture。
- `live-test-35` 与当前 Release 核心 DLL 哈希一致，可作为候选编译产物；名称不代表实机可用。

## 部分实现或占位功能

- 没有 `.trx`/JUnit 等持久测试报告、覆盖率、CI 或测试环境 manifest。
- `live-test-35` 已记录构建命令、测试结果、数据版本、包文件数/字节数和核心二进制 hash；由于无有效 Git，仍缺少 commit/source hash，配置/全量数据 hash 也尚未完整覆盖。
- `.git` 目录为空，不能计算可靠源提交，也无法审查跨对话差异。
- OCR 测试可能因系统 OCR 不可用提前返回但仍计为通过。
- 没有每次识别/动作的截图包、实机用例清单和发布签字记录。

## 核心代码入口

- `CurrencyWarsAssistant.sln`
- `tests/CurrencyWarsAssistant.Tests/`
- `tests/CurrencyWarsAssistant.Tests/Fixtures/PageReplay/`
- `artifacts/live-test-35/`
- `logs/`
- `Directory.Build.props`
- 各项目 `.csproj`
- `docs/PROJECT_STATUS.md`

## 输入与输出

输入：源代码、配置、模板、数据、测试环境、候选产物、日志/截图和实机用例结果。输出：L1/L2/L3/L4 报告、artifact manifest、失败清单、已知问题、发布/不发布结论。

## 依赖的其他模块

依赖所有模块提供测试和验收条件；依赖总控确定证据等级和发布资格；依赖 Vision 提供 fixture/准确率，依赖 Tasks 提供状态事件，依赖数据模块提供 schema/转换报告。

## 不允许破坏的既有规则

- 编译通过、测试通过、离线视觉验证、游戏实机跑通必须分别记录。
- Fake 测试是 L2，真实截图回放是 L3；只有绑定当前产物的游戏闭环是 L4。
- `live-test-N` 名称不构成发布证明。
- 每次 Bug 修复通过完整测试后必须递增生成新的 `live-test-N` 候选包并写入 manifest；不得覆盖旧候选，也不得让用户误测旧包。
- 未获明确授权不得启动/操纵游戏、BGI、其他应用或 Windows 桌面。
- 实机失败必须保留日志和证据，不通过改名或覆盖 artifact 隐藏。

## 已知问题

- `live-test-35` 尚无启动/实机日志；现有 2026-07-26 实测日志未记录候选包指纹，产物与实机结果仍未闭环。
- 无有效 Git，无法证明构建输入或安全回滚。
- fixture 覆盖集中于单分辨率和正样本，没有量化误报率。
- 项目没有正式许可证、版本标签、变更记录和发布流程。

## 下一步任务

1. 建立只读构建 manifest 生成与校验：源/配置/模板/数据 hash、SDK、命令、测试结果。
2. 把测试结果持久化，并让条件性 OCR 测试显式 Skip/Fail。
3. 扩充负样本和分辨率矩阵，生成离线准确率报告。
4. 用户明确授权后按“启动/捕获 → 正向开局 → 恢复闭环 → 分段奖励关”建立当前产物实机证据。
5. 在处理 Git 前先确认是否有需要恢复的外部历史，禁止直接覆盖。

## 验收标准

- 任一发布产物能唯一追溯到源码、SDK、依赖、配置、模板和数据 hash。
- 自动测试结果可机器读取并保留；环境缺失不被伪装成通过。
- 每项 L4 有产物 ID、游戏/数据版本、分辨率、用例、日志、截图和最终结果。
- 已知问题和失败用例写入发布说明；不满足门禁时明确标为候选而非可用版本。

## 测试与构建命令

```powershell
.\.tools\dotnet\dotnet.exe test CurrencyWarsAssistant.sln -c Debug --no-restore --nologo
.\.tools\dotnet\dotnet.exe build CurrencyWarsAssistant.sln -c Release --no-restore --nologo
```

如需生成新的 artifact 或执行实机用例，必须先定义输出目录/manifest；实机步骤必须得到用户明确授权。

## 最近可用构建

**尚未确认。** `artifacts/live-test-35` 是最新候选，核心 DLL 与当前 Release 一致，149/149 测试通过并带构建 manifest；没有候选自身的启动、捕获和业务闭环证据。`live-test-30` 至 `live-test-34` 均保留且未覆盖。

## 2026-07-27 live-test-65

- 最新候选为 `artifacts/live-test-65`，包含奖励关两个安全门禁修复。
- 最小验证：定向 29/29；Release publish 0 警告、0 错误。
- 按用户全局省 Token 决定不重复全量测试；此前源码拆分基线为 165/165。
- 当前仍是 L2/Beta，实机状态尚未确认。

## 最后更新时间

2026-07-26（Asia/Shanghai）

## 2026-07-27 live-test-66

- 最新候选：`artifacts/live-test-66`。
- 最小验证：奖励关定向测试 29/29；Release publish 成功。
- 未重复全量测试；尚无 live-test-66 游戏闭环证据，当前为 L2/Beta 候选。

## 2026-07-27 live-test-68

- 已授权挑战失败结算链取消点击次数上限：持续点击同一“下一步”位置并监测页面，稳定识别货币战争主页或用户从软件取消时结束。
- 临时 Unknown 不再提前退出。单项回归 1/1；Release publish 成功；未全量测试、未实机验证。

## 2026-07-27 live-test-69

- 联合定向回归：89 通过、0 失败、0 跳过。
- Release publish 成功，输出 `artifacts/live-test-69`；旧候选未覆盖。
- 回放证据包括真实椒丘商店截图和日志遮挡货币战争主页截图。
- 遵循省 Token 策略未重复全量测试；新包仍是 L2/L3 候选，不能记为完整整局 L4 实机通过。

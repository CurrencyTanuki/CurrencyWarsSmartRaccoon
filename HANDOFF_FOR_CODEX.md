# 货币战争智能狸 — Codex 续作交接文档

> 生成时间：2026-08-01 · 由 Reasonix（deepseek-v4-flash）完成 0.2.756→0.2.777 全部改动后整理
> 本文件夹是完整项目快照（源码 + 测试 + 数据 + 文档），**Codex 可直接在此之上继续开发/修 bug，无需改动已完成的代码**。
> 当前版本：**0.2.777**（全量测试 570/570 通过）

---

## 一、项目现状（一句话）

.NET 8 WPF 的《崩坏：星穹铁道》"货币战争"玩法辅助工具：**自动刷开局**（识别敌人阵营/负面词条/投资环境，循环刷到满意）+ **对局实时记录**（节点伤害/金币/奖励/血量Δ/阵容/装备/背包）+ **历史数据**（断点续玩、已完成对局回看）。

- 入口：`src/CurrencyWarsAssistant.App/`（WPF）
- 测试：`tests/CurrencyWarsAssistant.Tests/`（全量 570 项，`dotnet test -c Release`）
- 构建：`dotnet build CurrencyWarsAssistant.sln -c Release`（需 .NET 8 SDK；`Directory.Build.props` 中 `TreatWarningsAsErrors=true`）
- 数据：`data/4.4/`（页面模板、投资环境/词条/角色数据、OCR 模型）；`config/`（导航流程 navigation-flow.json、页面识别 page-recognition.json、布局 screen-layouts.json、刷开局规则 opening-rules.json）
- 运行产物：发布到 `artifacts/CurrencyWarsSmartRaccoon-<版本>-win-x64-portable/`（self-contained，双击 exe 即用）

## 二、⭐ 最重要的三条工程常识（必读，否则会重复踩坑）

1. **管理员权限（UIPI）**：游戏（星穹铁道）通常以**管理员（高完整性）**运行。软件必须申请管理员权限（`src/CurrencyWarsAssistant.App/app.manifest` 的 `requireAdministrator`），否则 Windows UIPI 会**静默拦截软件向游戏注入的鼠标/键盘**（SendInput/SetCursorPos 全部无效、光标不动）。**这是 0.2.771–0.2.775 刷开局"光标不动"的最终根因**（与 MoveMouse 用什么 API 无关）。**输入注入类问题先查完整性级别**（OpenProcess 查询被拒=对方高完整性）。
2. **OCR 并发安全**：PaddleOCR ONNX `InferenceSession` 非线程安全，同一 session 并发 Run 会导致 coreclr c0000005 崩溃（0.2.749–0.2.758 历史崩溃根因）。现方案：**每个 lane 独立 session 的池**（`PpOcrOfflineOcr`，App.xaml.cs 注册 `maximumConcurrency: 6`），同一 session 绝不并发。**不要恢复"多线程共享单 session"**。
3. **识别/关键帧逻辑改完必须用真实截图回放验证**（`tests/.../Fixtures/phase2-live-2026-07-29/` 及用户提供截图），只跑模拟帧单测视为未验证（0.2.758–0.2.764 关键帧队列反复炸的教训）。

## 三、0.2.756 → 0.2.777 改动清单（按版本）

| 版本 | 改动 |
|---|---|
| **0.2.756** | 首次候选版：23 项审计 + 修复 9 处（声明窗口非模态 DialogResult 回归、伤害千分位逗号当小数点（错 1000 倍）、热键注册失败静默、断点 runId 路径穿越、初始化异常兜底、滑块边界、Timer 空转、除零保护） |
| **0.2.757** | 撤销"词条单列 + 手动加载"（恢复 WrapPanel 多列、词条区启动即显示）——用户实机反馈 |
| **0.2.758** | 悬浮窗默认位置（日志左上 Top+14 / 历史右上 Top+20）；补给页（reward_shop）血量/金币不采集回填；结算奖励缺失时**备战金币差分回填奖励列**；非战斗页 SceneTransition 强制关键帧 |
| **0.2.759** | 修复 0.2.758 回归：主界面（Main）切换不再刷爆关键帧队列（SceneTransition 收窄 + 回归测试） |
| **0.2.760** | **开局信息进对局记录**（敌人阵营/负面词条/投资环境 → checkpoint/completed-run）；**历史对局窗口**（CompletedRunsWindow，读磁盘 completed-run 显示开局信息+每节点阵容/装备/背包）；历史窗口列表金色细线选中样式 |
| **0.2.761** | 主界面金色调（标题/主按钮/分隔线/卡片边框） |
| **0.2.762** | OCR 并发修复：多 session 池（防 c0000005） |
| **0.2.763** | 未知节点战斗帧不确认"战斗开始"（防"竞争对手生成中"误判）；Unknown 过场关键帧；**重开分段**（检测到 1-1 备战即判重开，旧局 Abandoned + 新 runId 分离） |
| **0.2.764** | GPU（DirectML）推理尝试 + 队列扩容 + Unknown 触发 3 秒限流 |
| **0.2.765** | **禁用 DML**（AMD 25.3.1 驱动挂起，dump 实锤）；对局结束页**连续 2 帧确认 + 页面置信度 ≥0.5**（防误判提前截断对局） |
| **0.2.766** | **备战血量区域修正**（像素级定位：血量在顶部进度条右端 x≈0.746–0.766/y≈0.061–0.086；1920 基准 `HealthOcrRegion=(1420,30,60,70)`、`HealthTightOcrRegion=(1430,64,44,34)`）——修复备战停留很久血量Δ仍缺失 |
| **0.2.767** | **续玩历史预加载**（LocalRunStore.LoadAnalysesAsync 灌投影，续玩后节点历史完整）；详细历史窗口 XAML 重复 ListBoxItem key 修复（FLT 错误）；节点历史表格**自动滚动到底** |
| **0.2.768** | **位面切换误判终局**：1-9 通关结算页（"挑战结束"标题+结算内容）被误判生命耗尽 → 对局截断 → 2-1 无记录。修复：`IsHealthDepletedRunPage` 识别到结算内容（金币/伤害）就不是终局 |
| **0.2.769** | 结算页 4 图分类断言（挑战失败/生命耗尽/挑战成功动画中/挑战成功最终，用户提供截图 Fixtures） |
| **0.2.770** | 开局词条列表滚动条隐藏（VerticalScrollBarVisibility=Hidden） |
| **0.2.771–0.2.775** | MoveMouse 实现折腾（SetCursorPos→SendInput→混合）——**结论：与实现无关**，权限问题 |
| **0.2.776** | ⭐ **UIPI 权限修复**：`app.manifest` `asInvoker`→`requireAdministrator`（与旧版/BGI 一致）——**刷开局恢复**；修正错误契约测试（原断言"不请求管理员"） |
| **0.2.777** | **断点列表过滤空 run**（无观测无节点的刷开局空 run 不再列出，用户不再选错导致历史为空）；新增回归测试 |

## 四、新增功能（相对 0.2.751 基线）

1. **开局信息完整记录**：自动刷开局达标后，敌人阵营（3 个）/负面词条（4 个）/投资环境写入对局记录（checkpoint/completed-run），历史可查
2. **历史对局窗口**：节点历史悬浮窗 → 详细历史 → "历史对局"按钮 → 已完成对局的完整后台数据（开局信息、每节点阵容/角色装备/装备栏/背包/投资环境策略）
3. **重开分段**：玩家重开（节点回到 1-1 备战）→ 上一局 Abandoned 封存 + 新 runId 分离，新旧数据不混
4. **备战金币差分回填奖励**：结算页被快速跳过时，用下一节点备战金币差分补上奖励列
5. **续玩历史恢复**：断点续玩时自动加载磁盘历史分析，节点历史完整显示
6. **表格自动滚动**：新节点同步时自动滚到底
7. **主界面金色调 UI**（货币战争风格）

## 五、技术报告（关键实现与教训）

### 5.1 输入注入（最重要）
- 软件/游戏权限必须同级（都是管理员）。`requireAdministrator` 后双击弹 UAC。
- `Win32InputBackend.MoveMouse`：SendInput 绝对坐标（虚拟桌面归一化）优先 + SetCursorPos 降级。
- 点击前有光标到位验证（`VerifyPointerArrivalBeforeClick`，不到位阻止点击）。

### 5.2 OCR 并发（崩溃史）
- `PpOcrOfflineOcr`：`_sessions`（Lazy<ModelSession[]>，每 lane 一个独立 InferenceSession）+ `ConcurrentQueue` 池 + `SemaphoreSlim(6,6)`。同一 session 绝不并发 Run。Dispose 时排空 lanes 再释放。
- 相关选项：`IntraOpNumThreads=1`、`EnableCpuMemArena=false`。
- GPU（DirectML）代码保留但默认禁用（AMD 25.3.1 驱动实测挂起——线程卡原生层、UI 空闲、CPU 零负载；dump 诊断法）。

### 5.3 页面分类与对局结束判定
- 4 种结算页（用户提供截图实测分类）：挑战失败（灰页+C 评价）→ `challenge_failed`；生命耗尽（红黑+破碎心形+-N）→ `challenge_health_depleted`；挑战成功动画中/最终（金色+完整心形）→ `challenge_success`。
- **对局结束判定**：pageId 匹配 + 置信度 ≥0.5 + **连续 2 帧** + **结算内容排除**（识别到金币/伤害就不是终局）——"挑战结束"标题同时出现在通关结算和终局页，标题模板不能单独决定。
- 关键帧：战斗页动画不触发、主页（Main）不触发、Unknown 过场 3 秒限流触发、备战/结算切换触发（SceneTransition + fastPageChanged）。

### 5.4 识别区域（像素级定位记录）
- 备战血量：顶部进度条右端（2560×1440 实测 x≈1910–1960, y≈88–124；1920 基准区域见上）。
- 结算金币总览、敌人概览、投资环境 4 槽（首槽环境+后 3 策略）等区域见 `Phase2RecognitionRegions.cs` 与 `config/screen-layouts.1920x1080.json`。

### 5.5 断点/历史
- `LocalRunStore.ListIncompleteRunsAsync`：**过滤无观测无节点的空断点**（刷开局空 run 不列出）。
- `LoadAnalysesAsync`：续玩时加载磁盘 analysis 灌投影（`Phase2LiveCollectionService` 的 `isResume` 分支）。
- completed-run.v1.json 含 `IdentityEvidence`（开局信息）。

### 5.6 测试体系
- 全量 `dotnet test -c Release` = **570/570**。
- 真实帧回放：`Fixtures/phase2-live-2026-07-29/`（battle-1-6-late.png、preparation-1-4-user-2026-08-01.png、settlement-failed-c.png 等 8 张用户实测截图）。
- 契约测试：UiRedesignContractTests（XAML 断言）、RunCompletionArchiveTests（归档/断点）、Phase2BattleOutcomeHealthTests（血量/完美/重开）、Phase2OperationalCollectionTests（真实帧识别）。

## 六、已知待办 / 已知问题（Codex 续作方向）

1. **受限备战状态识别**（用户 2026-08-01 反馈）：游戏崩溃/战斗中手动退出 → 下次进入回到"战斗开始前、只能换角色站位"的受限备战状态——软件断点恢复时当前节点为 Unknown，需识别该状态并确定中断节点（`docs/PENDING_USER_FIXES.md` 有记录）。
2. **"关键页面识别队列已满"**：偶发瞬时（0.2.764 后大幅缓解，未彻底消除）——识别吞吐与关键帧产生速率的平衡仍可优化（当前 6 路 CPU OCR）。
3. 手动录制（DirectRecording）路径的开局信息：无开局数据时标记"未记录"（已知局限）。
4. 待用户实机确认项：0.2.777 断点历史恢复、0.2.776 刷开局 + 管理员权限（UAC）。

## 七、给 Codex 的开发约束（重要）

1. **不要改已完成并验证的代码**（0.2.756–0.2.777 全部有测试背书）——在其上增量开发/修复。
2. **单变量修改**：一轮只改一个行为变量；性能类改动记录前后基线。
3. **真实帧回放**：改识别/关键帧/页面逻辑必须用 `Fixtures/` 真实截图回放验证。
4. **权限意识**：涉及输入注入必须管理员；涉及游戏交互先确认完整性级别。
5. **累积制**：用户反馈先记录 `docs/PENDING_USER_FIXES.md`，攒批后统一修复出 1 个新候选版本；阻断级（崩溃/无法使用）可立即修。
6. 交付物 = publish 目录（不打 zip），给出 exe 完整路径（用户约定）。
7. **回退基线**：`release-baseline/CurrencyWarsSmartRaccoon-0.2.751-win-x64-portable.zip`（用户认可的稳定版）；`Desktop/CurrencyWarsAssistant-phase1-livefix-20260728-build04-win-x64`（第一阶段稳定包，SendInput 输入可参考）。
8. 构建/测试：`dotnet build CurrencyWarsAssistant.sln -c Release`、`dotnet test -c Release`（TreatWarningsAsErrors，警告即错误）。

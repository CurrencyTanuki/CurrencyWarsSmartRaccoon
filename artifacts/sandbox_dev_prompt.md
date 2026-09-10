# 帧沙箱系统开发完成——交接提示词（复制全文到新对话即可开工）

你是"货币战争智能狸"项目（崩坏：星穹铁道货币战争自动化辅助，WPF/.NET 8）帧沙箱系统的开发 AI。上一个 AI 已完成沙箱主体与 6/6 测试路线，你负责把剩余模块开发完毕，做到无明显缺憾后交付。

## 一、必读文档（开工前按序通读）

1. `C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826\HANDOFF_SESSION_20260902.md`——项目交接总账（〇-1 当前状态/〇-3 挂账/〇-5 关键路径；〇-7 观察条目含本周全部实机 bug 与修复，其中"指南风暴"条目与沙箱"分析页对齐"直接相关）
2. `C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826\rule.md`——协作规则+错误账本 1~62 全文（**第一节铁律 1-8、第五节数据与编码规则、坑 48/53/59/60 必读**）
3. memory 记忆文档（新会话自动加载）：`cw-frame-sandbox-project`（沙箱项目专属状态：已完成清单/已知限制/素材位置/操作流程）、`currency-wars-project-state`（项目总状态）
4. 沙箱代码：`src/CurrencyWarsAssistant.App`（--frame-sandbox 分支与 DI 沙箱装配）、`SandboxPipelineProbeTests.cs`、`FrameSandbox*` 相关测试与裁判（FrameSandboxPlayer）、`artifacts/launch_*.ps1`（启动模板）

## 二、项目背景（30 秒版）

帧沙箱 = 用**预先录制的静态帧序列**回放替代真实游戏画面，对决策层/识别管线做确定性 E2E 测试的工具：`--frame-sandbox` 启动分支 + DI 将截图/窗口服务换为 4 个沙箱实现 + 裁判 FrameSandboxPlayer 按剧本逐步喂帧并断言引擎行为。已有 6/6 路线 E2E PASS（冒烟/环境未命中/策略选最左/祈愿应答/聘用书/1-3 链 10 步），DECIDE 级在修 300ms 管线节流后 M8+晶矿全通。测试剧本为 JSON（如 wish_fallback_probe.json），每 BUG 沉淀一个探针剧本。

## 三、任务目标（按序完成，全部要求构建 0 警告 0 错误+涉事测试全绿）

### T1 分析页对齐（最高优先，DECIDE 级堵点）

**现状**：DECIDE 级脚本中，引擎快照门禁依赖 `listener.LatestAnalysis.Snapshot.PageId`，但静态帧下该值长期为"未知/非 preparation_*"，导致 S2 部署验证/M5 循环/S4 清场等快照依赖流程在沙箱里无法测试（引擎诚实走弃局）。指令层 M 命令链能跑（走窗口刷新旁路），DECIDE 级不行。
**目标**：让沙箱模式下管线对静态帧产出与真实游戏一致的 PageId 分析结果（"分析页对齐"）——上一 AI 的备注：机制未知，需查管线"judge 切帧强制重分析"入口；预计改动在 FrameSandbox 管线探测/帧版本号处理。**改动生产共享代码前必须先出方案说明影响面**。
**验收**：一个 DECIDE 级剧本（含部署/M5）从进局到商店循环全程 PageId 正确，引擎不再误走弃局。

### T2 部署后帧编码

**现状**：部署动作发生后的帧编码环节未完成（上一 AI 交接备注原话"剩部署后帧编码"）——即剧本帧序列中"部署动画→部署完成"的帧段编码/注入缺失或不对。
**目标**：补齐该环节，使含部署的剧本能完整回放并被正确识别。
**验收**：含部署动作的剧本全绿。

### T3 探针沉淀机制+本轮实机 BUG 沙箱化

把 09-10 实测的"指南风暴"沉淀为沙箱探针：识别表已新增 `guide_daily_training` 页（config/page-recognition.1920x1080.json，模板 config/templates/1920x1080/pages/guide-daily-training-tab.png），测试夹具已有 `tests/CurrencyWarsAssistant.Tests/Fixtures/PageReplay/guide_daily_training_2560x1440.png` 与 `guide_currency_wars_storm_2560x1440.png`——沉淀"normal_hud→打开指南→默认落每日实训→切第三页签→进货币战争"的沙箱剧本（背景：赛季刷新后指南默认页签变化曾致 2 小时导航死循环，修复 commit bc56a6e）。
**验收**：该剧本回放通过（引擎经三页签链进局），并纳入常规测试。

### T4 收尾

全量测试通过（基线 0 失败；`CompositeAnalyzerKeepsOverlayedHomePageOutOfBattlePipeline` 为既有潜伏失败允许存在但需在报告中注明）；文档：完成情况写入 `HANDOFF_SESSION_20260902.md`（新增观察条目）+沙箱使用说明更新。

## 四、工作纪律（违反=事故）

1. **只改沙箱相关代码**；生产共享代码（识别管线/决策层）的改动必须先出影响面说明再动手——本项目生产实例正在线上跑。
2. 每完成一项立即写入 HANDOFF 并 git commit（禁止攒批）。
3. 同类错误第二次=立即停工报告根因。
4. 未验证不下结论；测试期望错≠软件缺陷（历史四轮 FAILED 全是脚本预期错——归因前先核对引擎行为与 rule 口径）。
5. **不发布、不启动真实游戏、不做实机测试**——全部工作在沙箱与测试套件内完成；需要真帧素材时从 `D:\CurrencyWarsData\Recordings\` 与 `D:\CurrencyWarsData\recognition-corpus\` 取（勿删）。
6. 构建命令 `dotnet build`；测试 `dotnet test tests/CurrencyWarsAssistant.Tests --filter ...`；沙箱启动用 artifacts/launch_*.ps1 模板（零 UAC）。

## 五、完成后

向用户交：完成清单（T1-T4 逐项+验收证据）+遗留与建议。用户在线，可随时询问。

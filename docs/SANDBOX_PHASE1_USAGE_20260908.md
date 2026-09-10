# 帧沙箱 Phase 1 骨架——使用说明（2026-09-08 值守班）

> 设计书：docs/SANDBOX_FEASIBILITY_20260908.md（动机/边界/分期）。
> 本文档只写 Phase 1 落地后的运行方法与注意点。

## 一、四件套（DI 层换装，决策/操作/识别零改动）

| 接口 | 沙箱实现 | 位置 |
|---|---|---|
| IGameCapture | FileSequenceGameCapture（按裁判当前步回 PNG 解码帧，像素逐次克隆） | Vision |
| IGameWindowService | StubWindowService（假窗口 1920×1080，前台恒真） | Vision |
| IInputController | RecordingInputController（全部操作记录交裁判，恒回 Success） | Automation |
| IGameForegroundGuard | AlwaysForegroundGuard（立即放行） | Automation |

裁判=驱动器：App/FrameSandbox/FrameSandboxPlayer（脚本加载 FrameSandboxScriptLoader、
参数解析 FrameSandboxLaunchOptions）。

## 二、启动

```
CurrencyWarsAssistant.App.exe --frame-sandbox <脚本.json> [--frame-sandbox-out <目录>]
```

- **零 UAC 启动（2026-09-09 起，用户常设令：禁止弹 UAC）**：改
  `C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\CodexHandoff-20260802\CurrencyWarsSmartRaccoon-CodexHandoff-20260801\artifacts\CurrencyWarsSmartRaccoon-0.2.780-win-x64-portable\launch-target.txt`
  （第 1 行=目标 exe 绝对路径；第 2 行=参数行，**路径不加引号**）→
  `schtasks /run /tn CWTLaunchApp`（PowerShell 包裹）。参考实现=
  `artifacts\launch_hire_letter_e2e.ps1`（含 run 目录存活检查：启动后 60s 内必须出现
  新 run 目录，否则=启动失败立即报告）。旧 `launch_sandbox_e2e.ps1` 等的
  `Start-Process -Verb RunAs` 写法已废弃，禁止再用。
- 产物默认目录：%LOCALAPPDATA%\CurrencyWarsSmartRaccoon\sandbox\run-<时间戳>\
- 产物：sandbox-ops.jsonl（每次操作+判定结果）/ sandbox-violations.jsonl（违规）/
  sandbox-verdict.txt（终局判定）。文件句柄带 FileShare.ReadWrite，值守可边跑边读。
- **单实例互斥被沙箱旁路**（与正式实例并存）。但命令通道文件按 BaseDirectory 取，
  **必须从非稳定目录启动**（如 bin\Debug\...），否则与正式实例共写同一组指令测试-*.txt。

## 二-2、脚本 schema：事件态种子（2026-09-09 聘用书专项新增）

```json
{ "name": "...", "seedWishesResponded": 1, "seedLettersObtained": 0, "steps": [...] }
```

- **用途**：静态帧世界里事件态无法自然推进——祈愿确认后的退出探针需要脚本切帧
  提供非弹框帧，而 `WishesResponded` 只有 Confirmed 才 +1，首祈愿（F9 盲选左）
  因此在沙箱中永远成立，第二祈愿路由（令咒聘用书侧等）永远无法被测。种子把
  事件态直接置为真实局面值（例：`seedWishesResponded:1` 复刻"当局第 2 次祈愿"，
  即视频 2 第 90 秒令咒决议·行为限制在屏时的真实状态）。
- 语义：`seedWishesResponded`（0-4）/`seedLettersObtained`（0-4），缺省 0；负数
  拒绝加载。注入时机=启动时经 `CommandTestWindow.NotifyFrameSandbox` 写入
  stateHolder（仅沙箱实例，`Reset` 后归零语义不变；生产代码零调用）。
- F9 之后的路由（奇迹代偿/令咒聘用书侧/无限之釜/默认选左）由种子解锁可测。
- exe 要管理员权限：无人值守启动仍走计划任务族方案（或用户在场时直接放行 UAC）。

## 三、驱动与判定

1. 启动后是普通指令测试台窗口（有"帧沙箱模式"横幅）。
2. 写 `START`（启动识别会话）→ 写 `DECIDE 开始`（决策层驱动 M8）。
3. M8 沿脚本逐帧前进：每帧的期望操作全部满足→切下一帧；脚本内已满足期望的
   重复操作（快速刷开局盲点连点/复点确认）不算违规；脚本外操作/步超时（默认
   120s，步级可覆盖）=违规。到达终局帧（1-1 备战席，空 expect）=PASS。
4. 终局判定三态：PASS / COMPLETED_WITH_VIOLATIONS（走完但有违规）/ FAILED（超时）。

## 四、冒烟脚本

tests/CurrencyWarsAssistant.Tests/Fixtures/FrameSandbox/smoke_home_to_preparation.json
（主界面→命中 019→1-1 备战停，11 步；帧取自 PageReplay 夹具+视频 2 命中环境页帧，
见 frames/*.provenance.txt）。无窗口回放回归=FrameSandboxSmokeScriptReplayTests；
帧级守卫=SmokeScript_EveryFrame_ClassifiesToExpectedPage（每帧过真实分类器）。

## 五、边界（可行性案 §四，重申）

沙箱 PASS=软件在预期画面序列下走预期路径；≠实机必过。静态帧会让动画等待全秒过
（测试偏松方向）；识别层只受测真实帧出现过的页面；五费聘用书试炼页无真帧（Phase 3）。

## 六、首轮对抗审查整改记录（2026-09-08）

- **P1 命中帧拿错**：初版环境帧误用 run-20260908-065551-reset 的 20260907-225548799.png
  （实为 1-1 备战页）——"分析 JSON 提到 067"≠"截图是环境页时刻"。已换为视频 2
  第 1 秒真环境页（019 命运圣杯邀请在 slot1），双验证：分类器 96.9% +
  OcrOpeningPageReader 三槽读数 019 置信 100%。期望操作随之改为 (960,530) 选中
  （ResolveInvestmentSelectionAction→InvestmentOptionPoints[1]）+ (1083,984) 确认。
  防回归：新增帧级分类器守卫测试。
- P2-2 `--frame-sandbox` 悬空开关=显式报错（原静默忽略会降级成正式模式）。
- P2-3 重复容差设上限 MaxRepeatsPerExpectation=200（防弃局链循环操作被吞），
  破线记 repeat-cap-exceeded 违规一次。
- P3：jsonl 中文不再转义 \uXXXX；Dispose 后不再重建产物文件；与
  --phase2-batch/--phase2-dataset-capture 同传显式拒绝；单步终局脚本拒绝加载；
  冒烟第 1 步 maxWait 统一 180s。
- **P3-4 备案（未改码）**：沙箱与正式实例并存的前提=不同 BaseDirectory 启动
  （命令通道/Recordings 按安装目录隔离）；日志 test-session-*.jsonl 同秒双实例
  会碰撞（CreateNew）——同秒双开属操作错误，规避即可。

## 七、DECIDE 级全链与分析页对齐（2026-09-11 班）

- **分析页对齐（HANDOFF 〇-3.3 挂账）已定案关闭，零生产代码改动**：
  "沙箱 PageId 永非 preparation_*"系前班探针键名大小写错误假象；300ms 捕获节流
  （FileSequenceGameCapture.CaptureCadence，2026-09-09）修复后，管线对静态帧
  持续产出与真实游戏一致的 PageId——回归探针=
  `SandboxPipelineProbeTests.Pipeline_PageId_OnStaticPrepFrame`（静态 prep 帧
  14 秒收集，断言产出 preparation_* PageId）。
- **DECIDE 级全链剧本**=stage_decide_full_chain.json（19 步：M8 导航→1-1 部署
  三月七→M1 出战→结算→1-2 收摊→晶矿/星徽/出战→结算→1-3 策略→商店收摊→终局）。
  E2E 驱动=artifacts\decide_chain_e2e.ps1（部署 D:\CW-sandbox-iter →
  CWTLaunchApp 启动 → START+DECIDE → 轮询 verdict，约 10-20 分钟）。
- **剧本期望必须覆盖引擎的全部合法中间动作**（坑 59 纪律）：静态帧世界里
  M2 晶矿点击后矿球不消失→引擎按 rule 四.4 重试（多点位×多轮）；A4 星徽装配
  靠"重扫面板金簇骤降"自证→静态帧必然判失败→按 476baf0 换抓取点重试 3 次。
  这些操作如未写进剧本 expect 即判 unexpected-op 违规（第一轮 E2E 12 条违规
  全部源于此，引擎行为符合 rule 四.2 流程口径）。修法=把 ops 实测坐标回填进
  剧本期望；重试自动落 Repeat 容差。
- **指南风暴探针**=guide_storm_probe.json + GuideStormProbeTests（2026-09-11）：
  normal_hud→打开指南→默认落每日实训页（切第三页签 605,229）→进货币战争→
  进局，11 帧全链；帧级守卫证实 2560×1440 风暴实况帧被正确分类
  （guide_daily_training/guide_currency_wars，bc56a6e 修复在实况分辨率下有效）。
- **testhost 残留锁注意**：dotnet test 异常中断会留 testhost 进程锁
  Tests.dll/Vision.dll，后续构建 MSB3027——先 `Get-Process testhost | Stop-Process -Force`。
- **E2E 实例自动收尾（2026-09-11）**：decide_chain_e2e.ps1 已内置 verdict 后自动收尾
  ——残留实例是 CWTLaunchApp（提权）子进程，Stop-Process 拒绝访问；脚本改为写
  `D:\CW-sandbox-iter\指令测试-exit.txt`（沙箱 BaseDirectory，与生产稳定目录隔离，
  **绝不碰生产实例的 exit.txt**）请求自退并轮询 60 秒。实战验证 exited。
  人工收尾同款：写该 exit.txt 等 15 秒。

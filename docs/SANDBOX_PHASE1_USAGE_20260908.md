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

- 产物默认目录：%LOCALAPPDATA%\CurrencyWarsSmartRaccoon\sandbox\run-<时间戳>\
- 产物：sandbox-ops.jsonl（每次操作+判定结果）/ sandbox-violations.jsonl（违规）/
  sandbox-verdict.txt（终局判定）。文件句柄带 FileShare.ReadWrite，值守可边跑边读。
- **单实例互斥被沙箱旁路**（与正式实例并存）。但命令通道文件按 BaseDirectory 取，
  **必须从非稳定目录启动**（如 bin\Debug\...），否则与正式实例共写同一组指令测试-*.txt。
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

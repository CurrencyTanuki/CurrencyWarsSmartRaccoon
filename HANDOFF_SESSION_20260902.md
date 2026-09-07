# HANDOFF——货币战争智能狸（2026-09-07 深夜交接版，历史细节见 git log 与文末归档指引）

> 新 AI 必读四件套：**本文件** + **rule.md**（规则与坑账本 1~52，含铁律 1~8 + 第八节监督纪律） + **docs/WINDOWS_OPS_STANDARD.md**（Windows 操作强制标准） + **记忆文档**（自动加载，重点 3 个：windows-ops-standard / currency-wars-project-state / supervision-invariant-discipline）。
> 决策树：docs/DECISION_TREE_1-3三星五费_定稿版_20260829.mmd；指令集：docs/GRAIL_COMMAND_SET_v43_final.md。
> **⚠️ 本文件已精简重写（2026-09-07 深夜）：1.2.101~1.2.119 逐版本细节压缩进"二、今日总账"，更早历史见文末归档指引。**

---

## 〇、09-08 晨班增量（最新事实，覆盖下文过期快照）

- **🔴 启动静默已定性=启动模式错误，非代码回归**：09-07 14:33~18:15 全部 7 个"静默"实例（14:33/14:47/14:51/18:03/18:08/18:10/18:15）jsonl 首事件=OpeningFilterSelectionsLoaded=**MainViewModel 专属签名=普通模式（无 --command-test）启动**；普通模式天然不消费指令文件+提权杀不掉+占单实例锁令后续计划任务实例 12 秒自退（jsonl 0 字节）。command-test 签名=首事件 Phase2RecognitionWarmUpCompleted（22:22 健康局与 23:31 实例均如此）。**上一班的 DI 二分撤回前提被推翻，已恢复被撤回的两注册（closeShop Func+IWishTrialPopupHandler）**。
- **autodecide 挂机补全（09-08 代码）**：23:31 取证证明原实现无窗口时 DECIDE 直接失败返回，"开机开游戏自动继续刷局"不成立——已实现等窗循环（20s 探测，窗口就绪经 UI 线程自动 DECIDE；决策层结束后 10s 重新武装；DECIDE 停止/关窗解除武装）。构建 0/0+涉事测试 161/161。
- **未解尾巴（不阻塞部署）**：①谁/为何以普通模式启动 7 例（嫌疑=09-07 下午回退实验期直接 start exe/双击，UAC 由在场用户点过）；②23:31 实例（1.2.117）23:37 交接后无崩溃记录干净消失（疑外部结束或另一次启动的 exit 握手）；③写 autodecide.txt 与启动存在竞态（Loaded 先过则开关漏检，23:31 实锤：START 被定时器消费而 DECIDE 未发）——**每次启动后必须核对：jsonl 首事件签名+autodecide.txt 是否已被删**。
- **帧沙箱可行性案已出（用户 09-08 令）**：docs/SANDBOX_FEASIBILITY_20260908.md——动机=五费聘用书试炼永远等不到真帧；方案=DI 层换 4 个基础设施实现（FileSequenceGameCapture/RecordingInputController/StubWindowService/AlwaysForegroundGuard），决策/操作/识别零改动；脚本=每帧+允许操作集，期望操作满足才切帧、偏离=违规（驱动器+裁判合一）。接缝与素材已勘察：PageReplay 107 张+runs screenshots 316 张+归档 1759 目录；**缺口=五费聘用书试炼页帧（素材来源 a 抽帧/b 合成/c 等真帧，待用户拍板）**。Phase 1 骨架待开工（批次二审查与部署优先）。
- **帧素材审帧完成（用户 09-08 令）**：docs/SANDBOX_FRAMES_AUDIT_20260908.md——视频本就在桌面（无需回 B 站）；**视频 2（A850 三星昔涟）=1920×1080 原生主素材源，拿到「5费聘用书·请选择1个」选择页全帧（第 119 秒）等核心画面**；视频 1（全网首发教程）=1728×1080 画幅不匹配仅作参考；叠加物（水印/中央字幕/后期大数字）全部可用选帧规避，不构成阻断；视频 1 解说=纯脑测未验证不采信，只采信画面。帧在 %LOCALAPPDATA%\CurrencyWarsSmartRaccoon\sandbox-frames\（不入 git）。
- **批次二对抗审查完成（09-08）= APPROVED after fixes（2×P1+4×P2+8×P3）**：P1-1 弹框守卫从未接线（死代码）/P1-2 四.13a 数据源在 SoftFallback 路径断裂+事件未落 jsonl；P2-1 13a 双重 A9+标签污染/P2-2 Dead 出口混标 R3/P2-3 买经验缺 1-3 门/P2-4 血量缓存跨局陈旧/P2-5 autodecide 三处竞态。**修复已全部落地（562cb98）**：modalGuard 接线+IModalGuard 注册、SoftFallback 带 strategyId、StrategyAbandonDecided/BuyXpForDeploy 经 publishEvent 落 jsonl（引擎新增可选参数）、13a 单点 A9、四 Dead 出口标签真实化、买经验 1-3 节点门、血量缓存随局复位、autodecide 派发时刻原子复查+失败回炉+循环兜底、wish 应答实例信号量互斥（P3-1 同批）。build 0/0+161/161。**修复复核已发回原审查员（后台），PASS 即部署**。
- **挂账新增**：①引擎级 13a/买经验判定单测（SendAsync 私有不可注桩，重构引擎管道超出本批）=**goal=All 启用前硬前置**（当前 Single 下两判定休眠）；②P3 备案项：闩锁布尔语义无法区分人口步进、降级重扫判据 Count<5 与 P2-5 量化口径不一致、弃局链无环境页"过路必选"分支、selectLeftmostStrategyIfUp 无注入点恒 null、证据文件无上限需轮转、XML 注释漂移两处。
- **🔴 启动挂死真根因已复现实锤（09-08 凌晨，用户质询后重新定位）**：1f61037（09-07 13:05）引入的 `AddSingleton<Func<nint,CancellationToken,Task<bool>>>` 单例工厂与"controller(transient)→其构造参数 IAbandonSettlementRecovery→recovery→再注入同一 Func"构成**解析重入死锁**——命令测试台启动线程卡死在 `GetRequiredService<CommandTestWindow>()`，闪屏永不关闭、jsonl 0 字节、指令通道不消费。**证据**：①部署该注册后的 13:58/14:01/14:04/14:06/14:10 五实例 jsonl 全 0 字节，而 1.2.117(72da03f,无该注册)的 22:22(2729 事件)/23:31 健康；②形状保真复现测试（tests/DiResolutionCycleRegressionTests.cs）实跑 testhost 挂死 519 秒 CPU 冻结。**更正**：09-07 下午的"静默"实为两个并发现象——14:33~18:15 七实例=普通模式误启动（我此前误当成全部根因）；0 字节实例=本死锁。上一班 14:16 的 DI 二分撤回方向本是对的但从未重建复测，本班恢复注册时把死锁一并带了回来。**修复方案（待子代理核验后实施）**：撤销 Func 单例注册，改逐接口显式工厂（IRejectedOpeningRecovery/IRunAbandoner 各自先解析 controller 再 new recovery 注入关店闭包；IAbandonSettlementRecovery 保持普通 Transient=1.2.117 行为；同型双参数误注入随之消失）；卡死实例 PID 49880 提权杀不掉（一次尝试拒绝访问，升级用户任务管理器）。子代理核验中（修前验证）。
- **DI 死锁修复已落地并双核验（09-08 凌晨，ef33819）**：核验子代理独立确认定位（内存转储抓到"无限递归→StackGuard 线程跳转→跨线程锁互等"完整机制 + 仓库外纯 DI 对照实验：无 Func 注册 6.5ms / 有则挂死），修法获支持；修复复核子代理（第二轮）已在跑。全量涉事测试 162/162（含新的形状守卫测试，<1ms 完成）。**唯一阻塞=卡死实例 PID 49880 仍在**（提权杀不掉：schtasks 改动作要密码、UAC 弹了一次被取消——违反占屏预告铁律，已向用户预告后将再弹一次）。49880 不死则互斥锁+App.dll 文件锁都不释放，部署与启动全部被堵。
- **通宵审计准备已就绪**：docs/OVERNIGHT_AUDIT_20260908.md 骨架已建（协议=逐局三问+八不变量+操作质量审计[冗余点击/坐标偏移/降级触发/过慢]，证据确认无异常即删）；证据通道位置已摸清（abandon/guard/deploy-evidence 在 %LOCALAPPDATA%\CurrencyWarsSmartRaccoon\ 下、input-diagnostics 在 logs\、run 目录在 runs\、DECIDE 录像在稳定目录 Recordings\、result 归档在稳定目录）。决策树+机制口径已通读。
- **下一步**：修复复核 PASS→按 WINDOWS_OPS_STANDARD 五步部署 1.2.119→验 App.dll ProductVersion=1.2.119+HEAD→计划任务启动→验 jsonl 首事件=warmup+STATUS 回执→写 autodecide 并复核被消费。**用户已授权：审查通过即可直接发布并打开软件（游戏暂不开，之后用户令继续）**。

---

## 一、当前状态快照（2026-09-07 深夜，已被第〇节覆盖，仅存档）

- **运行中：1.2.117（72da03f）挂机实例（PID 31572，计划任务拉起）**——DECIDE 已消费，M8 刷局循环在跑，**游戏未开**（用户睡前关闭），实例处于"未找到游戏窗口"失败重试循环中等待；**用户开机开游戏后自动继续刷局**。
- **🔴 1.2.119 未部署运行**：代码完整保留在 HEAD（1587fb1+3050092），含批次一（簇 A/C/H：弃局状态感知分流+弹框守卫+NavigationFailed 修复；两轮代码审查终审 APPROVED）+批次二（簇 D/E/F：四.13a goal=All 门+四.5 买经验闩锁/快照不可得终态机+弃局标签拆分/刷新换点位+降级重扫；**未做整批对抗审查**）+证据留存增强（7 个帧目录+决策叙述归档）。**未部署原因=启动静默回归未定位（见三.3）**。
- **启动静默回归（未解，最高优先）**：1.2.118 与 1.2.119 的实例启动后"静止"（jsonl 停在启动 4 事件、指令通道失效）；1.2.117 正常。已排除：睡眠（standby/hibernate/unattendsleep/monitor 四项已禁）。未定位：具体阻塞点（提权进程无法 dump，权限墙）。今日 14:04 后每个新实例都如此；1.2.117 是唯一正常版本。
- **明日审计素材已留存**：22:22-23:14 的 1.2.117 挂机 52 分钟（jsonl 678KB+决策叙述+22:53 出战失败 battle 帧）；**decide 录屏丢失**（软件异常退出，temp 未落盘——1.2.113 录屏机制已知风险）。
- 稳定目录：C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前（路径永不变）。发布命令：powershell -ExecutionPolicy Bypass -File artifacts/deploy_stable_1271.ps1（**注意：脚本不构建，必须先 dotnet build**）。
- 历史对局记录已清理：978 个 run-* 对局+781 个旧测试会话归档至 %LOCALAPPDATA%\CurrencyWarsSmartRaccoon\runs-archive-20260907\（用户令"清历史"；今日 235 个 cmdtest 证据会话保留待分析）。

## 二、今日（09-07）总账

- **1.2.118（72da03f）通宵发布**：三 P1 修复（赛季主页识别/gala 彩色锚点/分发器 A16 路由）；通宵挂机 22 命中。**全量逐局重审=docs/RE_AUDIT_20260907.md**（22 命中局逐局+七簇根因——**审计方法教训：事件存在性核对抓不到操作质量/规则符合性问题，必须逐局三问+不变量推演**）。
- **1.2.119（未部署，代码在 HEAD）**：批次一（簇 A 弃局状态感知分流 90s 上限+簇 C 弹框守卫实拍化+簇 H NavigationFailed×5 修复）+批次二（簇 D 四.13a goal=All 门+四.5 买经验闩锁/簇 E 快照不可得终态机+弃局标签拆分/簇 F 刷新换点位+降级重扫）。**勘误**：四.13a"该弃未弃 5 例"系审计漏核单人模式前提（单人永不弃），已在 RE_AUDIT 勘误。
- **证据留存增强**：abandon-evidence（弃局 4 节点）/deploy-evidence（部署+卖出失败）/guard-evidence（弹框守卫应答前后）/result.txt DECIDE 启动归档。
- **Windows 操作标准建立**：docs/WINDOWS_OPS_STANDARD.md（六节+第七节 AI 常见错误网络调研对照表 19 来源）+记忆 windows-ops-standard.md+MEMORY.md 顶部索引。
- **挂机中断一次**：23:14 用户关游戏→决策层停止→软件退出；23:31 重启（计划任务+autodecide）后处于"等游戏窗口"状态。

## 三、🔴 今日错误清单（新 AI 必须逐条读，防止重犯）

1. **回退实验未当场声明版本状态**：git checkout 72da03f 回退实验后构建部署了 1.2.117 覆盖稳定目录，**未及时向用户说明**——用户以为在跑 1.2.119。教训：**任何版本切换/回退，当场向用户声明+入档**。
2. **审计漏核前提**：四.13a"该弃未弃 5 例"全部误判（单人模式永不弃，rule 四.13a 明文）——**推理前必须核对目标模式/阶段等前提不变量**。
3. **启动静默回归（1.2.119 待定位）**：今日 14:04 后 1.2.118/119 多实例启动后静止（jsonl 停 4 事件/指令不消费/杀不掉）；1.2.117 正常。**已排除睡眠（三项禁用）；未定位根因；诊断受权限墙限制（提权进程 dump/kill/UIA 全拒）**。嫌疑：批次一/二某处启动路径改动；或提权实例+用户锁屏的 UIPI 组合。**定位手段建议：提权终端 dotnet-dump，或代码 review 启动序列（OnStartup→StartupWindow→主窗）的新增同步等待**。
4. **录屏丢失**：DECIDE 录屏写 temp（GrailRecordingTemp），软件异常退出=录屏丢失（22:23 的挂机录屏已丢）。**改进方向：滚动段定期落盘**。
5. **部署文件锁**：挂死实例锁 dll→robocopy 跳过→新旧混搭。**部署前必须确认进程全退；部署后必须验 App.dll ProductVersion+Tasks.dll LastWriteTime**。
6. **审计方法**：事件存在性核对≠审计；**三问逐局（该做吗/做对了吗/失败处置对吗）+不变量推演**才是有效审计（今日全量重审七簇根因全部由此才发现）。
7. **回退实验破坏工作树**：git checkout 旧 commit 会回退全部文件（含 HANDOFF）——恢复时必须逐文件核对（本文件曾短暂变回 09-06 版）。

## 四、挂账（全部未销）

1. **定位 1.2.119 启动静默**（最高优先，见三.3）→定位后部署 1.2.119 实机验证（验收清单=FIX_PLAN 第五节）。
2. **簇 B**：A4 点选收尾（Esc 限点选模式+abandon_settlement_prompt 首判据）+**详情框识别模板标定（需实机帧）**+坑 48 三处同步。
3. **簇 I**：局 5 漏买吉尔根因（白名单代码无缺陷可指，需专项）。
4. **备案**：盲点急停专测/selectLeftmostStrategyIfUp 接线/批次二整批对抗审查/**Func<nint,CancellationToken,Task<bool>> 已是容器注册类型——新类型同型可选参数会被静默注入关店委托**。
5. 用户睡前指令："弃局的对局不入历史，只有刷取成功的进历史"——**未实现**；建议方案=弃局 run 目录移入归档子目录（列表干净+证据保住），彻底删除会丢证据，**实施前与用户确认**。
6. 大金球参数校准（B.4，等真帧）；Esc 首试率埋点。

## 五、Windows 操作强制标准

见 **docs/WINDOWS_OPS_STANDARD.md**（启动/停止/部署/进程诊断决策树/睡眠防护/转义编码/第七节 AI 常见错误网络调研对照表/禁止事项 10 条）——**全部来自今日真实案例+官方文档溯源**。要点：启动只能走计划任务（直接 start 触发 UAC 无人确认）；提权进程无法被普通权限 AI 杀/dump/UIA（连续失败立即升级用户）；部署=build→停旧验退→robocopy→**验 App.dll ProductVersion**；挂机前 powercfg 四项禁睡眠（standby/hibernate/**unattendsleep**/monitor）。

## 六、下一班交接提示词

见本文件末尾"七、交接提示词"（用户可直接复制）。

## 归档指引

- 1.2.101~1.2.119 逐版本细节：git log --oneline -40 每条 commit 均有完整描述
- HANDOFF 精简前全文（含 1.2.101~11x 历史条目）：git show 37beb8a:HANDOFF_SESSION_20260902.md 与 git show 9fa6c5f:HANDOFF_SESSION_20260902.md
- 全量逐局审计：docs/RE_AUDIT_20260907.md；通宵审计：docs/OVERNIGHT_AUDIT_20260907.md；修复方案：docs/FIX_PLAN_1.2.119.md

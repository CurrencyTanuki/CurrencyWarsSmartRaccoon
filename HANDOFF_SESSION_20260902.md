# HANDOFF——货币战争智能狸（2026-09-07 深夜交接版，历史细节见 git log 与文末归档指引）

> 新 AI 必读四件套：**本文件** + **rule.md**（规则与坑账本 1~52，含铁律 1~8 + 第八节监督纪律） + **docs/WINDOWS_OPS_STANDARD.md**（Windows 操作强制标准） + **记忆文档**（自动加载，重点 3 个：windows-ops-standard / currency-wars-project-state / supervision-invariant-discipline）。
> 决策树：docs/DECISION_TREE_1-3三星五费_定稿版_20260829.mmd；指令集：docs/GRAIL_COMMAND_SET_v43_final.md。
> **⚠️ 本文件已精简重写（2026-09-07 深夜）：1.2.101~1.2.119 逐版本细节压缩进"二、今日总账"，更早历史见文末归档指引。**

---

## 〇-1、当前状态（2026-09-10 深夜交接基准，唯一有效状态）

- **已发布运行**：**1.2.123+7836ae3** 部署于稳定目录（哈希已核验），实例运行中、autodecide 已武装（游戏窗口出现即自动 DECIDE）。用户挂机中。
- **1.2.123 含全部根因修复**（均经子代理对抗审查，详见 docs/AUDIT_20260910_121_SESSION.md §九与 git log）：
  1. 商店识别稳定门：三帧仲裁+单帧 miss 容忍+挑战者门槛（连续两帧才可推翻共识）——修复"帧1 动画乱码导致帧2 正确读数（吉尔伽美什/Saber）被整槽丢弃后刷新"；
  2. 金币/血量/人口捕获时间戳=分析帧 AsOf（I5/I6/Assemble 三处）——修复滞后帧以组装墙钟伪装新鲜压过本地账（终态金 1→32→1 幻想）；
  3. R3 判死链：裁决金必须来自新鲜复核帧+账本 15s 新鲜判定，塌缩 0/滞后读数防御跳过判死（误弃不可逆）；
  4. 徽章拖拽按压 450→700ms、时长 1100ms（负载时段吸附劣化 18 连败对策）；
  5. 录像逐局落盘：RotateAsync 每局边界封箱上一段到 Recordings+开新段（F6 根治）；Kill 段进 damaged/ 隔离；
  6. 祈愿兜底选左+观测上限 3（前批）。
- **1.2.123 实机首测（01:19–02:28）已验证**：祈愿 2/2 应答 ≤3s、徽章 700ms 首试即成、商店识别与画面一致（抽验）、无 0-hold 复发、逐局录像三段封箱完整。**未解**：金币读数仍有幻值（真 9 报 27、真 3 报 1——时间戳已诚实但数值仍错）、金尽空转 10 分钟×2、第三局录像器停写。
- **三通道证据持续在录**：日志 jsonl/runs 分析/每局录像（Recordings 归档+GrailRecordingTemp 在录段+damaged/ 隔离）。
- **值守设施**：监视循环 artifacts\watch_duty_123.ps1（60s 快照+sleep 进程检测→D:\CurrencyWarsDataudit-123-watch\watch.log）；监测轮 monitor_cycle.py（同目录，jsonl 增量异常判据）；并行监督子代理按需再派（任务书模式见〇-3 末条）。

## 〇-2、09-09/09-10 两夜完成清单（细节见 git log 与 docs/AUDIT_*.md）

1. **09-09 夜会话（1.2.121）逐帧审计**：docs/AUDIT_20260909_NIGHT_REDO.md——五根因实锤（祈愿死锁/清场残漏/徽章装配/金币幻想/暂停页）；
2. **1.2.121 会话定点审计+独立复核**：docs/AUDIT_20260910_121_SESSION.md §九（含键名大小写重大更正）；
3. **根因修复批一**（abf2e65…ca0cdfb）：祈愿兜底选左/拖拽按压 250-300ms/NotPurchased 不记账/点选装配退役/暂停页防御跳过/F1 分析页 AsOf/F3 新鲜帧裁决；
4. **根因修复批二**（9540c51+e35e689+9f3df27+1254b16+80d9c2e）：稳定门三帧仲裁/挑战者门槛/观测上限 3/金币 AsOf（Assemble+I5/I6）/徽章 700ms/R3 新鲜帧裁决/damaged 隔离/R3 事件落 jsonl；
5. **录像逐局落盘**（2faa018）：RotateAsync 每局边界封箱上一段开新段，生产验证通过（Recordings 三段每局一段）；
6. **规程固化**：rule.md 第九节录屏逐帧审计（10 秒切帧+关键位置全分辨率单帧+双通道对表）；记忆 frame-by-frame-audit-procedure；
7. 1.2.122、1.2.123 两轮发布均 DEPLOY-OK+VERIFY-PASS+对抗审查（三轮子代理：初审查/独立复核/二轮增量）。

## 〇-3、挂账与待查（按优先级，全部未完成）

1. **P0 金尽空转循环 → ✅已修（09-10 03:2x 本班，详见〇-7，构建 0/0+Grail 145/145+Preparation 95 过+对抗审查 APPROVED、P2 已当场修、发布待用户令）**（1.2.123 实测两局各 ~10 分钟）：清场卖出被 reward_shop 页阻断（PreparationBenchSalePageMismatch）后安全停止且永不重试→清场残漏→R3 被"可卖>0"阻塞→空转等 30 轮上限。修法=卖出前查页，reward_shop→先收摊（1620,975@1920，页面已验证后点）再卖。
2. **P0 金币读数幻值 →定性拆分（09-10 本班录像+日志交叉）**：02:07 段实锤**购买失效≠幻值**——吉尔伽美什识别正确、输入到位、画面金 10-11 与账本 9 一致，两次购买后原槽卡均在=reward_shop 紧凑布局购买落点问题（见〇-7.8）；幻值本体（真 9 报 27/真 3 报 1）仍是 OCR/帧源问题，其"抬高快照金→R3 永不触发"危害已由 R3 结构防御（〇-7.6）结构化兜住；识别回归语料库（下条）仍需建，用于幻值定量。防御已闭环（R3 判死需新鲜读数+塌缩跳过），实害=运营低效非误弃。
3. **P1 分析页对齐（重开调查）**：实机识别管线健康（75/86 分析 PageId 正常），**前班"沙箱 PageId 永非 preparation_*"结论系探针键名大小写错误假象**（JSON 为 camelCase：snapshot/pageId）——沙箱会话 0 个 analysis JSON 落盘才是真差异（沙箱模式管线不写产物或未运行到产出）。重开步骤：起长会话沙箱验证管线是否运行→按结果修。
4. **P1 主界面 transition 误标 →降级 P3 观察项（09-10 本班重定性）**：引擎事件流 01:19-01:26 只有**一次 12 秒** Unknown→Esc→主界面恢复（01:20:01-03），"5 分钟卡死"在引擎侧不存在；画面取证（frames_d1/wall_01 帧 3）显示该时刻有一个**白色系统提示弹框**（"继续并锁存/稍后再说"——识别表外）在屏=Unknown 真因，Esc 已兜住。fast 豁免方案搁置（改动识别管线核心，收益/风险不匹配）；若该弹框高频复发再入识别表+自动应答。03:1x 的 Unknown 段已由弃局活锁修复（〇-7.5）覆盖。
5. **P2 识别回归语料库**：ShopRecognized 时存货架裁剪+识别名单（有界环），OCR/识别改动有真帧对照。
6. **P2 三星五费黄金剧本**：分析页对齐修完后，用真帧（奇迹代偿/昔涟货架/聘用书帧已有）编全流程沙箱剧本。
7. **待查**：~~18:14:28 金=13 卖卡是否违反凑息规则~~（**已关闭：UI 日志证据灭失**——result.txt 已轮转，无 09-09 18:1x 时段记录，无法定性）；~~84s 空窗前段 t≈10850-11005 前扩抽帧~~（**已关闭：素材灭失**——1.2.121 会话 11265s 录像已按用户令于 09-10 晚清理，现存 grail_decide-20260909-034921 仅 816s 为另一段）；四.13a 模式盲区（对弈/单人无法从证据确证）。
8. **小项**：15s 常量与 StaleAfter 共享；IsComplete 标注测试专用；[X,X,miss,Y] 门级用例；RotateAsync 单调用者约束注释；FinishAsync 空收尾日志措辞。

## 〇-4、实机验收观察点（下轮挂机）
0. **关游戏卡死修复验收（1.2.124 新增）**：识别会话活动期直接关闭游戏——软件必须 ①UI 保持响应（事件日志无新 AppHang）②60 秒内日志出现"游戏窗口已关闭"失败链或"未找到游戏窗口"响亮留痕 ③进程存活可正常关窗。反向：重启游戏→识别流自动恢复或经 START 重启成功。
1. 每局录像一档封箱入 Recordings（damaged/ 只应收坏段）；
2. 商店每轮识别名单与画面货架一致率 100%；
3. 祈愿弹框全应答 ≤3s；
4. 徽章装配（700ms 档）成功率；
5. 金币账本无跳变（GrailShopPurchaseNotConfirmed 事件应少而真）；
6. R3 判死链：金尽→清场卖光→弃局 ≤3 分钟（不再 10 分钟空转）。

## 〇-5、关键路径速查
- 审计报告：docs/AUDIT_20260909_NIGHT_REDO.md、docs/AUDIT_20260910_121_SESSION.md
- 值守工具：D:\CurrencyWarsDataudit-123-watch\{monitor_cycle.py,duty.log}；artifacts\watch_duty_123.ps1（60s 快照+sleep 检测）
- 沙箱检查点：HANDOFF 〇-13a（分析页对齐调查）
- 录像：GrailRecordingTemp=在录段；Recordings=封箱归档；damaged/=坏段隔离

## 〇-6、交接提示词

交接提示词已独立成节，见文末"九、下一班交接提示词（复制即用）"。

## 〇-7、09-10 深夜班修改记录（03:00 起本班）

**修改（全部未发布未实测，等用户令）：**

1. **P0#1 金尽空转收摊救援（已 commit 46e1250，PreparationFormation.cs CaptureVerifiedPreparationAsync）**：页面门内新增 reward_shop 收摊救援分支——页面分类=reward_shop 时（收摊尝试上限 2，独立于 Esc 预算 3 与 8 次总循环）发布 PreparationRewardShopCloseAttempt，调同类现成 GrailClickReferencePointAsync 点收摊开关 (1620,975)@1920（页面身份验证后才点，1.2.64 决策层救援同款坐标），点击后 250ms×20 步进轮询确认页面已离开 reward_shop 才交回门禁（未确认发 PreparationRewardShopCloseUnconfirmed 留痕）。**对抗审查 APPROVED；P2 当场修**（固定睡 1500ms 的"双向开关二次点击重开店"风险→改步进轮询，复核 APPROVED）；两 P3 记录不修。审查员核对 21 处调用点：不存在"商店页合法开启时调本门"场景。构建 0/0+Grail 145/145+Preparation 95 过/4 跳既有 Skip。
2. **弃局活锁修复（已 commit 93d3277，CurrencyWarsRejectedOpeningRecovery.cs RecoverAsync）**：病理=Esc 双败后确认框识别滞后（见观察 6），双败 Failed 交外层→外层重开时人还在局内→活锁每轮 ~2.5 分钟。修法=Esc 双败分流段①确认框在屏分支（稳定读=abandon_settlement_prompt 或 3s 重探命中→直接走统一结算返回）②preparation_* 分支补按一次 Esc（P-12 口径），仍无效才 Failed。Unknown 禁点/主界面即停/盛会应答/盲点直通红线全部未动。**对抗审查 APPROVED+P2 当场修**：补 3 个契约用例（StagedClassifier 夹具扩 PromptAfterEscapes/PromptDelayAfterEscape 仅 EA≥2 启用/FallbackPreparation 三开关），22/22 过。构建 0/0。
3. **R3 结构防御（已 commit 2f27915；GrailCommandTypes+GrailMacroCommands+GrailDecisionEngine）**：定性依据见观察 7。GrailShopPassFact 加 LiveLedgerGold（仅 grailLoopMode 且本地账有效时非 null，绝不兜底持有器值）；R3 候选与判死纳入 ledgerSaysBroke（本轮 M5 刚实跑刷新失败=行为级金尽证据）+谱系连击 ledgerBrokeStreak≥2 豁免新鲜帧反证（持续高幻值事故态下帧与账本同源；一审 FAIL 后二审 FAIL 各修一轮：一审 P1 复核重卖后不判死 sold==0 闸门、一审 P2 帧反证门、二审 P1 连击豁免）+R3LedgerBrokeVetoed 否决留痕事件；sold==0 硬条件与 OCR-only 路径 P2-2 新鲜帧防御原样保留。构建 0/0+Grail 145/145+Decision/R3 6 过；终审 APPROVED（事故态 2 轮判死/假账本单轮拦截自愈/事件互斥无双发，187/187 独立复跑）。P3 备忘：①执行器回带 M5 终态 endReason/种子来源元数据（后续收紧到首次被拒刷新+封闭停摆窗口）；②XP 拒买角卡死族不覆盖（30 轮上限仍为逃生）。
4. **值守环境整理**：monitor_cycle.py 4 重复实例（共享 state.json 竞态）→1（PID 56144）；watchdog daemon 双实例→1（保 keeper 拉起的）；watch_duty_123.ps1 沿用上班的（PID 53840）；watchdog keeper+daemon 在岗（daemon 高 I/O 下反复卡死被 keeper 正常复活=设计内行为）；审计脚本 artifacts/session_stats_123.py+session_timeline_123.py（全量扫 jsonl 用）。

**运行观察与定性（录像+日志双通道，墙图在 D:\CurrencyWarsData\audit-123-watch\frames_*）：**

5. **1.2.123 运行观察（01:18-04:06）**：刷局循环健康（22+ 轮弃局全闭环、每轮 40-90s、卡壳自愈）；P0 挂账实机复现取证（金尽空转 12min+10min 各一段）；02:47 识别流看门狗挂起报错→恢复链闭环（RecoveryCompleted 02:52:51）；04:06:30 DECIDE 正常结束待机。
6. **新发现（P1）弃局活锁（03:15-03:19 实锤，已修见修改 2）**：快速弃局 RecoverAsync 的 Esc×2 后确认框（abandon_settlement_prompt）识别滞后（4s 验证窗两次未确认，页面读 Unknown/43.7% 低置信备战页）→双败 Failed 交外层→外层重开时人还在局内→导航"直接到达 1-1"判未命中→再弃局；第 3 轮靠 NavigationFailed→决策层 A9 完整链概率逃生。
7. **重大定性（P0 链合并）：金尽空转的驱动者=金币幻值（P0#2）**——全会话（01:18-03:30）R3GoldExhausted=0、R3DefensiveSkip=0，R3 判死链一次都没进：OCR 幻金抬高 snapshot.Gold ≥RefreshGoldCost，R3 候选永假；金尽逃逸全靠 30 轮上限"运营轮上限（异常兜底）"（01:43/02:02 两次各 ~12 分钟）。M5 执行器实时本地账（1.2.24 口径）准确——行为级金尽证据（刚实跑刷新即失败）被弃用。已修：R3 结构防御（修改 3）。
8. **星徽装配失败本会话持续**（03:24 一局 3 试全败，质心 (2483,684) 分毫未动——与 1.2.121 审计发现 1 同族：700ms 档在负载时段仍被拒收）；金尽后 M5"开店→秒关店"循环（03:27 段）属 P0 链第二分支（无货可卖时反复开店），R3 结构防御修后第一轮循环即判死，一并治。
9. **新发现（P2）PurchaseNotConfirmed 定性修正（02:07 段录像+日志交叉，frames_g3/wall_03+wall_05）**：吉尔伽美什（5 费）在架识别正确→购买输入到位（SendInput ok+光标 (647,233)）→两次尝试后原槽卡均在；wall_05 画面定死金 10-11 足够、目标卡可见、反复开关店无成交=**reward_shop 紧凑布局购买落点 (647,233)（16x16 识别区中心）不在可点击区/对该卡面无效——购买交互问题非幻值非金不够**。审计报告"本轮未再出现"需更正（02:07-02:12 段 11 条循环即此）。待专项：落点与 RewardShopCharacterSlots192 点击区核对（注意 5 费卡面尺寸）。
10. **DECIDE 收尾状态（04:06:30 已结束，OK）**：末帧画面（frames_g4/tail06）停在弃局确认框（进度 1-1/血 80/画面金 3）——下轮 DECIDE 入口会遇到（已知页，NavigationFailed→A9 兜底，代价约 1 分钟，非事故）；画面金 3 vs 同段账本 1=压低幻值又一例。**逐局录像 6 段全部封箱 Recordings、damaged 空=验收观察点 1 通过**。P1#4 transition 误标已降级（见〇-3.4）。
11. **购买落点专项离线核对完成（04:4x，原分辨率帧取证）**：①坐标换算验证无误——引擎点击 (647,233)@2560 客户区=ShopCardPoints[0](485,175)@1920（RewardStageAutomation.cs:144），换算正确；②原帧（audit-123-watch/purchase_fail_shelf.png）显示**吉尔伽美什在槽 1、模式内价格 2 金币（非 5 费价格，坑32 再验）、卡带亮绿描边=鼠标悬停高亮——点击确实点中了卡牌**；③点中却不成交的候选根因收窄：a) 购买交互可能需要二次确认（但历史单点击买成过）b) **双货币池嫌疑**：弃局确认框显示"当前特殊金币 3"字段——1-3 投资模式或存在特殊金币（购买货币）与普通金币两池，若购买扣特殊金币（3<2 不成立；但引擎读的"金"与游戏扣的货币若不同池即可解释幻值"真3报1"）——**机制问题待用户口径**：买卡单击直接成交？特殊金币与金币关系？
12. **用户令变更（04:5x）+新缺陷（P1 关游戏卡死）**：用户令"发布新版本+持续实际验收不停"→发布执行：1.2.124+e3f2316 已 build+deploy（robocopy rc=3+版本串核对匹配），首启 STATUS 验收 60s 超时（VERIFY-FAIL）但实例健康（jsonl 首事件 Phase2RecognitionWarmUpCompleted 28.26s 正常）——**随后用户关闭游戏，软件立即 UI 未响应卡死**（用户目击）；用户改令"修好卡死+其他改动生效后再发布，期间不开始比赛（用户要用电脑）"。现场：用户已手动关闭卡死实例与游戏，现场丢失无法 dump（提权）。已排除：autodecide 循环（本实例未武装）、UI 回写非同步 Invoke、无 unhandled-errors.log、WGC 层 3s 超时+异步锁、listener 轻锁。**根因（子代理独立诊断 05:3x 交付）**：主因=WGC 捕获会话对已销毁目标"3s 等帧超时→ResetSession 同步 Dispose"楔死持有 _captureLock 的生产者线程（WinRT Dispose 等待在途回调）→收集循环冻死在 MoveNextAsync→**运行时级冻结**（60s 同进程看门狗同归于尽，stall-diagnostics 零更新实锤）→UI 线程 GC 安全点冻结=立即未响应；叠加缺陷=①游戏关闭盲区（stale 被失焦豁免吞+dead 判据被冻结打破→永不 revive 零痕迹）②看门狗与被保护对象同进程 ③ResetSession 无防御+单例捕获器毒化 ④失败不落回执的静默面。决定性取证：run 目录 checkpoint 04:36:18 后零写入；1.2.123 时代同类冻结 trigger=frame-flow-freeze 看门狗活着响亮失败（stall-diagnostics-latest 04:06:01）——本次看门狗也死了=升级为进程级。**修复三件套（已实施待审查）**：①WindowsGraphicsGameCapture：订阅 GraphicsCaptureItem.Closed→_targetClosed 快路径毫秒级失败+ResetSession 的 Dispose 后台化（楔死时泄漏一个后台线程+WinRT 对象，有界优于挂死）；②CheckStreamHealth 硬判据：窗口消失≠失焦不豁免→revive START 失败响亮留痕（300s 节流防热循环）；③结构性方案留后续版本：进程外看门狗/捕获器每会话实例化/result.txt 心跳回执。
13. **1.2.125 发布+验收暂停态（06:1x）**：卡死修复审查两轮终 APPROVED（P3-A IsWindow 防 ABA 残余/P3-B 退订防护/P3-C 重复退订清理顺手修）→版本升 1.2.125（a05a6ab）→build 0/0+Grail 145/145+Vision 31 过→deploy robocopy rc=3+版本串核对匹配。**VERIFY-FAIL 复现且成因坐实**：06:14:52 启动隔离把 verify 写的 STATUS 当残留改名丢弃（时序竞态）——手动补发 STATUS ⇒ OK（06:16:01）全量回执=指令通道健康，实例 64800 运行中。**新硬判据首次实机触发正常**：06:15:03 revive 尝试 START 响亮失败"未找到游戏窗口"（游戏未开=预期留痕非静默死）。**验收暂停中**：用户令"不开始比赛"（用户要用电脑）——未写 autodecide 未发 DECIDE；游戏开启后 revive 链每 300s 自动尝试 START 留痕，DECIDE 等用户令。
14. **值守监测判据升级（06:2x，为持续验收做准备）**：monitor_cycle.py 新增判据——①修复生效信号（R3GoldExhausted/R3LedgerBrokeVetoed/PreparationRewardShopCloseAttempt/CloseUnconfirmed 正常也记录供验收核对）②红旗：GrailShopPurchaseNotConfirmed 同段连发 ≥3（购买失效复发）③红旗：RecoveryKeySkipRetryUnknown 同段连发 ≥3（弃局活锁前兆）。watch_duty_123.ps1 修 result.txt 编码读取（曾按 UTF8 读 GBK 产生乱码写入）。两监测进程已重启加载新代码（watch_duty PID 58928/monitor_cycle PID 64076），工具 artifacts/watchers_status.ps1（监测进程盘点）。
15. **指南风暴专项测试+根因定案（10:2x-11:3x，用户令专项）**：用户口述三现象（局结束异常退到游戏主界面/游戏主界面无法识别/指南也失败）全部定案。**真根因（子代理独立复核推翻值守 AI 两轮假说，置信 ~90%）**：赛季刷新（09-07）→指南窗口默认页签变"每日实训"页（不在识别表，guide-shell-title 模板在新渐变背景 0.769<0.9 判 Unknown，OCR 兜底只认壳 98-99%）+切第三页签坐标 (803,281) 贴页签下沿间歇失败→Unknown→Esc 无效→TimedOut 死循环 2h09m；入口=07:55 金尽弃局失败盲点直通落 normal_hud（缺陷 C）。识别/捕获/分析层全程存活（v1 的"识别失效/分析层死亡/捕获层死亡"三轮假说全被证伪——教训：证据时序对位与帧源归属必须先验证）。报告 docs/GUIDE_STORM_TEST_REPORT.md（v2 终版）+125_SESSION.md（含勘误史）。
16. **指南风暴修复 F1/F2（已实施待审查，11:1x）**：①识别表+guide_daily_training 页（priority 35，单锚点 guide-daily-training-tab.png 阈值 0.85——模板自测每日实训页 0.995/旷宇纷争 0.612 区分度完美，红点已避开）②AutomationPageIds.Ids+FastPageIds 三 guide 页同步（坑 48 三处；guide_shell/guide_currency_wars 此前反向漏配一并补）③navigation-flow+guide_daily_training 节点（从每日实训页切第三页签，自愈循环）+normal_hud.open_guide 预期同步④守卫 17→18 步+新断言。构建 0/0+Grail 145/145+Navigation 守卫过（CompositeAnalyzer 失败=既有潜伏失败非回归）。F3（Esc 升级）/F4（漂移告警）/F5（弃局落点验证）留后续批。
17. **1.2.126 发布+持续验收运行（12:10）**：指南风暴修复审查 APPROVED（P2-1 坐标重标 605,229=页签中心已采纳；P3-1 PageReplay 帧级守卫 4 条+P3-2 JSON 尾换行+P3-3 AutomationPageIds 存在性守卫测试当场修；子代理 worktree 基线复测证实 CompositeAnalyzer 等 2 失败为既有）→版本 1.2.126（bc56a6e）→build 0/0→deploy robocopy rc=3+**VERIFY-PASS**（STATUS 端到端全通）→实例 58676+游戏（用户 11:08 开）→12:10:40 DECIDE 启动（I1=investment_strategy 引擎接管当前局）→**持续验收运行中**，观察点照 artifacts/acceptance_playbook_125.md 逐项核对。
18. **用户拍板+星徽专项+沙箱提示词（12:2x）**：①**用户拍板（金蒸发）**：金蒸发很可能是有目标但识别漏了；就算真无目标刷半天没有，也**直接退局重开**（不加保存止损）——R3 链方向获用户确认。②**星徽装配专项（取证+一项修复）**：1.2.126 会话 07:01/07:21 各 3 试全败（质心 2483,684 分毫不动，模板分 0.67），与 01:46 成功例拖拽诊断**字节级零差异**；落点/角色/页面全部正确（badge-evidence 帧证：两位置都是真星徽）；根因收敛=**游戏侧对正确输入间歇拒收**，121 审计"不可定案"在 700ms 时代依旧。**已实施**：重试换抓取点（attempt1 质心/2 +30px/3 -30px，121 审计既定建议，476baf0）；实机验证等游戏。③**沙箱开发提示词已写**（artifacts/sandbox_dev_prompt.md，用户自开新对话完成沙箱剩余：T1 分析页对齐/T2 部署后帧编码/T3 指南风暴探针/T4 收尾）。④**用户令变更**：用户自己在测试游戏——DECIDE 已于 12:13 停止（abort），何时开始验收等用户令；1.2.126 实例+识别会话保持运行。
19. **剩余离线任务全部完成+1.2.127 发布准备（23:0x-23:3x，用户令"离线任务全部做完优化好再发布，发布后不要运行"）**：①**F3**：WaitForStablePageWithRecoveryAsync 的 Esc×3 失败后升级动作——点指南窗口 ✕ (1449,55)@1920 关闭吃 Esc 的每日实训页再识别一轮（81 次 TimedOut 原样重试 2 小时的直接对策），commit 6cc25cb。②**F5**：盲点直通落 normal_hud（游戏大厅=模式被退出）时发 Warning 级落点事件（M8 指南链 1.2.126 起可自动导回），区分两种落点供审计。③**F4**：monitor_cycle 红旗——TimedOut 含"星际和平指南/页签"连发 ≥3 报警（指南风暴复发/锚点漂移）。④**分析调用超时**：本轮决策不碰生产识别管线（改动面需专项审查；指南页修复后"卡 Unknown 页→分析停摆"的触发源已消除），留待复现取证后专项。⑤**购买失效**：仍待用户两个口径（买卡交互/特殊金币）。⑥1.2.127 发布待审查 APPROVED 后执行，**发布后不启动实例**（用户令；deploy 验收后即写 exit 干净退出，预案 artifacts/publish_127_no_run.ps1 一键版+clean_exit_after_deploy.ps1）。
22. **值守交接（03:2x，用户令：软件开着不启验收，换上下文交接，提示词直接写聊天框，重点=值守工作流复述）**：1.2.127+76a33a7 已发布运行，实例 12096+游戏 12404+识别流健康（I1=currency_wars_home 帧龄 5s），DECIDE 未发。值守工作流与准则的复述文本已写入聊天框并存档 artifacts/handover_prompt_127.md——接班 AI 必读三件套+工作流复述+任务清单（验收观察点/购买失效待口径/星徽实机验证/R3 收摊触发观察/导航失败偏高/分析层停摆取证预案）全在提示词内。
23. **接班核实+值守设施重启（09-11 04:0x~04:1x，本班）**：全项核实通过——git 仅 4 个前班遗留 watch_*.ps1（已补档入库）；实例 12096+游戏 12404 存活；对时 04:0x 无挂起空洞（会话 jsonl 223B 停 3:20:18=待机正常非冻结）；I1 复核 currency_wars_home 无祈愿弹框帧龄 3s 识别流健康。**两处设施断岗（交接单口径与实况不符）**：①反摸鱼 watchdog daemon 自 09-10 11:35 用户日班暂停后未再启动（state.json active:false 实锤）→本班 04:03 重启 pid 24876（裸启 daemon 标准形态；--dirs 定制指向本工作区产出面 docs/artifacts/tools+HANDOFF+rule.md 两文件，心跳/告警仍落 guide 工作区供 hook_gate 消费；watch_start_watchdog.ps1=ASCII 启动器，Desktop *20260826 过滤器解中文路径）；gate 挂载自检过（值守态 sleep 探针被拦）。②watch_duty 60s 快照自 09-10 23:04 断岗（watch.log 停写，交接单"在岗"系陈旧信息）→本班 04:10 重启 pid 24760（watch_start_watchduty.ps1）。**文件口径澄清**：monitor_cycle.py 写 duty.log（每分钟在刷=健康），watch.log 是 watch_duty 的文件，两文件勿混（本班初查曾误判 monitor 断岗）。**I1 陈旧帧告警一次（04:03:55 页面=transition_animation+20s 无新帧）**：紧接复发 04:05:20 即 currency_wars_home 帧龄 3s——定性=静默画面期按需截帧的瞬时现象非冻结（1.2.127 陈旧帧守卫按设计留痕），挂观察项：验收运行中若复发需对照帧流。DECIDE 仍未发等用户令，基线与观察 22 一致。
24. **识别语料库扩充（09-11 04:1x，等令空窗离线产出）**：popup/ 新增 guide_currency_wars_storm_20260910_full.png（复制自 T3 探针测试夹具 PageReplay，2560×1440 风暴实况真帧）——指南回归帧凑齐三件套（每日实训/祈愿/货币战争页签）；README 已入库表补记前班两笔漏表（wish_trial/daily_training）+挂账"1.2.126/127 金状态（账本,画面）配对帧"待下轮空窗（需 ffmpeg 顺序解码 065358/071619 段+jsonl 时间配对）。库在 D:\CurrencyWarsData\recognition-corpus（长期资产勿删），本次零生产代码改动。
25. **语料库配对帧落地+录像时间基缺陷实锤（09-11 04:4x，用户令链：完成离线任务→发 DECIDE→复盘→持续观察）**：①语料库新增 5 帧（1.2.126 会话 065358/071619 段顺序解码 492 帧→双子代理墙图定位→全分辨率逐帧核验）：1-2 货架（大丽花/万敌/试用标形态，金7）、1-3 货架×2（花火在架金16/乱破双藿藿+槽1卡背动画帧金16）、金真值 27/23 各 1 帧；f0169 帧=坑60 同源"动画帧陷阱"活样本（同帧日志槽1=Saber、画面槽1=卡背）。②🔴**录像时间基≠墙钟实锤（审计级新发现）**：内容锚定三点位互证——f0016(1-1备战)→f0052(1-2商店) 108s 视频 vs ~250s 墙时；f0052(07:20:55)→f0169(07:28:30) 351s vs 452s；f0147 与 f0169 相距 66s 内容逐像素相同（编码冻结重复）。负载期丢帧/冻结，**帧号×间隔推墙时不可信，必须内容锚定**；验收观察点 1（逐局封箱完整）需加"时间轴覆盖核对"，逐帧审计报告覆盖度声明须含此项。③子代理墙图级读数两处实锤不可靠（金23 缩略图读成"约0"、1-2 货架读成"疑似弹框"）——墙图只用于定位，判定必须全分辨率核验（既有纪律再验证）。④A 段（065358）零商店帧=弃局重刷局（备战/战斗/弃局确认循环）。⑤临时帧/墙图/核验裁片已清理；artifacts 新增 make_walls.py（PIL 拼墙，ffmpeg 本机无 glob demuxer）+corpus_verify_crop.py。DECIDE 发令在即（用户 04:2x 明令授权）。
27. **Fix A 金尽提前收摊：全管线交付+1.2.128 发布+生产验证 PASS（09-11 06:1x）**：①用户 04:2x 令链（离线完成→发 DECIDE→全量复盘→持续观察不停止）——DECIDE 04:41:52 首发口径见报告 docs/AUDIT_20260911_ACCEPTANCE_127.md；②徽章第 7 试首试即成（Y=479 干净排）+04:50 三连败（Y=684 遮挡排 ±30px X 无效）=**位置相关失败假说 2/2 实证**（badge-evidence 成对帧+badge_compare 裁片），随机拒收定性降级，Y 轴补偿修复方向待用户拍板；③金尽 4 分钟停滞双局实锤→**Fix A 提前收摊**（GrailDecisionEngine opsRound 循环：账本 LiveLedgerGold<刷新价即 PageAsync 门控收摊，earlyCloseClicked 防双向开关二次点击）——对抗审查一审 FAIL（守卫引用已被覆盖的 snapshot=死代码，审查员行号级证据）→修正→二审 APPROVED；④**1.2.128+f986c98 发布 VERIFY-PASS**，DECIDE 05:57:35 重发；⑤**06:13:09 R3GoldExhausted 正式落日志**（连击=5，可卖=0）=观察点 6 由"事件缺席"转 **PASS**，金尽→判死→弃局全程 2.5 分钟；⑥冻结根因取证终裁：044623 段 12 连续静态帧=录像器有帧（捕获层活），**识别管线静态页假性饥饿+自增强循环**（修复方向挂用户拍板：静态页豁免/心跳帧）；旧实例 12096 轮转停摆随替换自愈，044623 已补封箱；⑦**账本漂移升级 P2 建议项**：卖出金款不入执行器本地账（画面金 3 vs 账本 1 实锤），导致金尽后拒绝刷新空转+R3 判定延后——修复需动执行器记账（敏感），等用户拍板。
26. **DECIDE 验收开跑+前 31 分钟战报（09-11 05:1x，用户令链：离线完成→DECIDE→全量复盘→持续观察不停止）**：DECIDE 04:41:52 回执 OK（目标=单人）。31 分钟 17 轮刷局：14 干净弃局+1 命中（命运圣杯邀请，第 5 轮进 1-3：晶矿双趟/三帧仲裁×3/买吉尔伽美什成功[后置3帧 ConfirmedInShop]/祈愿兜底选左 4s 退出/凑息卖绯英+清场卖砂金卡芙卡[费用实查 costs=2 非5费 合法]）+零 NavigationFailed/零 TimedOut/零 PurchaseNotConfirmed。**两 P1+一未达**：①徽章 ±30px 修复机制生效但 3/3 拒收（诊断实锤三点位，假说否定）；②捕获子系统 04:58 起帧流反复冻结×3+会话重启×3+录像轮转停摆（044623 段 219MB 滞留 Temp），引擎 RFC 盲点推进自愈闭环 ~4 分钟（结算统计页截图取证+result.txt 侧信道定位=jsonl 观测盲区实锤，值守必须双通道）；③R3GoldExhausted 事件 0 次（判死条件齐备未落日志）=观察点 6 部分达标，下局健康流金尽重测。报告=docs/AUDIT_20260911_ACCEPTANCE_127.md（增量中，8 次 commit）。语料库+5 帧（含双藿藿疑难样本+动画帧陷阱活样本）、前班 4 段夜录像归档、语料 README 时间基警告均已入档。
21. **F3 坐标勘误（09-11 00:4x，审查员像素级取证）**：初稿升级点击 (1449,55)@1920 映射 (1932,73)@2560 为**空背景**（真 ✕ 距其 550px）——帧目视换算误读。真 ✕=**StandardPoint(1862,66)**（四帧像素级一致：(2483,88)@2560→(1862,66)）。且修正后 (1862,66) 与 currency_wars_home 右上角**模式退出 ✕ 近重合**——加指南在场 OCR 门控（壳标题置信 <0.72 不点击发"升级放弃"Warning）。F4 阈值 ≥3→≥2（真实风暴 60s 窗口最大值=2 永不可达 ≥3）。commit 9a0410c，终审中。**教训入档**：帧目视估坐标必须像素级取证（亮斑质心法）+与既有 UI 元素（模式退出钮）近重合的点击必须加在场门控——坑 39"新增盲点击先答这个位置在别的页面是什么"的进阶版。**Windows 事件日志实锤**：04:37:07 Application Hang (AppHangB1) 1.2.124.0——UI 线程真挂死（OS 级）；**历史 hang 仅两条**（本次+08-02 0.2.777 远古版）=9 月以来全版本从未挂死→**低频存量缺陷非本班回归**（三新 commit 均为决策层文件，待命态无执行机会；触发条件推测=识别会话活动期直接关闭游戏，既往用户总先停引擎）。**VERIFY-FAIL 成因候选**：verify 脚本写 STATUS 时机撞启动隔离窗口（04:34:39 残留改名可能把 STATUS 当残留丢弃）——下次发布验收失败时先手动补发一次 STATUS 再定性。③**识别回归语料库已建**（D:\CurrencyWarsData\recognition-corpus\，README+首批 5 帧：购买失效货架×2/金尽/正常金/弃局框）——P2 挂账"识别语料库"基建完成。
20. **帧沙箱收尾班终态（09-11 00:0x-01:0x，沙箱开发提示词 T1-T4 全部闭环，零生产代码改动）**：
    - **T1 分析页对齐=定案关闭（〇-3.3 挂账销账）**：探针测试 `SandboxPipelineProbeTests.Pipeline_PageId_OnStaticPrepFrame`（37b074a）证实 300ms 捕获节流（2026-09-09）修复后沙箱管线对静态帧持续产出 preparation_* PageId，与真实游戏一致；前班"PageId 永非 preparation_*"确认为键名大小写假象。**零生产代码改动**——"机制未知需查 judge 切帧入口"的预计改动点不存在。
    - **T2 部署后帧编码=已在位**：stage_decide_full_chain step11 部署三月七 drag→step12 stage_s2_prep_deployed.png 出战链路工作（两轮 E2E 全部 SatisfiesPending）。
    - **DECIDE 级 E2E 验收 PASS（run-20260911-001934）**：stage_decide_full_chain 19/19 步 0 违规，从 M8 进局到 1-3 商店循环全程引擎未误走弃局=快照门禁（GrailRunLoop.AssembleLatest preparation_* 前缀）沙箱下畅通。**首跑 12 条违规归因=剧本期望缺失（坑 59 同型，非软件缺陷）**：step15（preparation_1_2）引擎合法动作 M2 晶矿三点位×3 轮（静态帧矿球不消失→rule 四.4 重试）+A4 星徽换抓取点×3（476baf0；静态帧金簇自证必败=沙箱已知局限）未写进 expect——ops 实测坐标回填后重跑 PASS（d408555）。
    - **T3 指南风暴探针（37b074a）**：guide_storm_probe.json（normal_hud→默认落每日实训→切第三页签 605,229→进货币战争→进局 11 帧全链）+GuideStormProbeTests 2/2 绿；帧级守卫证实 2560×1440 风暴实况帧被正确分类（guide_daily_training/guide_currency_wars）=bc56a6e 修复在实况分辨率下有效。
    - **T4 收尾**：沙箱套件 34/34 绿+Grail 145/145 绿+**全量测试 1191 过/1 失败/22 跳过**（唯一失败=CompositeAnalyzerKeepsOverlayedHomePageOutOfBattlePipeline，既有潜伏失败允许存在——本班零生产代码改动非回归；交接提示词明示口径）；沙箱使用说明第七节新增（SANDBOX_PHASE1_USAGE_20260908.md：DECIDE 全链/分析页对齐定案/坑 59 剧本期望纪律/testhost 残留锁处置）。
    - **运维注意**：decide_chain_e2e.ps1 结尾不杀实例——E2E 后实例残留提权进程（CWTLaunchApp 子进程非提权杀不掉，PID 1532 实锤 Stop-Process 拒绝访问），正确收尾=写 `D:\CW-sandbox-iter\指令测试-exit.txt`（沙箱 BaseDirectory，与生产稳定目录隔离）请求自退，实测 15 秒内退出；**绝不碰生产实例的 exit.txt**。另：dotnet test 异常中断会残留 testhost 锁 dll（MSB3027），先 `Get-Process testhost | Stop-Process -Force`。

## 五、Windows 操作强制标准

见 **docs/WINDOWS_OPS_STANDARD.md**（启动/停止/部署/进程诊断决策树/睡眠防护/转义编码/第七节 AI 常见错误网络调研对照表/禁止事项 10 条）——**全部来自今日真实案例+官方文档溯源**。要点：启动只能走计划任务（直接 start 触发 UAC 无人确认）；提权进程无法被普通权限 AI 杀/dump/UIA（连续失败立即升级用户）；部署=build→停旧验退→robocopy→**验 App.dll ProductVersion**；挂机前 powercfg 四项禁睡眠（standby/hibernate/**unattendsleep**/monitor）。

## 归档指引

- 1.2.101~1.2.119 逐版本细节：git log --oneline -40 每条 commit 均有完整描述
- HANDOFF 精简前全文（含 1.2.101~11x 历史条目）：git show 37beb8a:HANDOFF_SESSION_20260902.md 与 git show 9fa6c5f:HANDOFF_SESSION_20260902.md
- 全量逐局审计：docs/RE_AUDIT_20260907.md；通宵审计：docs/OVERNIGHT_AUDIT_20260907.md；修复方案：docs/FIX_PLAN_1.2.119.md

## 九、下一班交接提示词（复制即用）

你是"货币战争智能狸"项目（崩坏：星穹铁道货币战争自动化辅助，WPF/.NET 8，截图识别+模拟输入）的值守 AI。
工作目录 C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826。

【必读】
1. 本文件（〇-1 当前状态/〇-3 挂账/〇-4 验收观察点/〇-5 关键路径）；
2. rule.md 全文（第四节机制口径/第九节录屏逐帧审计规程/坑账本 1~62）；
3. docs/AUDIT_20260910_121_SESSION.md（1.2.123 首测审计：已知问题与根因）；
4. docs/WINDOWS_OPS_STANDARD.md（启动/停止/部署/进程诊断）；
5. 记忆文档自动加载，重点：currency-wars-project-state / frame-by-frame-audit-procedure / no-sleep-idling-in-watch-shifts。

【环境现状（09-10 05:1x 更新）】
**用户令（04:5x 变更）：修"关游戏→UI 未响应卡死"（观察 12，OS 级 AppHangB1 实锤，存量缺陷非回归）→修好+审查后再发布→持续实际验收；期间不开始比赛（用户曾要用电脑）**。1.2.124+e3f2316 文件已部署稳定目录但实例未运行（用户关掉了卡死的）。三修复（46e1250/93d3277/2f27915）已 commit 全审毕。D 盘余约 135GB。

【任务按序】
1. 完成卡死根因修复（子代理诊断结论见〇-7.12 与对话）→对抗审查→发布 1.2.124 修复版→**持续实际验收**（用户令"不允许停"）：DECIDE 武装+〇-4 观察点逐项核对+rule 第九节逐帧复盘；
2. 验收后处理挂账（P2 购买落点待用户口径/语料库扩充/分析页对齐沙箱）；
3. 用户令变更时：每回合修改当场写 handoff+commit。

【纪律】
值守禁 sleep 空转（监视脚本已带 sleep 进程检测会抓）；每回合修改当场写 handoff+commit；同类错误两次=停工报告；未授权不发布不实测；识别失败先查帧质再疑软件；取证必须核对键名（camelCase）/日期/时间基。

27b. **值守结束（09-11 09:08，用户令"值守结束+把脚本关了"——用户对等待类轮询烧 token 怒斥）**：watchdog daemon 经 stop 文件退出（24876），stop 残留已删、state 已拨 active:false；monitor_cycle 21264、watch_duty 24760 已杀；**监督脚本已关闭**。软件 19048 与游戏 12404 已不在进程表（非我方关闭——我仅写 abort.txt 中断指令，疑似用户手动关闭，待确认）。本班最终账：DECIDE 04:41 首发→Fix A 修复→1.2.128 发布 VERIFY-PASS→05:57 重发；验收 4 小时：命中 13/R3 判死 11/11/徽章本会话 4/4/零红旗；待用户拍板：徽章 Y684 补偿、账本卖款入账、冻结管线修复三专项。全程详见 docs/AUDIT_20260911_ACCEPTANCE_127.md。

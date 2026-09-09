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
2. **P0 金币读数幻值**（时间戳已诚实但数值仍错：真 9 报 27、真 3 报 1、真 4 报 1）：识别 OCR 或滞后帧值问题——需识别回归语料库（下条）定量化。防御已闭环（R3 判死需新鲜读数+塌缩跳过），实害=运营低效非误弃。
3. **P1 分析页对齐（重开调查）**：实机识别管线健康（75/86 分析 PageId 正常），**前班"沙箱 PageId 永非 preparation_*"结论系探针键名大小写错误假象**（JSON 为 camelCase：snapshot/pageId）——沙箱会话 0 个 analysis JSON 落盘才是真差异（沙箱模式管线不写产物或未运行到产出）。重开步骤：起长会话沙箱验证管线是否运行→按结果修。
4. **P1 主界面 transition 误标（间歇）**：3D 主界面高运动时段全帧被判 transition_animation→M8 等不到稳定页（01:19 实锤，01:24 自愈）。修法候选=fast 分类器命中已知页时豁免 transition 标记（fast 集已含 currency_wars_home）。
5. **P2 识别回归语料库**：ShopRecognized 时存货架裁剪+识别名单（有界环），OCR/识别改动有真帧对照。
6. **P2 三星五费黄金剧本**：分析页对齐修完后，用真帧（奇迹代偿/昔涟货架/聘用书帧已有）编全流程沙箱剧本。
7. **待查**：18:14:28 金=13 卖卡是否违反凑息规则（需 UI 日志）；84s 空窗前段 t≈10850-11005 前扩抽帧；四.13a 模式盲区（对弈/单人无法从证据确证）。
8. **小项**：15s 常量与 StaleAfter 共享；IsComplete 标注测试专用；[X,X,miss,Y] 门级用例；RotateAsync 单调用者约束注释；FinishAsync 空收尾日志措辞。

## 〇-4、实机验收观察点（下轮挂机）
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

1. **P0#1 金尽空转修复（PreparationFormation.cs CaptureVerifiedPreparationAsync）**：页面门内新增 reward_shop 收摊救援分支——页面分类=reward_shop 时（收摊尝试上限 2，独立于 Esc 预算 3 与 8 次总循环）发布 PreparationRewardShopCloseAttempt，调同类现成 GrailClickReferencePointAsync 点收摊开关 (1620,975)@1920（页面身份验证后才点，1.2.64 决策层救援同款坐标），点击后 250ms×20 步进轮询确认页面已离开 reward_shop 才交回门禁（未确认发 PreparationRewardShopCloseUnconfirmed 留痕）。**对抗审查 APPROVED；P2 当场修**：初版固定睡 1500ms 有"双向开关二次点击重开店"风险（审查员指出），改步进轮询后消除；两 P3（收摊后 Esc 分支理论暴露/重复前台守卫）记录不修。调用点影响面：审查员逐一核对 21 处调用，确认不存在"商店页合法开启时调用本门"的场景（M5 两条路径上场动作均在 CloseShopAsync 之后）。验证：构建 0 警 0 错 + Grail 145/145 + Preparation 95 过/4 跳既有 Skip。**未发布未实测，等用户令**。
2. **值守环境整理**：monitor_cycle.py 曾有 4 个重复实例（共享 state.json 竞态），清到 1 个（PID 56144）；watchdog daemon 双实例清到 1 个（keeper 拉起的 32172）；watch_duty_123.ps1 沿用上班的（PID 53840）。watchdog keeper+daemon 在岗（keeper 日志显示 daemon 高 I/O 下反复卡死被 keeper 正常复活，属设计内行为）。
3. **1.2.123 运行观察（01:18-03:16）**：刷局循环健康（22+ 轮弃局全闭环、每轮 40-90s、卡壳自愈）；P0 两条挂账实机复现取证（金尽空转 12min+10min 各一段、02:07-02:12 出现 PurchaseNotConfirmed 循环 11 条——金 9-10 反复购买未生效，与金币幻值同族）；02:47 识别流看门狗挂起报错→恢复链闭环（RecoveryCompleted 02:52:51）。

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

【环境现状（09-10 深夜交接）】
1.2.123+7836ae3 已发布运行，autodecide 已武装（游戏窗口出现即自动 DECIDE 刷局）。三通道证据持续落 D 盘（日志 jsonl/runs 分析/每局录像）。D 盘余 137GB。

【任务按序】
1. 值守监测：运行 artifacts\watch_duty_123.ps1（60s 快照+sleep 检测）+ 定期跑 D:\CurrencyWarsData\audit-123-watch\monitor_cycle.py 读增量异常；
2. 处理 〇-3 挂账清单（按优先级）；
3. 修复需走完整流程：根因定位→修→构建 0/0+涉事测试→子代理对抗审查→commit→用户授权后发布；
4. 会话结束/用户停 → 按 rule.md 第九节做录屏逐帧复盘。

【纪律】
值守禁 sleep 空转（监视脚本已带 sleep 进程检测会抓）；每回合修改当场写 handoff+commit；同类错误两次=停工报告；未授权不发布不实测；识别失败先查帧质再疑软件；取证必须核对键名（camelCase）/日期/时间基。

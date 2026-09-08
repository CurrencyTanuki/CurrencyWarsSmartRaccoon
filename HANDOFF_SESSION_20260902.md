# HANDOFF——货币战争智能狸（2026-09-07 深夜交接版，历史细节见 git log 与文末归档指引）

> 新 AI 必读四件套：**本文件** + **rule.md**（规则与坑账本 1~52，含铁律 1~8 + 第八节监督纪律） + **docs/WINDOWS_OPS_STANDARD.md**（Windows 操作强制标准） + **记忆文档**（自动加载，重点 3 个：windows-ops-standard / currency-wars-project-state / supervision-invariant-discipline）。
> 决策树：docs/DECISION_TREE_1-3三星五费_定稿版_20260829.mmd；指令集：docs/GRAIL_COMMAND_SET_v43_final.md。
> **⚠️ 本文件已精简重写（2026-09-07 深夜）：1.2.101~1.2.119 逐版本细节压缩进"二、今日总账"，更早历史见文末归档指引。**

---

## 〇-2、09-08 通宵班归档（值守成果全清单,交接基准）

- **软件最终态**：修复版已部署上线并验证（DEPLOY-OK+VERIFY-PASS,版本串 1.2.119+9afd5a6=audit 提交,二进制含 5414170 全部修复——构建时序瑕疵致 stamp 指向 audit 提交,内容已经 162 套件验证）。**DECIDE 已停（用户手停 12:11）,软件运行中（指令通道/识别流健康）,游戏停在货币战争主界面**。恢复测试=发 DECIDE 或写 autodecide 后重启。
- **代码修复全清单（全部已部署）**：①DI 解析死锁（1f61037 Func 单例工厂重入环）→逐接口显式工厂（ef33819,双核验含内存转储）；②识别流"心跳续传但分析冻结"→复活判活补分析年龄判据+重启 90s 豁免（e714d08+4fc13cd）；③**系统级 P1 交换缺陷**（停滞→快照缺失→M5 购买部署默认拖前台 1 号位占用槽→交换→星徽携带者/成员下板凳永不归位,23/33 局中招）→四连修（5414170:占位双读诚实跳过/部署后板凳复核检出交换+被换下成员重部署/M5 收摊后携带者账本扫描重部署(昔涟除外)/S2+S3 M1 前停滞等待 60s——复核 APPROVED）；④autodecide 等窗循环+派发原子复查（d19e34a/5414170）；⑤deploy 脚本原生库 runtimes 布局兼容（b02c5b0）；⑥关店点复核注（坐标正确勿校准）。
- **审计成果（docs/OVERNIGHT_AUDIT_20260908.md 全文=下一班必读）**：〇-d 值守元审计（时间线/数据通道矩阵/三失职）；〇-c C 盘满事故闭环（归档 7.5GB+截图录像→凌晨满→截图写入失败=分析管线卡死=识别流停滞真根因候选——**C 盘已腾空 25GB,验证实验待做：重启 DECIDE 跑一局若停滞消失=坐实**）；〇-b 预热审计（弃局首 Esc 冗余 40/44、holdMs=0 32/35、重试成功×2、首帧 OCR）；一/二/三 逐局+缺陷+计数器；四 深挖清单 6 项；五 全量 32 局 Ledger（命中 31/环境分布/M7 策略 30 种/结局分布）。
- **工具与数据**：全夜不变量扫描器 artifacts/sweep_invariants_20260908.py（**被 .gitignore 忽略未入库,需 git add -f 强制入库**——离线跑法:python 直跑,输出 audit-prep/ 三文件；改造为每局边界自动跑的看门狗=制度待办）。视频证据 2 帧+录像抽帧素材已随 12GB 搬迁至 **D:\CurrencyWarsData\sandbox-frames\recording-audit\**（OVERNIGHT_AUDIT 内旧路径 %LOCALAPPDATA%\...\sandbox-frames 已失效,读帧时用 D 盘新路径）。录像两段 3.2GB 亦在 D:\CurrencyWarsData\Recordings\。
- **环境终态**：软件运行中（PID 2540,DECIDE 停止）；游戏运行中（主界面）；C 盘剩余 25GB（凌晨曾满=通宵停滞/任务异常停止/jsonl 0 字节的统一根因,详见 〇-c）；**磁盘水位每小时检查=新值守制度**（记忆 29 条）。
- **下一班主任务（用户令）**：**开发帧沙箱 Phase 1 骨架**（用户原话"下一步任务就是开发之前的那一个沙箱"）——按 docs/SANDBOX_FEASIBILITY_20260908.md 设计执行：--frame-sandbox 参数+FileSequenceGameCapture/RecordingInputController/StubWindowService/AlwaysForegroundGuard 四实现+脚本加载/裁判/违规输出+PageReplay 夹具冒烟脚本。待用户拍板：五费聘用书帧来源（a 抽帧/b 合成/c 等真帧）。

### 09-08 帧沙箱班增量（本次交接最新状态）

- **🔴 1.2.120 已发布上线（用户令"发布你最新的修改版本,我放到游戏里去测试"）**：DEPLOY-OK+VERIFY-PASS（14:04,版本串 1.2.120+043b3c0=帧沙箱 Phase 1 内容, Directory.Build.props 同步升版）,新实例 PID 54656 运行中（指令通道健康,DECIDE 未发）。**用户将实机测试 1.2.120**。
- **🔴 昨晚记录数据已删（用户令,前提核实后执行）**：前提=素材审查完成（OVERNIGHT_AUDIT 全量定稿）+代码修改完成（通宵修复全提交+部署）,均核实成立。删除共 **5.01GB**：Recordings 两段 decide 录像 3.28GB+GrailRecordingTemp 1.1GB+runs 通宵局 91 目录 281MB+badge/battle/mine/deploy-evidence 300MB+abandon/guard-evidence 旧文件 107MB+logs 审计定稿前旧日志 52MB。
- **🔴 待办（用户令）：用户实机测试完成后,删除 D:\CurrencyWarsData\sandbox-frames 全部保留文件**（recording-audit 4 文件+video1-1728+video2-1920+video2-focus114-135=五费聘用书页素材在 video2-focus 里）——**用户测试完成一说一声就删,别再问**;用户存储空间紧张。另 runs-archive-20260907（09-07 历史归档,非昨晚数据,本次未删）是否同删等用户届时指示。
- **帧沙箱 Phase 1 骨架已交付（用户令"开发之前的那一个沙箱"）**：`--frame-sandbox <脚本.json>` 启动分支（与 --command-test 同族）+DI 层换 4 个基础设施实现（Vision: FileSequenceGameCapture 按裁判当前步回 PNG 帧/StubWindowService 假窗口 1920×1080 前台恒真；Automation: RecordingInputController 全操作记录交裁判恒回 Success/AlwaysForegroundGuard 恒放行）+裁判=驱动器 FrameSandboxPlayer（App/FrameSandbox/：期望全满足才切帧/脚本外操作=违规/步超时=FAILED/终局帧到达=PASS；重复操作容差=匹配既往期望但上限 200 破线记违规；产物 sandbox-ops.jsonl/sandbox-violations.jsonl/sandbox-verdict.txt 带 FileShare.ReadWrite 可边跑边读，默认落 %LOCALAPPDATA%\CurrencyWarsSmartRaccoon\sandbox\run-*）。**决策/操作/识别代码零改动**；单实例互斥对沙箱旁路（必须从非稳定目录启动=bin\Debug 天然隔离命令通道）。使用说明+整改记录：docs/SANDBOX_PHASE1_USAGE_20260908.md。
- **冒烟脚本**：tests/CurrencyWarsAssistant.Tests/Fixtures/FrameSandbox/smoke_home_to_preparation.json——11 步主界面→命中 019→1-1 备战停；帧=PageReplay 1920×1080 夹具 10 张+真实命中环境页帧（视频 2《A850 三星昔涟》第 1 秒，**019 命运圣杯邀请在 slot1**→软件应点 (960,530) 选中+(1083,984) 确认，双验证=分类器 96.9%+OcrOpeningPageReader 三槽 100%）；provenance.txt 记录来源与教训。
- **测试**：FrameSandbox 30/30（脚本加载负例/裁判语义/四件套/冒烟回放 PASS+偏离判违规/帧级分类器守卫每帧过真分类器）+Grail 短套件 120/120+DI 守卫 7/7+启动识别面 158 过 3 既有 Skip。构建 0 警告 0 错误。
- **对抗审查闭环（两轮）**：首轮 FAIL（1×P1：命中帧误用备战页截图——"分析 JSON 提到 067"≠"截图是环境页时刻"；3×P2+6×P3）→全部整改（换真帧 019+坐标 960,530+悬空参数报错+重复上限 200+帧级守卫测试+jsonl 中文直读+Dispose 防截断+参数冲突拒绝+单终局步拒绝）→**复核 APPROVED（零 P1/P2 遗留）**。复核遗留 4 条 P3 备案：①帧级守卫只验页面分类未验 OCR 槽位（Phase 2 补槽位断言）；②新帧含水印/字幕（管线验证无害，Phase 2 黄金样本换干净帧）；③**既有死代码备案：CurrencyWarsNavigation.cs:647-657 投资环境快速复点分支条件恒假（id 已被 :1339-1354 重写），"0.05s 复点"加速意图从未生效，Phase 2 修导航时一并处理**；④Dispose 后 ops 静默丢弃（P3-2 合理代价）。
- **沙箱 E2E 实跑未做（须用户当场授权启动实例）**：跑法=从 bin\Debug\net8.0-windows10.0.19041.0 启动 `--frame-sandbox <脚本>` → 指令文件写 START → 写 DECIDE 开始 → 读 sandbox-verdict.txt。exe 需管理员（计划任务族或用户在场 UAC）。
- **挂账**：①Phase 2 常规全路径脚本（runs screenshots 拼帧，人工拼帧为主）；②Phase 3 五费聘用书试炼段——**帧来源三选项仍待用户拍板**（可行性案 §三）；③全帧级端到端（真导航器+真识别流跑冒烟脚本）未做——现回放测试为操作流级+帧级分类守卫两层，识别→决策贯通要靠 E2E 实跑；④录像命名陈旧 temp 段名（1.2.113 风险族）让"按文件名推录像时间窗"不可靠（本次考古踩过：`_20260907_201116` 后缀非真实起始）。

### 09-08 上半夜增量（历史记录,最新状态见上方帧沙箱班增量）

- **🔴 启动静默已定性=启动模式错误，非代码回归**：09-07 14:33~18:15 全部 7 个"静默"实例（14:33/14:47/14:51/18:03/18:08/18:10/18:15）jsonl 首事件=OpeningFilterSelectionsLoaded=**MainViewModel 专属签名=普通模式（无 --command-test）启动**；普通模式天然不消费指令文件+提权杀不掉+占单实例锁令后续计划任务实例 12 秒自退（jsonl 0 字节）。command-test 签名=首事件 Phase2RecognitionWarmUpCompleted（22:22 健康局与 23:31 实例均如此）。**上一班的 DI 二分撤回前提被推翻，已恢复被撤回的两注册（closeShop Func+IWishTrialPopupHandler）**。
- **autodecide 挂机补全（09-08 代码）**：23:31 取证证明原实现无窗口时 DECIDE 直接失败返回，"开机开游戏自动继续刷局"不成立——已实现等窗循环（20s 探测，窗口就绪经 UI 线程自动 DECIDE；决策层结束后 10s 重新武装；DECIDE 停止/关窗解除武装）。构建 0/0+涉事测试 161/161。
- **未解尾巴（不阻塞部署）**：①谁/为何以普通模式启动 7 例（嫌疑=09-07 下午回退实验期直接 start exe/双击，UAC 由在场用户点过）；②23:31 实例（1.2.117）23:37 交接后无崩溃记录干净消失（疑外部结束或另一次启动的 exit 握手）；③写 autodecide.txt 与启动存在竞态（Loaded 先过则开关漏检，23:31 实锤：START 被定时器消费而 DECIDE 未发）——**每次启动后必须核对：jsonl 首事件签名+autodecide.txt 是否已被删**。
- **帧沙箱可行性案已出（用户 09-08 令）**：docs/SANDBOX_FEASIBILITY_20260908.md——动机=五费聘用书试炼永远等不到真帧；方案=DI 层换 4 个基础设施实现（FileSequenceGameCapture/RecordingInputController/StubWindowService/AlwaysForegroundGuard），决策/操作/识别零改动；脚本=每帧+允许操作集，期望操作满足才切帧、偏离=违规（驱动器+裁判合一）。接缝与素材已勘察：PageReplay 107 张+runs screenshots 316 张+归档 1759 目录；**缺口=五费聘用书试炼页帧（素材来源 a 抽帧/b 合成/c 等真帧，待用户拍板）**。Phase 1 骨架待开工（批次二审查与部署优先）。
- **帧素材审帧完成（用户 09-08 令）**：docs/SANDBOX_FRAMES_AUDIT_20260908.md——视频本就在桌面（无需回 B 站）；**视频 2（A850 三星昔涟）=1920×1080 原生主素材源，拿到「5费聘用书·请选择1个」选择页全帧（第 119 秒）等核心画面**；视频 1（全网首发教程）=1728×1080 画幅不匹配仅作参考；叠加物（水印/中央字幕/后期大数字）全部可用选帧规避，不构成阻断；视频 1 解说=纯脑测未验证不采信，只采信画面。帧在 %LOCALAPPDATA%\CurrencyWarsSmartRaccoon\sandbox-frames\（不入 git）。
- **批次二对抗审查完成（09-08）= APPROVED after fixes（2×P1+4×P2+8×P3）**：P1-1 弹框守卫从未接线（死代码）/P1-2 四.13a 数据源在 SoftFallback 路径断裂+事件未落 jsonl；P2-1 13a 双重 A9+标签污染/P2-2 Dead 出口混标 R3/P2-3 买经验缺 1-3 门/P2-4 血量缓存跨局陈旧/P2-5 autodecide 三处竞态。**修复已全部落地（562cb98）**：modalGuard 接线+IModalGuard 注册、SoftFallback 带 strategyId、StrategyAbandonDecided/BuyXpForDeploy 经 publishEvent 落 jsonl（引擎新增可选参数）、13a 单点 A9、四 Dead 出口标签真实化、买经验 1-3 节点门、血量缓存随局复位、autodecide 派发时刻原子复查+失败回炉+循环兜底、wish 应答实例信号量互斥（P3-1 同批）。build 0/0+161/161。**修复复核已发回原审查员（后台），PASS 即部署**。
- **挂账新增**：①引擎级 13a/买经验判定单测（SendAsync 私有不可注桩，重构引擎管道超出本批）=**goal=All 启用前硬前置**（当前 Single 下两判定休眠）；②P3 备案项：闩锁布尔语义无法区分人口步进、降级重扫判据 Count<5 与 P2-5 量化口径不一致、弃局链无环境页"过路必选"分支、selectLeftmostStrategyIfUp 无注入点恒 null、证据文件无上限需轮转、XML 注释漂移两处。
- **🔴 启动挂死事故闭环（09-08 凌晨，用户定性"重大事故"）**：①根因=1f61037 的 Func 单例工厂 DI 解析重入死锁（复现测试 519 秒挂死+核验子代理内存转储"无限递归→StackGuard 线程跳转→跨线程锁互等"+仓库外纯 DI 对照，双实锤）；②修复=逐接口显式工厂 BuildOpeningRecovery（复核子代理 APPROVED，消环彻底+关店委托意图保留+双参数误注入消除），提交 ef33819+662e7cb；③**卡死实例 49880 五种方式杀不掉 84 分钟**（提权墙+改任务要密码+UAC 四次未点，教训入 rule 坑55+ops standard 第八节），用户亲手结束后 watcher 自动部署+启动（**全自动恢复流程 02:53 实战验证成功**）；④**当前运行=1.2.119+a2c6556 修复版**（DEPLOY-OK+VERIFY-PASS，02:53:47 起 DECIDE 运行中，M8 在途，识别流健康）。⑤预防：DI 变更子代理审查+守卫测试入套件；启动看门狗（90 秒未完成启动自退+标记文件）=新待办；AI 可写 wrapper 提权任务=可选待办（需用户最后一次 UAC）。⑥预热审计（用户新维度）完成：弃局首 Esc 系统性冗余 40/44、holdMs=0 违背坑44（32/35）、2 次失败重试成功、商店首帧 OCR 不稳——详见 OVERNIGHT_AUDIT 〇-b。
- **🔴 通宵班终态（09-08 09:0x 收班，唯一有效状态）**：①运行史：02:53 watcher 自动部署恢复（1.2.119+a2c6556）→ 通宵挂机至 04:11 用户结束卡死实例后**自动重部署 1.2.119+1baf888 构建（内容含 e714d08+4fc13cd 全部修补,162 套件验证）**→ 挂机至 09:03:49 **用户回机主动停 DECIDE**（无停机门痕迹=人为；游戏停 normal_hud,软件健康 PID 48280）。②累计战绩：命中 31（067×2 拿赠体）/R3 8（全部前置核实,含两段式"确认仍有可卖→再卖"）/快照不可得弃局 11（修补前 2 必死,修补后漏网 3）/冻结自动复活 42（修补前此路死）。③**用户所见"几个问题"=三个已入账模式**：I10 失败刷屏（识别滞后门禁+5 秒重试,机制正确视觉刺眼）；M5 收摊后面板残留（09:03:41 收起点击后 I1 仍=reward_shop 实锤,点击点 (1620,975) 对当前 UI 疑似失效——晨间深挖#2）；对局间停主界面观感。④视频审计首战：298MB 录像抽帧 2 帧——镜流卖出失败实拍=备战席全空（快照模型错误被反证即停拦截 ✓）+弃局中段空棋盘吻合；录像命名引用陈旧 temp 段名（1.2.113 风险族）。⑤**下一班必读 docs/OVERNIGHT_AUDIT_20260908.md 全文**：P3-1 分析管线停滞（顶层,Mode B 真冻结,取证方案已列）/P3-2 面板残留+关店点击点失效/单帧实拍旁路候选/jsonl 0 字节之谜/卖出复核槽位错位×3/Esc 冗余 40/44/holdMs=0 32/35。
- **🔴 系统级 P1 定案+修复（09-08 晨,用户质询后全量扫描发现,提交 5414170+9afd5a6）**：识别流停滞→快照缺失→M5 购买自动上场默认拖前台 1 号位（占用!）→游戏判定交换→**已上场成员（常为星徽携带者）被换下板凳→保护只保不卖、重部署永不发生→23/33 局中招（保护×2~10+上场 0 次指纹）**。冻结局逐拖拽铁证：08:53:30 三月七→前台1、08:53:44 星徽→三月七、08:55:03 远坂凛→前台1(占用!)=交换。修复：①占位实时读失败重试一次仍失败=诚实跳过不盲拖；②部署后板凳复核检出被换下成员→立即重部署真空位（递归一层）；③M5 收摊后星徽携带者账本扫描重部署（昔涟除外）；④S2/S3 M1 前 EnsureStreamReadyForBattleAsync 停滞等待 60s。**复核子代理审查中,过审即重部署**。**晨间深挖清单#2 修正**：ShopTogglePoint (1620,975) 为 1920 参考系×1.333=恰命中收起按钮(2160,1300)——关店坐标无误,"收起后仍 reward_shop"=动画滞后/其他,勿再"校准"。
- **DI 死锁修复已落地并双核验（09-08 凌晨，ef33819）**：核验子代理独立确认定位（内存转储抓到"无限递归→StackGuard 线程跳转→跨线程锁互等"完整机制 + 仓库外纯 DI 对照实验：无 Func 注册 6.5ms / 有则挂死），修法获支持；修复复核子代理（第二轮）已在跑。全量涉事测试 162/162（含新的形状守卫测试，<1ms 完成）。**唯一阻塞=卡死实例 PID 49880 仍在**（提权杀不掉：schtasks 改动作要密码、UAC 弹了一次被取消——违反占屏预告铁律，已向用户预告后将再弹一次）。49880 不死则互斥锁+App.dll 文件锁都不释放，部署与启动全部被堵。【已解决：用户亲手结束 49880,watcher 02:53 自动部署成功】
- **通宵审计准备已就绪**：docs/OVERNIGHT_AUDIT_20260908.md 骨架已建（协议=逐局三问+八不变量+操作质量审计[冗余点击/坐标偏移/降级触发/过慢]，证据确认无异常即删）；证据通道位置已摸清（abandon/guard/deploy-evidence 在 %LOCALAPPDATA%\CurrencyWarsSmartRaccoon\ 下、input-diagnostics 在 logs\、run 目录在 runs\、DECIDE 录像在稳定目录 Recordings\、result 归档在稳定目录）。决策树+机制口径已通读。

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

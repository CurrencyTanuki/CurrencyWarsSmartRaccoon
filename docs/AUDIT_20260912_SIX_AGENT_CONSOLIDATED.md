
# 六代理并行全项目审计合并报告（09-12 凌晨，用户令：6 子代理并行审全项目 bug+跨阶段不兼容）

六份原始报告：~/.zcode/cli/agents/sess_636c02d4/agent_{上述六个 ID}/output.txt（各数千字含行号）。本文件=合并定谳+修复路线。全部结论基于只读审计；P1/P2 级发现我方未逐条人工复现（子代理均给出行号级证据，抽验的两条——Fast 后台部署错位、characters 表加载——已亲自到源码核实为真）。

## 一、合并后缺陷清单（去重后 P1×9 / P2×19 / P3×40+）

### P1（9 项，按危害排序）
| # | 子系统 | 缺陷 | 位置 |
|---|---|---|---|
| 1 | App | 三星五费"成功局"录像必被删：GrailRunLoop 只 Start 不 Rotate/Finish(true)，MainWindow 唯一收尾硬编码 FinishAsync(false) | MainWindow.xaml.cs:516 + GrailRunLoop.cs:70 |
| 2 | Tasks | GrailRunLoop 跨局状态不复位（无 Reset 调用）→第 2 局起祈愿计数/聘用书/购买账全继承→单人模式判 ExhaustedReroll 无限速刷弃局、全员模式可假 Success 停机 | GrailRunLoop.cs:54-151（组合根 MainWindow.xaml.cs:403） |
| 3 | Tasks | 录像轮转 RotateAsync 从未接入主循环（接口停在 Start/Finish 旧时代）→逐局落盘特性整体死代码，damaged/隔离同失效 | GrailRunLoop.cs + MainWindow.xaml.cs 接线 |
| 4 | Tasks | 滚动录像器同步写 ffmpeg stdin+stderr 无人排干+停止无超时→三重楔死（写阻塞/背压/关闭等待）→_gate 永久持有全链冻结 | GrailRollingRecorder.cs:233-281,148-160 |
| 5 | Advisor | 27 册中 20 册 costFocus 为 int 数组而模型是 string?→加载必炸被容错吞→v1 匹配库只剩 6 册且零告警 | GuidePlaybook.cs:235 + AdvisorPipeline.LoadError 无消费 |
| 6 | Advisor | v0 契约加载器（要求 schemaVersion=1.0.0）与 26 个 v1.2 攻略册同目录互斥→SituationScreenshotAnalysis 每帧抛异常被吞→**局势分析主链路断路**（与已知"静帧饥饿"症状吻合，或为其真因/共因） | AdvisorJson.cs:24-41 + SituationScreenshotAnalysis.cs:602 |
| 7 | Tasks | Phase2LiveCollectionService._saveQueue 会话收尾即永久 Complete→同一实例后续所有会话帧证据静默丢失（savedCount 照涨） | Phase2LiveCollectionService.cs:103-109,1362 |
| 8 | Tasks | Fast 模式后台车道部署拖拽落点=备战席第 9 格而非后台槽→后台角色永远不上场，静默降级 | PreparationFormation.cs:1310-1312,1581-1583 |
| 9 | Tasks | 自动战斗 Disabled 只需 2 帧即按 V（Enabled 需 3 帧）→终结技动画 2 帧假 Disabled 可关掉已开的自动战斗 | RewardStageAutomation.Battle.cs:448-454 |

### P2（19 项，摘最要者）
- 费用/专家门控 id 空间错位=死代码（_charCost/_expertIds 全形制键 vs 查找短 id）；
- 阶段权重映射疑似倒挂（Lv≤3 时 Early=0.3<Final=0.5，"邻近衰减"实为固定段名）；
- v1 匹配只回 Top3→其余册 sim 恒 0.5（不匹配反得中间分）；
- MergeCheckpointSnapshot/LocalRunStore.Fingerprint 漏 StoreLevel（断点恢复丢等级）；
- RunContext 缺 Health/population 断链（识别层已产出 Population 观测但桥接缺失）；
- CanStart/CanEdit 缺 IsGrailRunActive（双向互斥单向失效）；
- IsGrailRunActive 无属性通知；
- Advisor VM 订阅 Transient collector 实例（面板"记录中取快照"承诺失效）；
- HasError 绑定不存在（错误面板永不显示）；
- 按钮旁路 _busy 破坏 M8/M1 复活豁免；
- CompletedRuns 日志写无 try/catch（异常可取消运行中自动化）；
- 单步 analysisAsOf 漏传（CommandTestWindow RefreshLatestSnapshot 陈旧帧播种）；
- 聘用书末试成功误报失败+读不出=已选假阳性；
- 双向 Contains 可选"姬子•启行"冒充"姬子"；
- 后台槽第二帧丢 BackRow 选项；
- 跨局陈旧帧消费（预热信号一次性+LatestAnalysis 不清台）；
- 旧循环链金塌缩 0 无新鲜度防线（幻象金尽驱动卖角/GiveUpFiveBond 闩锁）；
- "槽位已空"仍计卖款（估金虚增）。
- 另：识别热路径 3 处 Console.WriteLine（stdout 重定向时可无限阻塞=静帧饥饿第一嫌疑，删除成本≈0——优先处理）。

### 跨阶段不兼容（6 审计员独立发现，交叉印证）
1. **双 GuidePlaybook/双决策栈/双必要性标尺三重并存**（阶段乙基线 vs 阶段丁融入）——MainWindow 生产栈停在 1.2.127 语义（无快速通道/无 R3 事件/无账本判死），DECIDE 栈已到 1.2.131：同一执行器两颗大脑，MainWindow 的"刷三星五费"按钮若被使用将复现已根治的旧病（P2-3）。
2. **1.2.68 时序压缩 × 1.2.123 三帧观测的残余数学**：刷新后 650-1150ms 窗 vs 三帧+单帧容忍——两帧乱码同误可骗过稳定门（低概率非零）；两条刷新路径（400ms vs 零等待）语义分叉。
3. **1.2.130 ignoreBookCounters × F11a 计数门**：单步序列 opened/obtained 不收敛（已修一半，窄窗仍在）。
4. **1.2.124-125 WGC 后台化 × 旧 Dispose 路径**：表型从卡死漂移为泄漏+黄框常驻。
5. **1.2.129-131 增量识别 × Feed"非心跳即新结果"旧语义**：半量增量帧被当新结果分发。

### 正面清单（确认无问题的缝合点）
11 项缝合核对通过：双类型别名隔离/v2 数据根形制修复验证/EnsureLoaded 时序/GralShopPassFact 四消费方/金账三源播种/Xp 闩锁互斥/M4 两语义分道/M8 双超时自洽/RunCheckpoint 契约演化处置/Advisor 基建 7 文件镜像自洽/单实例与 exit-abort 通道闭环。

## 二、修复路线（建议顺序，待用户令）
1. **止血批（半天）**：P1-4 录像器楔死（WriteAsync+排干+超时）、P1-7 _saveQueue 每会话重建、P1-3 Rotate 接入主循环、P1-1 FinishAsync(true)、识别热路径删 3 处 Console.WriteLine——全是一小时内的小改，消除"证据丢失/识别卡死"两大类。
2. **行为批（一天）**：P1-2 跨局 Reset、P1-8 Fast 后台槽、P1-9 自动战斗对称门、门控 id 空间（P1-5/P1-6 同源）、P2 群逐项。
3. **结构批（待拍板）**：双决策栈合并（MainWindow 栈退役或升级到 1.2.131 语义）、双 GuidePlaybook/双必要性标尺统一、Population 桥接、静帧饥饿根治。
4. 每批=构建 0/0+Grail 全绿+新回归测试+对抗审查+部署。

## 三、审计方法沉淀
6 代理并行各管一面（UI/决策核/执行层/新融入库/识别管线/跨阶段专项），任务书统一带：分级口径+已知问题清单（防重复）+覆盖度声明义务。实测有效：6 代理独立发现的交叉印证（Agent 4 P1-3 ≡ Agent 6 P1-1；Agent 3 的跨阶段节与 Agent 6 的参数叠加节互证）比单代理全面审计信息密度高一个量级；误报率也可控（Agent 3 的 P1-1 经本体到源码核实为真；Agent 6 的两处"疑似严重"中一处经基线测试实跑确认为真）。

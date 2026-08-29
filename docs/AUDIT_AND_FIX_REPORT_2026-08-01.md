# 货币战争智能狸 0.2.752–0.2.755 逐项审计报告（2026-08-01）

审计人：Reasonix（deepseek-v4-flash，0.2.756 候选版）
范围：交接包 `CurrencyWarsSmartRaccoon-zero-context-handoff-20260801-030132` 内全部源码。

## 结论总览

- 23 项候选修改中 **21 项实现完整且正确**（含对应测试）；
- **1 项存在真实回归**（0.2.755-22 声明窗口按钮失效），已在 0.2.756 修复；
- **1 项补充防御性修正**（0.2.753-8 完美通关边界）；
- 另外在审计中额外发现并修复 **7 个非清单问题**（含 1 个高危伤害数量级 bug）。

## 逐项审计

### 0.2.752
| # | 项 | 结论 | 依据 |
|---|---|---|---|
| 1 | 删除无效按钮 | ✅ 通过 | MainWindow.xaml 顶部仅剩 开始刷开局/开始记录/停止；全库无 OpenStartPage/RecognizeCurrentScreen 残留；RealtimeRecognitionEntryTests、UiRedesignContractTests 有防回归断言 |
| 2 | 启动耗时边界+虚拟化 | ✅ 通过 | App.xaml.cs:74 shellElapsed 隔离启动窗；MainWindow.xaml:418-428 FilterListStyle Recycling 虚拟化；UiRedesignContractTests 有断言 |
| 3 | 敌人预览异步缓存 | ✅ 通过 | CurrencyWarsNavigation.cs:625-674 识别完成后才缓存；Phase2AdvisorTests 覆盖跨页保留 |
| 4 | 未知身份伤害更新 | ✅ 通过 | PreferRowObservation 大值优先不查身份；UnknownCharacterDamageKeepsStableIdAndFinalValue 测试 |
| 5 | 历史节点封存 | ✅ 通过 | 仅 Finalized 节点入表；不完整节点保留 Unknown（设计如此，Observe_PreservesIncompleteFinalDataAsUnknown 测试） |

### 0.2.753
| # | 项 | 结论 | 依据 |
|---|---|---|---|
| 6 | 详细历史关闭 | ✅ 通过 | TitleDragSurface 独立拖动区 + Esc 兜底；契约测试含防回归断言 |
| 7 | 伪节点过滤 | ✅ 通过 | IsCanonicalNodeId 仅接受 d-n；battle_generic 附加到上一节点（测试覆盖） |
| 8 | 完美通关规则 | ✅ 通过（补防御修正） | +2/未知血>10/==10 不推断均正确；postBattleHealth==100 补充 healthDelta is not < 0 约束（血量 0-100 校验下原场景不可达，防御性） |
| 9 | 伤害数量级保守 | ✅ 通过 | 缺单位不升级（RepeatedSettlementCandidateWithoutDamageUnitStaysUntrusted）；两帧一致+单位明确升级为设计行为；结算页无单位"万"回退为既有特例 |
| 10 | 详情界面样式 | ✅ 通过 | 字体层级/金色青色/细线选中/自定义滚动条齐备；契约测试断言 |

### 0.2.754
| # | 项 | 结论 | 依据 |
|---|---|---|---|
| 11 | 金币 23 识别 | ✅ 通过 | 宽窄区域+后缀优先；真图测试 PreparationEconomyKeepsLeadingDigitAtShopCardBoundary |
| 12 | 结算奖励总览 | ✅ 通过 | 仅语义标签总览行；总览 8 不被明细 2 覆盖测试 |
| 13 | 黄色页快速消失 | ✅ 通过 | 100ms 间隔、战斗→备战强制边界、保留前序帧；单帧黄色页测试 |
| 14 | 节点跳号/总结丢失 | ✅ 通过 | ResolvePendingBattleNode 编号与标签分离；1-9 修正回 1-8 测试 |
| 15 | OCR 队列满落盘 | ✅ 通过 | 失败截图+manifest.jsonl+runId/version/reason（保存路径无直接单测，属集成行为） |
| 16 | 多帧覆盖规则 | ✅ 通过 | 黄色页血量优先锁定、空值低置信不覆盖；多个覆盖测试 |
| 17 | 透明控件抢点击 | ✅ 通过 | OperationPanel 打开详情时隐藏 hit target 并恢复；LogOverlay 拖拽把手仅 34×9px 且随窗口隐藏，次要项不修 |

### 0.2.755
| # | 项 | 结论 | 依据 |
|---|---|---|---|
| 18 | 单实例保护 | ✅ 通过 | Mutex+EventWaitHandle 唤醒后退出；契约测试断言 |
| 19 | 首次点击反馈 | ✅ 通过 | 先置"已接收"再 Yield(Render) 再启动；防重入测试 |
| 20 | 启动并行加载 | ✅ 通过 | 6 个 Task.Run 并行，角色模板依赖游戏数据属合理依赖 |
| 21 | 词条按需加载 | ✅ 通过 | Collapsed+点击加载；契约测试 |
| 22 | 声明窗口非模态 | ❌ **修复** | StartupNoticeWindow 仍设 DialogResult（非模态禁止）→ 按钮失效+异常日志；0.2.756 改 Close() |
| 23 | 悬浮窗低优先级 | ✅ 通过 | DispatcherPriority.Background 创建；契约测试 |

## 额外发现并修复（非清单问题）

1. **高危：千分位逗号被当小数点**（Phase2OperationalScreenshotAnalyzer）——`1,234万` 错 1000 倍。修复 + 测试。
2. 全局热键注册失败静默 → 记录警告。
3. 断点 runId 路径穿越（SanitizeSegment 未滤 `.`/`..`）→ 拒绝。
4. OnContentRendered 异常无兜底 → 捕获记录。
5. 透明度滑块 0.3 与钳制 0.25 不一致 → 统一 0.25。
6. 日志悬浮窗 Timer 空转 → 随可见性启停。
7. 游戏区域归一化除零 → 尺寸 >0 保护。

## 记录为已知（不修改）的项

- 0.2.754-15：manifest 保存无直接单测（集成行为，风险低）。
- 0.2.755-19/23 一致性：自动刷开局入口（AssistanceActivated）同步创建悬浮窗，未走 Background 优先级；首帧已 Render，影响小。
- 潜在死锁风险（Dispatcher.Invoke × Task.Result）：未证实、无用户反馈，改动事件顺序语义风险高，留待实机验证。
- UI/美术：未发现 XAML 断裂（StaticResource/Binding 全部验证通过）；主观审美改动无法实机预览，不做无验证的视觉重设计。

## 测试与交付

- 全量回归：557/557 通过，0 失败 0 跳过（2026-08-01，.NET 8.0.423）。
- 候选包：`artifacts/CurrencyWarsSmartRaccoon-0.2.756-win-x64-portable.zip`（附 SHA-256）。
- 回退基线：`release-baseline/CurrencyWarsSmartRaccoon-0.2.751-win-x64-portable.zip` 原样保留。
- 未验证：实机对局（本机无游戏）。

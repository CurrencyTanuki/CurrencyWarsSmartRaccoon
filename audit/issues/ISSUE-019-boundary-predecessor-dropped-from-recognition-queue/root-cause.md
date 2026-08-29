# ISSUE-019 根因与最小修复边界

## 单一根因

`Phase2RealtimeFrameSelector` 与 `Phase2BoundedRecognitionQueue.DropStaleFrames()` 对“最新关键帧”的语义不一致：

- selector 在页面切换时故意选出最多三个关键前驱，再追加当前边界帧；这些项都可能是 critical。
- stale 清理的业务目标是最多保留两项：最新帧，以及严格位于它之前的最近 critical。
- 旧实现却在全队列中取最后一个 critical。当队尾最新帧本身就是 critical 时，两种角色落在同一个节点上，旧循环会删除此前全部 critical 前驱。

这使 selector 为一闪而过的最后战斗帧/结算前驱所做的保护，在识别负载超过 1.5 秒并触发 stale 清理时失效。

该控制流缺陷在生产上确定可达，并能造成真实用户所见的字段缺失；但 2026-08-09 的具体 1-1/1-2 会话没有保存实际被删除的序号与队列轨迹。因此对那两场的实际因果结论仍是“高度吻合、待修后真实复跑确认”，不是已被日志直接证明。

## 正确合同

当队列超过两项、确实需要执行 stale 裁剪时，保留队尾最新帧；再保留严格位于队尾之前的最近 critical（若存在），裁剪后总数最多两项：

- 队尾为 regular：最近 critical + 最新 regular；
- 队尾为 critical：倒数第二个 critical + 最新 critical；
- 队尾之前没有 critical：仅最新帧。

既有的 `items.Count <= 2` 快速返回保持不变；本项不把一个本来就不超过上限的两项队列进一步压成一项。

合法队列可能是 `[C0,C1,R2,C3]`。因此不能只删除保留前驱之前的前缀，否则会错误留下 `[C1,R2,C3]` 三项。

## 最小生产修改

仅修改 `Phase2BoundedRecognitionQueue.DropStaleFrames()`：

1. 以链表队尾节点作为 `latest`；
2. 从 `latest.Previous` 向前查找第一个 critical 节点；
3. 遍历完整链表，只保留这两个节点引用；
4. 删除每个其他节点时恰好执行一次 `available.Wait(0)`，保持链表项数与 semaphore permit 一致。

不修改 selector、Enqueue、队列容量、1.5 秒阈值、识别器、tracker 或状态模型。

## 独立测试边界

- all-critical：`[C0,C1,C2] -> [C1,C2]`；
- mixed：`[C0,C1,R2,C3] -> [C1,C3]`；
- selector-to-queue：真实 selector 构造的未分类结算前驱在 preparation 边界 stale 清理后仍先于当前 preparation 出队；
- 每条测试继续尝试第三次 Dequeue，确认没有残留项或幽灵 semaphore permit；
- 关联测试覆盖尾部 regular、关键帧溢出、页面前驱、实时捕获与终局保护。

## 明确不合并的问题

- 当前正式 WebView2 renderer 不展示 partial damage；
- node-final 引用的 battle evidence PNG 没有实际落盘；
- successor preparation 的低置信度血量可能在第二次确认前就被 finalize；
- 当前 run 的行动值根因因证据帧缺失尚未判定；
- compact 历史只显示可靠最终值，未显示“已记录但残缺”的安全摘要；
- 详细历史仍可能暴露内部 ID。

这些问题必须后续逐项复现、修复和验收，不能由本队列补丁外推为节点历史已完全恢复。

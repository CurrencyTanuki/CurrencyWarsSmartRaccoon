# 0.2.794 更新说明（2026-08-03 晚）

## 回滚：逻辑判定改动全部撤回，回到 0.2.789 稳定逻辑（只保留延时更改）

用户实测 0.2.790~0.2.793 后明确要求：**逻辑判定部分没有任何提速，
反而修出一堆 bug，退回开始修复前的稳定版本，只保留延时更改。**

### 本次回滚的改动（全部恢复 0.2.789 原逻辑）
1. **快速点击路径**（0.2.790 引入）：删除 FastPathFromHome 选项、
   FastPathFromHomeAsync / FastWaitForPageAsync / 单帧确认分支、
   Coordinator 传参——导航恢复逐步稳定识别（每步 2 帧确认）。
2. **节点识别正则**（0.2.791）：NodePattern 恢复 node 0~9、
   ReadLocalizedNodeDigits 恢复 stage 0~9。
3. **页面切换消息**（0.2.791/0.2.792）：PageTransitionMessage 恢复
   用 frame.PageFamily、恢复原文案（"进入备战节点 未知节点"等原样）。
4. **PageId 回填**（0.2.792）：SituationScreenshotAnalysis 恢复
   PageFamily.ToString() 原逻辑。
5. **自动战斗检测**（0.2.793）：删除 TryCaptureAsync / windowService
   依赖，恢复 CaptureForegroundAsync（失焦等待前台）与 V 发送原样。

### 保留的延时更改（0.2.790 引入，用户允许保留）
- 部署/出售/Esc 后 AfterActionDelay 500ms → 350ms（3 处，有验证兜底）
- 其余 6 处动画稳定等待（1400/900/500/200/300/300ms）在 0.2.792 已
  回退到 0.2.789 原值，本次维持不变。

### 测试
- 全量回归 **647/647 通过**——与 0.2.789 交付时完全一致。
- 已删除为回滚功能编写的测试（FastPathTests、NodeStabilityTests）。

### 需实机验证
- 逻辑行为应与 0.2.789 一致（稳定版本）；延时 350ms 保留应无副作用。

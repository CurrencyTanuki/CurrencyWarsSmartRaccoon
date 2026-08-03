# “币站开局小助手”开发规划

当前阶段项目名称为“币站开局小助手”。当项目完成正式对局的局势识别、评估与个性化建议等高级功能后，更名为“货币战争智能狸”。

## 1. 产品定位与边界

本工具首先解决“刷开局”：自动观察开局信息，判断是否满足用户设定，满足则停下并提示，不满足则按安全流程重新开始。

后续逐步扩展为：

1. 攻略与资料集合；
2. 当前局势快速评估；
3. 下一步操作建议；
4. 经用户确认后执行建议；
5. 在规则明确、识别置信度足够高的场景中自动执行。

技术边界：

- 只使用屏幕截图、图像/OCR 识别和鼠标模拟；
- 不读取或修改游戏内存，不注入进程，不绕过反作弊；
- 默认运行于 Windows PC 客户端，建议窗口化或无边框模式；
- 所有点击必须经过界面状态确认，禁止依赖固定延时连续盲点；
- 识别不确定、界面未知或窗口失焦时立即暂停，不猜测点击。

## 2. 总体架构

将系统拆成五层，第一版也沿用完整分层，避免形成一次性脚本。

### 2.1 感知层 Perception

职责：

- 定位游戏窗口及客户区；
- 截取整窗或局部 ROI；
- 识别当前页面；
- 识别文字、图标、按钮、卡片、数值及状态；
- 输出带置信度和证据位置的结构化观测结果。

建议实现：

- 桌面端：C# 12、.NET 8、WPF 与 MVVM；
- 截屏：Windows Graphics Capture，BitBlt 作为兼容方案；
- 图像处理：OpenCvSharp；
- 中文 OCR：PaddleOCR 的 ONNX 推理实现，必要时针对游戏字体建立轻量分类器；
- UI 元素：优先模板匹配，复杂对象再采用 ONNX 检测模型；
- 坐标：以 1920×1080 标准空间保存，再映射到实际游戏客户区；
- 运行时：.NET Generic Host、依赖注入、取消令牌和显式任务状态机。

统一输出示例：

```json
{
  "screen": "opening_enemy_modifier",
  "confidence": 0.96,
  "observations": [
    {
      "type": "enemy_modifier",
      "id": "modifier_xxx",
      "text": "识别到的词条",
      "bbox": [0.31, 0.22, 0.68, 0.29],
      "confidence": 0.93
    }
  ],
  "captured_at": 0
}
```

### 2.2 状态层 State

把连续截图整合成稳定的游戏状态，隔离 OCR 抖动和动画干扰。

核心对象：

- `ScreenState`：当前处于哪个页面；
- `OpeningState`：投资策略、敌人词条、其他开局变量；
- `RunState`：金币、等级、回合、生命/容错、商店、阵容、事件等；
- `Uncertainty`：缺失字段、冲突识别、低置信度项目；
- `History`：最近若干次观测与已执行动作。

状态更新采用“多帧一致”原则：重要文字或按钮至少在连续帧中稳定出现后才提交。

### 2.3 决策层 Decision

第一阶段使用可解释规则，不上复杂模型。

刷开局的决策输出只有：

- `KEEP`：满足条件，停止自动操作并提醒用户；
- `REROLL`：明确不满足条件，进入重开流程；
- `WAIT`：动画、加载或信息尚未稳定；
- `NEED_REVIEW`：无法可靠识别，需要用户确认；
- `RECOVER`：页面偏离预期，尝试回到已知状态；
- `STOP`：达到次数/时间限制或发生风险事件。

后续在同一接口下增加：

- 规则评分器；
- 局势特征提取器；
- 候选行动生成器；
- 行动收益/风险评估器；
- 攻略检索与解释模块。

### 2.4 执行层 Action

职责：

- 根据语义目标查找当前按钮位置；
- 将窗口相对坐标转换为屏幕坐标；
- 执行单击、移动和必要的滚动；
- 点击后等待预期页面变化并验证结果。

每个动作采用“前置条件—执行—后置验证”结构：

```text
确认当前是重开确认页
→ 定位确认按钮并检查置信度
→ 单击一次
→ 等待页面切换
→ 确认进入加载页或新一局
```

不得将“重开一局”实现成一串固定坐标和固定延时。

### 2.5 应用层 App

第一版界面建议保持简单：

- 启动/暂停/紧急停止；
- 选择游戏窗口；
- 开局目标条件；
- 最大重刷次数和最长运行时间；
- 当前识别结果、置信度和决策理由；
- 重刷计数、命中记录；
- “仅观察”“自动重刷”两种模式；
- 截图与日志开关。

后续增加攻略页、局势面板、建议列表以及“确认后执行”按钮。

## 3. 第一阶段：刷开局

### 3.1 先定义“开局指纹”

把一次开局抽象为固定结构，而不是写死某几个词条：

```yaml
opening:
  investment_strategy:
    id: strategy_xxx
    confidence: 0.95
  enemy_modifiers:
    - id: modifier_xxx
      confidence: 0.92
  other_factors: {}
```

用户规则示例：

```yaml
rules:
  require_all:
    investment_strategy: [strategy_a, strategy_b]
  reject_any:
    enemy_modifiers: [modifier_bad_1, modifier_bad_2]
  unknown_policy: review
```

如果开局因素之间存在组合价值，则增加加权评分：

```yaml
score:
  accept_at: 80
  weights:
    strategy_a: 60
    modifier_good_1: 30
    modifier_bad_1: -100
```

### 3.2 状态机

```text
IDLE
  → LOCATE_WINDOW
  → DETECT_SCREEN
  → READ_OPENING
  → STABILIZE_RESULT
  → EVALUATE
      ├─ KEEP → NOTIFY_AND_STOP
      ├─ REROLL → OPEN_RESTART → CONFIRM_RESTART → WAIT_LOADING
      │                                      └────────────→ DETECT_SCREEN
      ├─ NEED_REVIEW → PAUSE_FOR_USER
      └─ WAIT/RECOVER → DETECT_SCREEN
```

所有状态设置超时、允许页面集合、最大重试次数和失败去向。

### 3.3 识别策略

按成本由低到高组合使用：

1. 页面锚点识别：固定标题、角标、按钮图案；
2. ROI 裁剪：只在确定区域识别投资策略和敌人词条；
3. 图标模板匹配：对稳定图标建立多分辨率模板；
4. OCR：识别名称或说明文字；
5. 图标与文字交叉校验；
6. 只有常规方法长期不稳定时，再标注数据训练检测/分类模型。

不要一开始就训练大模型。第一版先用真实截图验证模板匹配和 OCR 的成功率。

### 3.4 重开流程安全条件

- 游戏窗口必须位于前台且位置未改变；
- 当前页面必须被明确识别；
- 危险按钮必须有页面锚点和按钮本身双重确认；
- 点击前再次截图，确认目标仍在原位；
- 点击后必须验证页面确实发生预期变化；
- 连续失败两次后暂停；
- 设置最大重刷次数和最大运行时长；
- 鼠标移到屏幕角落或按全局热键可紧急停止；
- 检测到弹窗、断线、更新提示、结算奖励等未知页面时暂停。

## 4. 数据接口：等待另一位 AI 的基础资料

建议将资料拆成以下文件：

```text
data/
  investment_strategies.yaml
  enemy_modifiers.yaml
  opening_rules.example.yaml
  screen_definitions.yaml
  ui_templates/
```

投资策略字段：

```yaml
- id: stable_internal_id
  name_zh: 显示名称
  aliases: []
  description: 效果说明
  tags: []
  opening_value: 0
  synergies: []
  conflicts: []
  source_version: ""
  icon_file: ""
```

敌人负面词条字段：

```yaml
- id: stable_internal_id
  name_zh: 显示名称
  aliases: []
  description: 效果说明
  severity: 0
  affected_archetypes: []
  counters: []
  source_version: ""
  icon_file: ""
```

资料中的名称、效果和评价应分开保存：名称与效果属于事实数据，`opening_value`、`severity` 和搭配建议属于可调整的策略数据。

还需要另一位 AI 尽量提供：

- 每类页面的原始截图，避免压缩和裁切；
- 游戏分辨率、显示缩放比例、画质及语言；
- 同一词条在不同状态下的截图；
- 从开局信息页到重开完成的完整页面序列；
- 每一步可能出现的弹窗和异常分支；
- 词条版本来源，方便以后处理版本更新。

## 5. 后续能力路线

### 阶段 A：观察型刷开局

- 自动定位窗口；
- 识别开局内容；
- 显示结构化结果和是否值得保留；
- 不自动点击。

这是识别准确率基线，优先完成。

### 阶段 B：自动刷开局

- 加入重开状态机；
- 加入安全验证、热键停止和运行限制；
- 保存每次识别证据；
- 命中后暂停并通知。

### 阶段 C：攻略集合

- 建立版本化资料库；
- 按投资策略、词条、羁绊、角色和阵容检索；
- 每条攻略保留适用版本和来源；
- 识别到游戏内容时自动跳到对应攻略，但不执行操作。

### 阶段 D：当前局势快速评估

逐步增加识别项：

- 回合、资源和生命/容错；
- 商店内容；
- 已拥有单位与强化等级；
- 当前上阵、备选席和羁绊；
- 敌方信息、增益和负面词条；
- 可选事件或奖励。

将状态保存为可回放的 `RunSnapshot`，先实现“截图 → 状态 JSON → 人工校验”。

### 阶段 E：下一步建议

建议引擎分三步：

1. 生成合法候选动作，如买入、刷新、升级、调整阵容、保留资源；
2. 根据即时战力、经济、成型概率和风险评分；
3. 给出排序后的 1～3 个建议，并解释主要原因和不确定项。

第一版只建议，不点击。经过离线回放验证后，再加入“用户确认后执行”。

### 阶段 F：有限自动执行

- 只自动执行高置信度、低风险、可验证的操作；
- 涉及出售、放弃奖励、消耗关键资源时必须人工确认；
- 每个版本更新后先回到观察模式完成回归验证。

## 6. 推荐项目结构

```text
app/
  main.py
  config.py
  perception/
    capture.py
    window.py
    screen_classifier.py
    template_matcher.py
    ocr.py
    opening_reader.py
  state/
    models.py
    stabilizer.py
    run_tracker.py
  decision/
    opening_rules.py
    evaluator.py
    advisor.py
  action/
    mouse.py
    guards.py
    state_machine.py
  knowledge/
    repository.py
    retrieval.py
  ui/
    main_window.py
  telemetry/
    logger.py
    evidence.py
data/
tests/
  fixtures/
    screenshots/
  perception/
  decision/
  state_machine/
tools/
```

建议采用 C# 12、.NET 8 和 WPF，与 BetterGI 已验证的 Windows 原生桌面路线保持接近。图像处理和模型推理分别通过 OpenCvSharp 与 ONNX Runtime 接入。除非后续出现只能通过 Python 生态解决的模型需求，否则不在第一版引入第二套运行时。

本项目只参考 BetterGI 的公开架构和使用经验，独立实现代码与识别资源。BetterGI 使用 GPL-3.0；在本项目坚持禁止商业用途的情况下，不直接复制或改造其 GPL 代码。

## 7. 测试与验收

### 离线测试集

每个页面至少收集：

- 常见分辨率；
- 100%、125%、150% Windows 缩放；
- 不同亮度和特效状态；
- 鼠标悬停、动画、遮挡；
- 正确页面和容易混淆的负样本。

原始截图只读保存，识别测试不能依赖实时游戏。

### 第一阶段验收指标

- 页面识别准确率不低于 99%；
- 已收录投资策略/词条识别准确率不低于 98%；
- 低置信度结果不得触发重开；
- 100 次离线流程回放中不出现错误页面点击；
- 异常弹窗和窗口失焦均能在一次动作前被拦截；
- 连续实际运行达到约定次数，无卡死、重复确认或越界点击；
- 每次决定均可追溯到截图、识别结果、规则和动作日志。

## 8. 实施顺序

1. 收集 20～50 张完整流程截图，确定页面和 ROI；
2. 建立数据表 schema，并导入投资策略、敌人词条；
3. 完成窗口定位、截屏和坐标映射；
4. 完成页面分类器；
5. 完成开局信息识别及离线测试；
6. 完成规则编辑和 `KEEP/REROLL/NEED_REVIEW` 判断；
7. 交付只观察版本，人工对照准确率；
8. 加入鼠标执行、状态机和后置验证；
9. 小次数实机灰度，逐步扩大重刷次数；
10. 固化 `RunSnapshot`，开始攻略检索和局势评估。

## 9. 第一轮开发产物

在基础资料和截图到位后，第一轮应交付：

- 可运行的桌面壳；
- 游戏窗口选择与截图预览；
- 页面识别和 ROI 调试视图；
- 投资策略、敌人词条数据加载器；
- 开局结构化识别结果；
- 可编辑的保留/排除/评分规则；
- 仅观察模式；
- 离线截图测试与准确率报告。

通过这一轮后再接入自动点击，可以显著降低把识别错误转化为误操作的风险。

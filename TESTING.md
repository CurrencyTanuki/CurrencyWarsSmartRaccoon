# 测试说明（Testing）

## 全量测试

```powershell
dotnet build CurrencyWarsAssistant.sln -c Release
dotnet test tests/CurrencyWarsAssistant.Tests -c Release
```

当前全量 **651 项测试**，覆盖：识别（模板+OCR）、状态机、购买逻辑、对局记录、真实帧回放等。

## 测试资源说明（重要）

部分测试依赖 `tests/CurrencyWarsAssistant.Tests/Fixtures/` 下的**真实游戏截图与 OCR 引擎资源**（约 284MB）。这些资源**包含游戏版权素材，不随公开源码分发**（`.gitignore` 已排除）。

### 如何复现完整测试

1. 本地已有完整工作区（含 Fixtures）时，直接跑上述命令即可
2. 从公开仓库克隆后，Fixtures 缺失——**以下测试会跳过或失败**：
   - `Phase2*`（真实帧回放类）
   - `OcrOpeningPageReaderTests`（真实 OCR）
   - 其他依赖 `Fixtures/` 的测试

### 最小可跑测试集（不依赖版权资源）

以下测试可脱离 Fixtures 运行（纯逻辑/模拟帧）：
- `OpeningFilterEvaluatorTests` / `OpeningRuleEvaluatorTests` / `OpeningRecognitionAccumulatorTests`
- `RewardShopPurchaseGeometryTests` / `InitialRewardFormationPlannerTests` 等纯计算类
- `CurrencyWarsNavigationConfigTests` / `GameDataCatalogTests`

筛选运行（仅跑不依赖资源的测试）：
```powershell
dotnet test tests/CurrencyWarsAssistant.Tests -c Release --filter "FullyQualifiedName!~Phase2&FullyQualifiedName!~Ocr"
```

## 为什么测试资源不公开

- Fixtures 含《崩坏：星穹铁道》游戏截图与图标，版权归米哈游
- 本项目许可证（CC BY-NC-SA 4.0）与版权素材的再分发受限
- 如需复现完整测试，请联系作者（QQ 群 726898246）获取脱敏资源或完整工作区

## 测试约定（给贡献者）

- 识别/页面切换/节奏类改动：必须用真实截图回放测试，**只跑模拟帧单测不算验证**
- 新增逻辑必须带测试；一轮只改一个行为变量

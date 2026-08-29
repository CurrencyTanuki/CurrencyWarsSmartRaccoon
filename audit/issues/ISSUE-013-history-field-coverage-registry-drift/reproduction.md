# ISSUE-013 复现：历史字段静态覆盖 registry 漏登记 Health/Population

## 范围

本项只处理 `HistoricalUiFieldCoverageRegistry` 与 `Phase2OperationalState` 公开属性集合不一致。该 registry 仅被测试和文档契约使用，不参与当前 WebView2 历史界面的运行时渲染。

不得把本项转绿描述成“两个历史界面全部字段已显示”。ISSUE-008/009 已独立验证 Health 98、Population 8 等核心摘要；其余真实 HTML 字段缺口继续单列。

## 修前证据计划

1. 隔离运行 `HistoricalUiFieldCoverageTests.Registry_CoversEveryPublicPropertyOfFinalDataTypes`，保存原始红测 TRX。
2. 用反射同一规则列出 `Phase2OperationalState` 的公开属性与 registry 字段：预期模型 33 项、registry 31 项，缺 `Health` 与 `Population`，无多余项。
3. 核对交接原件与工作副本的模型、registry、测试哈希，证明不是前述修复引入。

## 修前结果

定向测试已按上述过滤器隔离运行，结果 0/1。失败在 `HistoricalUiFieldCoverageTests.cs:27`：Expected 集合含 `Health`，Actual 在 `Formation` 后直接进入 `Interest`。

- TRX：`test-results/ISSUE-013-before-focused.trx`
- SHA-256：`596624D645720E884313F8AED8E6D0AC1BE6C2EF3672A42E2DC2ADFD700DADBD`

按测试同一字段名规则对源码全集比较：

- `Phase2OperationalState` 公开 init 属性：33；
- registry 映射：31；
- 缺失：`Health`、`Population`；
- 多余：无。

桌面交接原件与工作副本逐字节一致：

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `HistoricalUiFieldCoverage.cs` | 28721 | `8EE9D134319F3E18ABDD7EFB21B9BA50BFFB32730DCE9C9BC81F08B2186DC166` |
| `Phase2OperationalContracts.cs` | 16879 | `95CBEA4A32D530D6006F4357546A073F731996A034DA67041DDF6E82FCC2FB12` |
| `HistoricalUiFieldCoverageTests.cs` | 11005 | `5B25DF7E01BB6A7264F5164F0026E9CD8BCDE6FDC553B3FB0263051572C93C0A` |

因此该漂移随 20260808 交接原件存在，不是 ISSUE-001～012 引入。

## 运行时边界

- 当前主历史和悬浮详细历史均使用 WebView2 + `gen_report.py`。
- `Population` 已由 ISSUE-008 在共享 renderer 中显示。
- `Health` 当前从 `FinalPreparationSnapshot.Health` 显示，不由 registry 驱动。
- 旧 `HistoricalDetailPresentationBuilder` 不是两个当前窗口的可见主体。

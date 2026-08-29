# ISSUE-013 验证记录

## 修前

- 定向测试：0/1；
- TRX：`test-results/ISSUE-013-before-focused.trx`；
- SHA-256：`596624D645720E884313F8AED8E6D0AC1BE6C2EF3672A42E2DC2ADFD700DADBD`；
- 源码全集：33 个 `Phase2OperationalState` 公开属性、31 个 registry 映射，缺 `Health`/`Population`，无多余项。

## 修改范围

- 唯一生产文件：`src/CurrencyWarsAssistant.App/HistoricalUiFieldCoverage.cs`；
- 修前：28721 字节，SHA-256 `8EE9D134319F3E18ABDD7EFB21B9BA50BFFB32730DCE9C9BC81F08B2186DC166`；
- 修后：28827 字节，SHA-256 `7C40E3FB539F88464F375D5E60800894FF983C66D6FB812C01D9D0E94A4EF1A4`；
- 逐行 diff 仅新增 `nameof(Phase2OperationalState.Health)` 与 `nameof(Phase2OperationalState.Population)`，没有其他生产逻辑变化；
- 模型文件未改：SHA-256 `95CBEA4A32D530D6006F4357546A073F731996A034DA67041DDF6E82FCC2FB12`；
- 测试文件未改：SHA-256 `5B25DF7E01BB6A7264F5164F0026E9CD8BCDE6FDC553B3FB0263051572C93C0A`。

修后源码全集：33 个公开属性、33 个唯一映射，重复 0、缺失 0、多余 0。

## 定向测试

覆盖完整性与重复/静默登记两项：2/2。

- TRX：`test-results/ISSUE-013-after-focused.trx`；
- SHA-256：`AFF3FC8987CD2092C47B91B077F9A9B59E9A6A3DD063CC749D794F5B1D68ACFD`。

全部 `HistoricalUiFieldCoverageTests`：5/5。

- TRX：`test-results/ISSUE-013-after-class.trx`；
- SHA-256：`D1055689AFA53AC7E5A3AF4D035F4FB0AC6DEEFD94544A28974CB74E7DE5FCB0`。

## 关联历史链保护

覆盖 `HistoricalPopulationHtmlTests`、详细历史来源选择/归档 render state/去重 UI 契约、UI redesign、历史投影、run completion archive 与 challenge summary：62/62。

- TRX：`test-results/ISSUE-013-after-related-history.trx`；
- SHA-256：`5F98752FD7ADD6AE241C94663716336BB55FEC099E7F21AADF99547B457200E0`。

## 全量回归

- 最终：807 项 = 805 Passed / 0 Failed / 2 NotExecuted；
- TRX：`test-results/ISSUE-013-after-full.trx`；
- SHA-256：`D59D22FB6920DD75AE143A7FCAC08D266AC4DD8F4D91360A07066555821FED8D`；
- ISSUE-012 基线：807 项 = 804 Passed / 1 Failed / 2 NotExecuted，SHA-256 `EF4A48B69D0372E7E15B23B112D11232AFC48425A843C9550229EFB39B617B17`；
- 逐唯一 testName 对比：807 对 807，新增 0、缺失 0、结果变化 1；唯一变化为本项 `Registry_CoversEveryPublicPropertyOfFinalDataTypes` 从 Failed 转为 Passed；其他 806 项结果不变。

两条性能项仍为静态 NotExecuted，不能计作通过。因此“全量 0 Failed”不等于所有测试均已执行。

## Release 构建

- solution Release build：退出码 0；
- 0 警告、0 错误；
- log：`build/ISSUE-013-release-build.log`，SHA-256 `8ACC142FA18672F6755219CC29C9631E6D9EDD4909CBA1A3DF2E44EC75B6A012`；
- binlog：`build/ISSUE-013-release-build.binlog`，SHA-256 `8A5FF2D69C2CD161C278AF0897378B0366CDD821481618954A8256DB4439C4AD`；
- Release App DLL：665088 字节，SHA-256 `0957466C16ECF93264D1DF9A60F08A59B5D40013BACA718A6CB2DCCC00C1EA9E`。

## 用户界面边界

registry 没有运行时消费者；本修改不会改变当前两个 WebView2 历史界面的可见内容。ISSUE-008/009 已用真实窗口验证节点2-2、血量98、商店Lv7、人口8；本项关联测试只保护这些已有链路，没有重新宣称全部字段已在两个界面显示。

真实 HTML 尚未逐项显示 registry 中所有 HistoricalDetail/Diagnostics 字段，该缺口必须在后续全字段双界面审计中单独处理。

## 独立审查

独立只读审查员已批准本项，严格限定为静态 registry 契约漂移修复。审查员独立核对了原件、两行 diff、33/33 唯一映射、修前/修后 TRX、807 项逐名差分、构建和运行时零引用边界；未发现阻断。

批准不代表真实 UI 有变化，也不证明两个历史界面已覆盖全部字段。两条性能测试继续 NotExecuted。

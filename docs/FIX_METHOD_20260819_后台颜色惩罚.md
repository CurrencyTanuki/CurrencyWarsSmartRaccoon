# 后台颜色惩罚修复——已实施记录（2026-08-19）

> 用户批准「做第一步(warp 对准/内缩) + 方案 A(后台专属降权)」，并要求确保 A 相对 B(全局删惩罚)
> 综合识别正确率更高。已实施完毕，源码改动在工作区未提交（等用户实机验收后提交/发布）。

## 已实施（方案 A + 第一步，单变量）

| 文件 | 改动 |
|---|---|
| `src/CurrencyWarsAssistant.Vision/CharacterCardRecognition.cs` | ① options 加 `BackRow` 字段；② 常量 `BackRowColorSimilarityGrace=0.80`；③ `Recognize` 内 BackRow 时后台槽用「内缩 6px matchRect」做匹配（星级仍用原 slot——保护 000032 星级）；④ `Match` 加 backRow 参数，后台用宽松触发线 0.80；⑤ Rank 传 BackRow |
| `src/CurrencyWarsAssistant.Tasks/Phase2OperationalScreenshotAnalyzer.cs` | 后台 warp 识别传 `boardOptions with { BackRow = true }`；应援槽强制 `BackRow=false`（review should-fix：避免 0.80 削弱应援污染惩罚） |

## 效果（生产 Recognizer 实测）

- 后台槽0：`误判开拓者 0.499/lead 0.001` → `正确霍霍 0.538/lead 0.022`（角色纠正 ✓）
- **UserReferenceGroundTruthTests 10/10 全绿**（爻光星级2/欢愉5/后台角色/佩佩）
- SilverWolf 16/16、CharacterCard 8/8
- **全量 901 通过/16 失败/2 跳过**：16 失败全为既有基线组，**零新增回归**

## A vs B（用户要求的综合正确率对比）

- **A（本实现）**：后台角色纠正为霍霍（正确），且保留前台/备战席惩罚 → 金标准 10/10。
- **B（全局删惩罚）**：后台 conf 略高(0.542)但**角色仍错(开拓者)**，且全局删惩罚会伤前台相似角色区分（爻光/欢愉回归）→ 综合正确率低于 A。
- **结论：A 更优**（后台识别对 + 前台/备战席不退化）。

## 已知（待用户实机验收确认是否够）

- keyframe analyze 后台 conf 单帧仍 0.52（探针已 0.538 但 analyze 全链路含应援检测等未升至 0.55 线）——角色判断已纠正、金标准全绿，但**后台完整识别落地(analyze 线路)需实机验收**。
- 若实机后台仍不足，下一步可在「后台阈值 / 应援检测」单独收尾（未动，等指示）。

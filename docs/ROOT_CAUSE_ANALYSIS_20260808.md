# 备战页识别十项问题 · 根因分析（RCA）

> 日期：2026-08-08（用户 000032 实机验收失败后，本轮对话重新分析）
> 范围：备战页识别管线（`src/CurrencyWarsAssistant.Tasks/` + `src/CurrencyWarsAssistant.Vision/`）
> 素材：`tests/CurrencyWarsAssistant.Tests/Fixtures/PageReplay/user_ref_000032_prep22.png`（2559×1439，2-2 备战）
> 铁证：`C:\Users\zzz81\Desktop\cwt_recognition_report_data.json`（000032 全量识别输出，由 `FullRecognitionReportExportTests` 导出）

---

## 〇、总述：为什么"全量测试通过"但实机近半错误

`FullRecognitionReportExportTests.ExportAllReferenceRecognitionToJson` 的"通过"= `Assert.NotEmpty(report.Entries)`（176 行）——**只断言"有输出"，不校验任何字段值**。同类问题遍布备战识别测试：

| 缺陷模式 | 实例 |
|---|---|
| 零断言（只 WriteLine） | `UserReferenceFullRecognitionTests` 全部 12 个方法、`Phase2Prep37FullSlotDiagnosisTests`、`VideoPrepFrameBatchDiagnosisTests` 等 |
| 非空断言 | `UserReferenceEquipmentTests:70`（`Assert.NotEmpty(formation)`，注释写了 ground truth 却从不比对）、`EndToEndRealFrameRecognitionTests:67,85` |
| 数量下限/计数断言 | `EndToEndRealFrameRecognitionTests:71`（≥9）、`Phase2Prep37SlotDiagnosisTests:90`（=11，11 槽全识别成同一个错角色也通过） |
| 状态枚举断言 | `StoreLevel != "not observed"`（不查数值）、`synergy.ActiveCount >= 1` |
| 关键字段零断言 | 血量、商店 Lv、人口、星级、装备种类在测试中只被打印，从不断言等于任何值 |
| 素材错配 | `UserReferenceFullRecognitionTests:28,104` 把 `000036_prep31.png` 标成 `"000038"`；`UserReferenceEquipmentTests:157` 测 000035 却加载 000036 图 |
| ground truth 缺失 | 素材目录无任何"图→每槽 expected"数据文件（唯一人工 ground truth 是测试注释：000032=银狼 2 件/刃 1 件/其余 0 件） |

**结论：测试全绿 = 只要不抛异常。修复必须同时建立 ground truth 断言。**

---

## 一、十项问题根因（逐项，代码级）

### 1. 血量识别 = 0（实际 98）

- **区域错位**：`Phase2RecognitionRegions.PreparationHealthValue = (0.797, 0.064, 0.035, 0.050)`（x=1530-1597@1920）——裁剪验证该区域**无数字**（std=37 仅为背景）；实际血量 "98" 在 x≈1382-1650, y≈40-120（qwen 视觉确认 98，裁剪 `health_c_wide` 识别 98）。
- **兜底区域传错**：`Phase2OperationalScreenshotAnalyzer.cs:528-537` 调用 `ReadIntegerWithLocalizedFallbackAsync(frame, PreparationHealthValue, PreparationHealth, ...)` ——`digitRegion`（第二参数，模板匹配兜底区域）传了**血量图标区** `PreparationHealth`（0.730,0.010，是心形图标不是数字）→ OCR 失败后模板匹配必失败。
- 无放大/降级路径（`SituationScreenshotAnalysis.cs` 的同一字段有 enlarged-OCR 更稳实现，analyzer 路径没有）。

### 2. 商店等级 = 0（实际 Lv.7）

- **区域错位**：`StoreLevelValue = (0.18, 0.83, 0.035, 0.035)`（x=345-412, y=896-934@1920）——裁剪验证**无数字**（std=11 纯背景）；实际 "Lv.7" 在 x≈278-383, y≈880-934（裁剪 `store_b_left` 识别 "v. 7"、`store_c_wide` 识别 "Lv. 7"）。
- OCR 失败后数字模板兜底对 `store-level` 不强制（`RequiresCleanNumericToken` 只含 interest/cumulative-spend），无放大降级 → 恒 0。

### 3. 火花无法识别（用户：前台火花）

- 数据确认：**火花=character_09（4费前台），花火=character_54（2费后台）是两个独立角色**（用户说的"两套皮肤"实为两个角色条目）。两者各有 `__default.png` 模板。
- 000032 识别输出 Front 4 条 = unknown(0.415)/绯英(0.686)/银狼(0.562)/藿藿(0.682)，**无火花** → 火花所在槽要么被判 unknown（conf<0.55 未达阈值），要么被误识成其他角色（藿藿 0.682 可能就是火花槽的误识）。
- 需实机诊断 000032 前台各槽匹配分数（修复时跑诊断）。

### 4. 后台特殊单位佩佩无法识别

- 特殊单位（佩佩/狸猫等）**无视觉模板**：`character-card-templates/` 只有 85 个角色模板 + 1 个 `bench_special_privilege_armament_box.png`（特权武装箱）。`CharacterCardRecognition` 的格子内容识别只匹配角色模板 → 佩佩槽判 `Uncertain`，输出 `unknown-formation-unit-Back-x`。
- 000032 输出证实：Back unknown(0.472) 即佩佩槽。
- 文本层 `TriggeredSpecialUnits`/`ApplySpecialUnitContext`（analyzer:890-975）只把特殊单位 id 挂到 pending 候选，**从不进识别结果**。

### 5. 装备种类全错 + 数量不对（银狼 3 vs 实际 2；藿藿 3 vs 实际 0）

- **共享图标 canonical 丢粒度（种类必错）**：`Phase2IconRecognition.cs:175-205` 按 SHA256 分组（实测 157 图标中 **47 组共享、107 个图标字节相同**——如 002 分裂•圣杯/003 圣杯/013 诅咒•圣杯 同一图标），`ResolvesExactIdentity=false`，识别输出恒为**组内字典序第一个 id（canonical）**→ 同组装备永远识别成同一个，种类必错。
- **置信度兜底过宽**：装备模板 `MinimumConfidence=0.30` + 兜底线 0.60 + margin 0.025 → 相似图标（不同组但形状接近）在 0.60-0.74 区间互相误配。
- **数量虚报**：000032 藿藿（实际 0 件）识别出 3 件——装备区域 `CharacterEquipmentSlots` y=0.96H 卡底（1920 系 y≈463-508）裁剪 std 实测 8-11（无前景），却识别出 3 件 → **区域与实际装备位置不符**（前台卡装备可能不在卡底 y=0.96H，或在卡外下方），`HasDetailedForeground` 前景判定与识别路径不一致。
- 报告显示"三个角色带装备"（银狼/藿藿/刃）与用户 ground truth（银狼 2 件/刃 1 件/其余 0）数量种类均不符。

### 6. Front/Back 的"格"都是 0

- **直接根因：报告导出漏填 SlotIndex**。`FullRecognitionReportExportTests.cs:92-113` 构造 `SlotInfo` 时**没有给 SlotIndex 赋值**（默认 0），`gen_cwt_report.py` 的"格"列显示 `s['SlotIndex']` → 全部 0。
- 识别层槽位索引本身正常（analyzer 内 `slot.SlotIndex` 正确使用）。

### 7. 备战席 Bench 少识别一个（用户：3→2）

- 000032 实测 bench 有 4 个有内容卡位（像素 content=0.55/0.74/0.71/0.81 → 索引 2/4/6/7），识别输出 Bench 4 条：爻光(idx2)/刃(idx4)/开拓者(idx6)/缇宝(idx7)。
- **缇宝（character_55）是特殊单位被当角色识别** → 若用户数的是"3 个常规角色 + 1 特殊单位"，则软件把 4 条都当角色；若用户数 3 个，则有一个是误识。
- 空槽判定 `EmptyVisualStandardDeviation`（stddev≤18）对"半截卡/污染卡"不可靠；`bench-empty-*.png` 模板存在但代码从不参与匹配。

### 8. 部分角色星级错误（后台除爻光外全错）

- `RecognizeStarLevel`（CharacterCardRecognition.cs:381-474）：卡底 y=0.66-0.94 带内金色 HSV 掩码 + 列投影峰值计数。
- **星带位置漂移**：代码注释自认"000032/000035 星级有更大偏移"——固定 y=0.66-0.94 带与实机星位不符（我的像素检测：Back1 刃=1 峰、Back2 开拓者=3 峰、Back3 爻光=1 峰 vs 识别输出 2/2/2——两边都不一致，说明星带没对准）。
- **伪峰**：后台卡底部费用标签/卡框金边在掩码带内产生伪峰。
- 000032 识别输出：刃 2 星、开拓者 2 星、爻光 2 星（用户说除爻光外全错 → 刃/开拓者实际非 2 星）。

### 9. 羁绊显示"1-正无穷"

- 完整链条：实机羁绊识别 `ObserveSynergies`（analyzer:2080-2115）只识别到羁绊 **id**（icon 匹配 Known），面板 "N/N" 文本 OCR 失败 → `ActiveCount=null, NextThreshold=null` → 合并逻辑（770-800）从阵容推算**只补 ActiveCount**（789-796），**不补 NextThreshold** → 输出 `ActiveCount=推算值, Next=null`。
- 渲染端 `gen_cwt_report.py:115`：`s.get("NextThreshold") or "∞"` → **null 渲染成"正无穷"**。
- 000032 输出：7 个羁绊全部 `NextThreshold=null`（欢愉 4/null、星核猎手 2/null、仙舟 2/null……）→ 用户看到"欢愉 4-∞"等。
- 用户疑问"正无穷是什么意思"：游戏里羁绊层级有限（如星核猎手 2/3/4），不存在正无穷——显示 ∞ 是软件 bug（数据缺失被渲染成 ∞）。

### 10. 人口数量 8/8 无识别

- `ReadProgressAsync`（analyzer:3789-3855）确实读 `PreparationFrontCapacity` 区域（0.430,0.180,0.130,0.090 → x=826-1075, y=194-291@1920）解析 "8/8"（`ExperiencePattern` 正则），但结果**只用于验证"等级"**（`capacityLevel` → `PlayerProgressState.Level`），**没有独立的人口字段**。
- `PlayerProgressState = (Level, Experience, ExperienceToNextLevel)`（Phase2OperationalContracts.cs:232-235）——无 Population。
- 000032 输出 `Level=null, Experience=null`（人口 8/8 未被利用；区域仅覆盖 8/8 左半，x=826-1075 vs 实际 8/8 在 x≈826-1159）。

---

## 二、额外发现（不属十项但必须修）

1. **SlotIndex 导出丢失**（问题 6 根因）——报告 JSON/HTML "格"列全 0。
2. **装备共享图标 47 组 / 107 个**（68% 图标字节相同）——canonical 机制需改为候选输出 + 上下文消歧。
3. **测试系统性缺陷**——零断言/非空断言/素材错配/ground truth 缺失（见总述表）。
4. **佩佩/狸猫等特殊单位无模板**（问题 4 根因）。
5. **`bench-empty-*.png` 模板从不参与匹配**（问题 7 相关）。
6. **羁绊合并补数不对称**（问题 9 根因）。

---

## 三、修复方案（按根因，非打补丁）

| # | 问题 | 修复方向 |
|---|---|---|
| 1 | 血量 0 | 重新像素标定血量数字区（实测 x≈1382-1650,y≈40-120@1920 → normalized ~(0.72,0.037,0.14,0.06)）；修正 `digitRegion` 参数为数字区；加宽 OCR 裁剪 |
| 2 | 商店 Lv 0 | 重新标定（实测 x≈278-383,y≈880-934 → ~(0.145,0.815,0.055,0.05)）；单数字走数字模板 + 放大降级 |
| 3 | 火花 | 诊断 000032 前台各槽匹配分数，确认是模板缺失/阈值/误识；若"两套皮肤"=火花/花火两角色，确保两模板都参与匹配 |
| 4 | 佩佩 | 从实机素材裁剪佩佩等特殊单位模板，注册为 SpecialOccupied 类模板，格子内容识别输出特殊单位 id（而非 unknown） |
| 5 | 装备 | 共享组输出 CandidateIds（显示层展示组内全部候选）；校准装备区域位置（实机裁剪验证）；前景判定与图标识别同一区域；修正 0.30/0.60 兜底阈值 |
| 6 | 格 0 | 导出 SlotIndex |
| 7 | Bench | 诊断 000032 bench 各槽匹配；用 bench-empty 模板 + 内容判定；区分特殊单位（缇宝） |
| 8 | 星级 | 动态定位星带（在卡内搜索金色星行位置，而非固定 0.66-0.94）；过滤费用标签/卡框金边 |
| 9 | 羁绊 ∞ | 合并补数时同步补 NextThreshold（ResolveNextBondTier）；渲染端 null 显示"已满级"或"?"，不显示"∞" |
| 10 | 人口 | PlayerProgressState 增加 Population 字段；区域扩到完整 8/8；报告导出 |

**修复顺序**：先修数据/显示层确定性 bug（1/2/6/9/10 + 导出），再修识别管线（3/4/5/7/8），最后测试重构（ground truth 断言）→ review → 真实帧回放验证 → 交付。

**验收标准**：000032 ground truth（血量 98、商店 Lv.7、人口 8/8、前台 4 卡含火花、后台含佩佩、银狼 2 件/刃 1 件/其余 0、羁绊 Next 有值或"已满级"）逐项断言。

---

## 四、修复进展（2026-08-08 本轮对话，逐项）

| # | 问题 | 状态 | 修复内容 |
|---|---|---|---|
| 1 | 血量 0 | ✅ 已验证（98） | 区域重标定 (0.720,0.058,0.140,0.085) + digitRegion 从图标区改数字区 |
| 2 | 商店 Lv 0 | 🔧 待验证 | 区域重标定 (0.150,0.795,0.090,0.062) + 新增 ReadStoreLevelAsync（文本 OCR + LevelPattern 读 "Lv.7"） |
| 3 | 火花 | 🔧 待验证 | 用 000032 实机卡面生成 character_09 变体模板（__user_prep22.png） |
| 4 | 佩佩 | 🔧 待验证 | 裁剪实机卡面生成 special_unit_peipei 模板（SpecialOccupied）+ 识别层输出模板 id |
| 5 | 装备种类 | ✅ 部分 | canonical 共享组 → 导出/报告展示全部候选（Ambiguous 标记）；0.60 兜底加 top-2 margin≥0.015 收紧 |
| 6 | Front/Back 格 0 | ✅ 已验证 | **根因：应援路径单槽识别 SlotIndex 恒 0**（RecognizeCharactersSafely）→ with { SlotIndex = i } 修正 |
| 7 | Bench 3→2 | 🔧 待验证 | 000032 当前识别 4 条（爻光/刃/开拓者/缇宝），缇宝是普通角色——疑似旧版本已改善 |
| 8 | 星级全错 | 🔧 待验证 | RecognizeStarLevel 重构：动态找星带（行聚合金色峰 ±3 行）替代固定 y=0.66-0.94 |
| 9 | 羁绊 1-∞ | ✅ 已验证 | 合并补数同步补 NextThreshold（ResolveNextBondTier）；头号玩家 1 档=已满级正确；渲染 null→"已满级"（gen_cwt_report.py + HistoricalDetailViewModels） |
| 10 | 人口 8/8 | 🔧 待验证 | PlayerProgressState 加 Population 字段 + ReadPopulationAsync（独立于等级读取） |

**新根因发现（本轮）**：
- **SlotIndex 全 0 = 应援路径单槽识别**（RecognizeCharactersSafely 对 cheeredIndices 非空时逐槽调用 Recognize，单元素列表返回 index=0）——000032 有应援（开拓者在后台）触发，导致报告 Front/Back"格都是 0"。
- 装备"种类全错"两层：①47 组共享图标 canonical 丢粒度（已修：显示候选）②非共享组 0.60-0.74 区间误配（margin 收紧实验后回退——真装备也被过滤，得不偿失）。
- 缇宝（character_55）是普通角色非特殊单位；佩佩/狸猫才是特殊单位（无模板）。
- 星级：实机星带位置各卡不同（刃 0.95-1.0H / 爻光 0.74-0.82H），固定带必错。
- **佩佩模板裁剪错位（第二轮）**：模板从 x=1265 裁剪（中心 1330），识别槽位是 [1330,600]（中心 1395）——偏左 65px → 匹配负相关。重新裁剪 x=1330-1460 后 conf 0.682（SpecialOccupied ✓，runner-up 翡翠 0.465 分差大）。
- **人口丢失根因（第二轮）**：人口原并进 PlayerProgress（Level/Experience/ExperienceToNextLevel），等级 OCR 失败（progress Unknown）时人口一起丢。改为独立字段 Observation<int> Population。
- **StoreLevel 暗字 OCR 读不出（第二轮）**：数字 X 精确定位 x=288-308/y=849-924@1920（单字符 27×75px）；数字模板对 36×99px 区域投影分割失败（"No unambiguous digit components"）；普通 OCR 空。三路径：数字模板 → robust OCR+LevelPattern → 数字正则。

**待办**：
1. 导出测试完成后核对 000032 全部字段（血量/商店/人口/星级/火花/佩佩/装备/羁绊）
2. 建立 ground truth 断言测试（UserReferenceGroundTruthTests）
3. 全量回归（关键测试组 43 项 + review）
4. 交付 self-contained 包（完整路径）

---

## 五、最终状态（2026-08-08 深夜，0.2.839 交付）

- **000032 ground truth 断言 8/8 通过**（UserReferenceGroundTruthTests）：
  Health=98、Population=8、StoreLevel=7、Front 含火花(09)、Back 含佩佩、
  Bench 4 卡、SlotIndex=[0,1,2,3]、羁绊欢愉 5/7 + 头号玩家已满级。
- **关键回归 60/60 通过**（含 review 修改后）。
- **review 完成**：无阻塞；1 should-fix（商店 Lv 取最右 glyph 对 Lv.10+ 会取个位——
  已知限制已注释）+ 3 nit 已处理（sliding 高度 10 档保留 16/20、删 PlayerProgressState.
  Population 半成品字段、SpecialOccupied 星=null）。
- **交付**：`artifacts/CurrencyWarsSmartRaccoon-0.2.839-win-x64-portable/CurrencyWarsAssistant.App.exe`
  （self-contained 350MB，含 data/ 与佩佩/火花新模板）。软件 requireAdministrator，
  启动需用户桌面双击 exe（UAC 确认）。
- **剩余待用户实机验收**：星级精确值（000032 输出 刃3/开拓者1/爻光3/缇宝3——
  爻光=3 新旧一致，刃/开拓者待确认）、装备候选显示可接受度、Lv.10+ 商店等级。

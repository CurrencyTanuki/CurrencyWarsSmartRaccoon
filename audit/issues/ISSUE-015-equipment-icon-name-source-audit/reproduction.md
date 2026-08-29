# ISSUE-015：装备图标—名称—ID—来源专项核对

状态：证据与清单已完成，等待独立只读审查；本项未修改任何生产装备资产、名称、分类或识别映射。

## 问题与验收范围

交接说明声称装备数据已完成，但用户明确指出本地图标与标注名称可能错配。不能仅凭 JSON 中已有名称、路径或 URL 宣称正确。本项只处理资产与来源核对：

1. 盘点全部本地装备记录、中文名、内部 ID、分类、图标路径与哈希；
2. 逐图检查空白、损坏、裁切、相邻错位、跨名称重复和分类异常；
3. 与记录声明的远程图片、固定来源快照、独立社区资料和官方公告交叉核对；
4. 来源冲突不猜测修改，保存证据并标为未验证；
5. 生成完整 157 行 XLSX/CSV 清单。

装备识别、存储及两个历史界面的玩家可读展示属于后续独立问题，不在本项冒充完成。

## 输入与原件保护

- 工作副本：`D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-audit-20260808`
- 交接原件：`C:\Users\zzz81\Desktop\货币战争Codex交接包-20260808`
- 运行数据：`data/runtime/1.0.0/4.4/equipment/equipment.json`
- 原始数据：`data/raw/4.4/equipment/890ae486642e979b/records.json`
- 工作副本、交接“源代码”和交接“程序”三份 `equipment.json` SHA-256 均为：
  `4B525D125B38B86EF68852C3D06BB23A28A7190A3389D9821AB814F7CBCB3554`

本项生成物仅位于本审计目录；生产 `data/`、`src/`、`tests/` 均未因本项修改。

## 本地静态复现

脚本：`verify_local_equipment_assets.py`

关键结果：

- 157 条记录、157 个唯一 ID、157 个唯一中文名、157 个声明 PNG；
- 实际 PNG 157，缺失 0、孤儿 0、重复路径 0、文件名与 ID 不符 0；
- 工作副本/交接源代码/交接程序三方图标逐项与声明 SHA 一致：157/157；
- `base_equipment_id` 引用 36 条，缺失 0；`component_ids` 引用 108 条，缺失 0；
- 数字编号范围 001–159，仅缺 018/019；记录集合与文件集合完全一致，因此不是本地漏文件；
- 唯一图像哈希 97；47 个重复哈希簇覆盖 107 条，50 条为单例；
- 14 个运行分类合计 157。

证据：

- `local-asset-verification.json` SHA-256：`E2BF6E295FD042D33A53DC6F671BAD794309A40C3D9E3411863F75FB8BF8403A`
- `local-asset-verification.csv` SHA-256：`5F3CCA02D24D6DF50E8362A9E715795D7CA21F1395A5F30C73D5859ADC412816`

## 视觉复现

`build_contact_sheets.py` 按运行记录顺序生成 8 页、每页最多 20 项的联系表。主执行者与独立视觉代理均逐页检查全部 157 项。

结论：

- 未见空白、透明损坏、异常裁切、相邻格错位；棋盘格是脚本显示 alpha 的背景；
- 垃圾袋 038 与金垃圾袋 039 未互换；基本装备、消耗品、三类武装箱和星徽秘典的直观物体类别相符；
- 47 个重复簇全部属于同一名称核心：5 个命运改件簇、6 个骇客改件簇、36 个进阶/特权簇，其中 3 簇另含白昼羁绊版本；
- 归一名称后跨核心重复簇为 0；36/36 特权记录的 `base_equipment_id` 指向同核心进阶装备；
- 抽象阵营/流派徽标和幻想专名只能判“无明显本地错位”，不能靠画面证明官方中文名。

联系表位于 `visual-local/equipment-contact-01.png` 至 `08.png`，逐页哈希记录在最终工作簿“视觉证据”页及 `workbook-manifest.json`。

## 远程图标复现

脚本：`verify_remote_assets.py`

157 个 `patchwiki.biligame.com` 唯一图片 URL 全部重新下载并比较：

- HTTP 200：157/157；
- 远程内容 SHA = 本地实际 SHA：157/157；
- 远程宽高 = 本地/声明宽高：157/157；
- 错误：0。

证据：

- `remote-icon-verification.json` SHA-256：`E356AC6BC3EFCAA0ED882D1791E1174F6AA26CF44BD42EBBC4B1A0DEDF9DD96E`
- `remote-icon-verification.csv` SHA-256：`F43621A0405D86C85A5E937016F3C8BFD607C23FEA5C6DB745EAE35EAEB0DFFB`

这只能证明本地图标字节与声明的社区图片源完全一致，不能单独证明中文名或分类是官方确认。

## 来源页面复现与限制

逐条详情页脚本 `verify_source_pages.py` 首轮得到 9 条完整页面实证；随后 BWIKI 反爬/限流导致 67 条 HTTP 567、80 条约 987 字节的 HTTP 200 占位页，星徽秘典为无独立页的汇总页 URL。证据完整保留，未把占位页算作成功，也未继续高频请求：

- `source-page-verification.json` SHA-256：`F21A132896C9FC6C28CBB30425DA9C441336B54C07512B304A8199596303915D`
- `source-page-verification.csv` SHA-256：`7C0AB6AFC4ADF04888227EF9E2558068656DCFFF36C6BE75BF8DCA8B52D996BB`

因此来源核对改用固定总表修订和官方/独立来源交叉，而不是谎称 157 个详情页全部实时可访问。

## 固定快照冲突复现

脚本：`capture_source_conflicts.py`

固定 API：`https://wiki.biligame.com/sr/api.php?action=parse&format=json&oldid=99085&prop=text%7Ctitle%7Crevid`

保存的原始响应 `web-evidence/bwiki-equipment-oldid-99085.json` 独立解析得到：

- `divsort` 行 159；唯一名称 158；唯一重复名为“财富”两行；
- 本地名称与表内匹配 156 个；本地排除财富×2与专家邀请函×1，另从特殊道具段补入星徽秘典×1；
- 两条财富分别为“命运改件”和“其他”，共用同一图标；
- Fandom revision 459160 的保存响应可复核为 119 条数据行、118 个唯一英文名；其中两条 Wealth 分别位于 Destiny Component 与 Other，Emblem 段也包含与本地 001 对应的 Holy Grail of Destiny Emblem；该页未覆盖本地 156–159；
- 交接 `coverage_note` 所称“筛选表158条、两条财富为误列”的口径与固定修订 DOM 不一致，且未说明专家邀请函的预先排除。

官方米游社 V4.0 公告将“专家邀请函”单列在“战利品”而不是“装备”，因此排除它有官方类别依据。两条财富则在两个社区源中同时存在，最多只能提出“7月24日联动上线前过渡资产”的解释；没有上线后的实机图鉴或截图，不能宣称它们是误列或应永久删除。

冲突摘要：`web-evidence/source-conflict-summary.json` SHA-256：
`90D065C7A1F6CB80518CDB7BC2C95D627FE29D3689483D0AE9A96D37FC411DAC`

## 需要保留的未验证项

1. 两条财富是否应纳入 2026-07-24 联动上线后的当前 4.4 装备清单；
2. 垃圾袋、金垃圾袋的来源端原始类型分别仍是 `？`、`？？？`，本地映射为 `special_material` 的精确分类未获来源确认；
3. 星徽秘典无独立 BWIKI 详情页，`special_item` 是本地建模枚举，效果说明仍有推导成分；
4. 157 项没有官方完整逐项图标目录；社区来源和相同哈希不得被表述为“官方全量确认”。

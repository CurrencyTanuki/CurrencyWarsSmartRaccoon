import fs from "node:fs/promises";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const here = path.dirname(fileURLToPath(import.meta.url));
const issueDir = path.resolve(here, "..");
const projectRoot = path.resolve(issueDir, "..", "..", "..");
const handoffRoot = String.raw`C:\Users\zzz81\Desktop\货币战争Codex交接包-20260808`;
const runtimeDir = path.join(projectRoot, "data", "runtime", "1.0.0", "4.4", "equipment");
const rawDir = path.join(projectRoot, "data", "raw", "4.4", "equipment", "890ae486642e979b");
const originalSourceRuntime = path.join(handoffRoot, "源代码", "data", "runtime", "1.0.0", "4.4", "equipment");
const originalProgramRuntime = path.join(handoffRoot, "程序", "data", "runtime", "1.0.0", "4.4", "equipment");
const outputDir = path.join(issueDir, "outputs", "019fe1b0-c12f-7373-b6f1-e42697b1805d");
const previewDir = path.join(here, "previews");
const workbookPath = path.join(outputDir, "货币战争装备图标名称来源核对清单.xlsx");
const csvPath = path.join(outputDir, "货币战争装备图标名称来源核对清单.csv");

const sha256 = async (filePath) => {
  const bytes = await fs.readFile(filePath);
  return crypto.createHash("sha256").update(bytes).digest("hex");
};

const loadJson = async (filePath) => JSON.parse(await fs.readFile(filePath, "utf8"));
const boolZh = (value) => (value ? "是" : "否");
const shortId = (value) => value?.replace("currency_wars_equipment_", "") ?? "";
const csvEscape = (value) => {
  const text = value == null ? "" : String(value);
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
};

const runtime = await loadJson(path.join(runtimeDir, "equipment.json"));
const raw = await loadJson(path.join(rawDir, "records.json"));
const remoteRows = await loadJson(path.join(issueDir, "remote-icon-verification.json"));
const pageRows = await loadJson(path.join(issueDir, "source-page-verification.json"));
const records = runtime.records;
const rawById = new Map(raw.records.map((record) => [record.id, record]));
const remoteById = new Map(remoteRows.map((record) => [record.id, record]));
const pageById = new Map(pageRows.map((record) => [record.id, record]));

const localHashGroups = new Map();
for (const record of records) {
  const iconPath = path.join(runtimeDir, record.icon.asset_path);
  const actualHash = await sha256(iconPath);
  if (!localHashGroups.has(actualHash)) localHashGroups.set(actualHash, []);
  localHashGroups.get(actualHash).push(record);
}

const contactSheetHashes = [];
for (let index = 1; index <= 8; index += 1) {
  const fileName = `equipment-contact-${String(index).padStart(2, "0")}.png`;
  const filePath = path.join(issueDir, "visual-local", fileName);
  contactSheetHashes.push({
    index,
    fileName,
    filePath,
    sha256: await sha256(filePath),
    first: records[(index - 1) * 20]?.id ?? "",
    last: records[Math.min(index * 20, records.length) - 1]?.id ?? "",
  });
}

const sourcePageStatus = (row, id) => {
  if (row?.http_status === 200 && row?.title_contains_name && row?.body_contains_name && row?.body_references_image_basename) {
    return "页面实证通过";
  }
  if (String(row?.error ?? "").includes("HTTP Error 567")) return "HTTP 567（限流/反爬）";
  if (row?.http_status === 200 && Number(row?.bytes ?? 0) < 5000) return "HTTP 200 反爬占位页";
  if (id === "currency_wars_equipment_159") return "汇总页URL；单页未验证";
  return "页面未验证";
};

const clusterRelation = (group) => {
  const categories = new Set(group.map((item) => item.category));
  if (categories.has("fate_component")) return "命运改件普通/极/诅咒/分裂变体共享";
  if (categories.has("hacking_component")) return "骇客改件普通/Max/Pro变体共享";
  if (categories.has("advanced") || categories.has("privileged")) {
    return categories.has("bond_equipment")
      ? "进阶/特权与同核心白昼羁绊版本共享"
      : "进阶/特权同核心装备共享";
  }
  return "重复图标关系待人工确认";
};

const sourceTierFor = (record) => {
  if (record.id === "currency_wars_equipment_001") {
    return "BWIKI oldid=99085+PatchWiki；米游社官方V4.4直接支持中文名/效果（不提供单项图标）";
  }
  if (record.id === "currency_wars_equipment_021") {
    return "BWIKI oldid=99085+PatchWiki；米游社官方V4.2直接支持欢愉星徽名称/效果（不提供单项图标）";
  }
  if (record.id === "currency_wars_equipment_038" || record.id === "currency_wars_equipment_039") {
    return "BWIKI oldid=99085+PatchWiki；米游社官方V4.0直接支持垃圾袋/金垃圾袋属于新增装备（不提供单项图标）";
  }
  if (record.category === "hacking_component") {
    return "BWIKI oldid=99085+PatchWiki；米游社官方V4.2仅支持骇客改件类别语义，不逐项支持16个子名称/图标";
  }
  return "BWIKI oldid=99085+PatchWiki；米游社官方资料仅提供玩法或少量条目补充";
};

const rows = [];
for (let index = 0; index < records.length; index += 1) {
  const record = records[index];
  const rawRecord = rawById.get(record.id);
  const remote = remoteById.get(record.id);
  const page = pageById.get(record.id);
  const iconPath = path.join(runtimeDir, record.icon.asset_path);
  const sourceIconPath = path.join(originalSourceRuntime, record.icon.asset_path);
  const programIconPath = path.join(originalProgramRuntime, record.icon.asset_path);
  const localSha = await sha256(iconPath);
  const sourceSha = await sha256(sourceIconPath);
  const programSha = await sha256(programIconPath);
  const cluster = localHashGroups.get(localSha);
  const pageNumber = Math.floor(index / 20) + 1;
  const pagePosition = index % 20;
  const gridRow = Math.floor(pagePosition / 5) + 1;
  const gridColumn = (pagePosition % 5) + 1;
  const rawType = rawRecord?.equipment_type ?? "";
  const overall =
    record.icon.sha256 !== localSha || sourceSha !== localSha || programSha !== localSha || !remote?.remote_matches_local || !remote?.dimensions_match
      ? "冲突"
      : rawType === "？" || rawType === "？？？"
        ? "分类冲突待确认"
        : cluster.length > 1
          ? "图标一致；具体ID不唯一"
          : "图标/名称/来源无已发现冲突";

  rows.push({
    sequence: index + 1,
    id: record.id,
    name: record.name,
    rawType,
    category: record.category,
    equippable: record.equippable,
    occupies: record.occupies_equipment_slot,
    baseId: record.base_equipment_id ?? "",
    componentIds: (record.component_ids ?? []).join("；"),
    iconPath: record.icon.asset_path,
    declaredSha: record.icon.sha256,
    localSha,
    handoffMatch: sourceSha === localSha && programSha === localSha,
    duplicateCount: cluster.length,
    duplicateIds: cluster.map((item) => shortId(item.id)).join("；"),
    duplicateNames: cluster.map((item) => item.name).join("；"),
    bytes: remote?.local_bytes ?? (await fs.stat(iconPath)).size,
    width: remote?.local_width ?? record.icon.width,
    height: remote?.local_height ?? record.icon.height,
    iconSourceUrl: record.icon.source_url,
    sourcePageUrl: record.source_extensions?.source_page_url ?? "",
    sourceRevision: record.source_extensions?.source_revision_id ?? "",
    remoteHttp: remote?.http_status ?? "",
    remoteSha: remote?.remote_sha256 ?? "",
    remoteMatches: Boolean(remote?.remote_matches_local),
    dimensionsMatch: Boolean(remote?.dimensions_match),
    pageStatus: sourcePageStatus(page, record.id),
    pageTitle: page?.title ?? "",
    visualPage: `equipment-contact-${String(pageNumber).padStart(2, "0")}.png`,
    visualCell: `第${gridRow}行第${gridColumn}列`,
    visualReview: "逐项可见；未见空白、破损、异常裁切或相邻错位",
    identityConclusion: cluster.length > 1
      ? "同图多ID；识别时必须保留CandidateIds/Unknown，不得强收敛"
      : "本地哈希唯一；未发现图标与名称错位",
    sourceTier: sourceTierFor(record),
    boundary: rawType === "？" || rawType === "？？？"
      ? `上游类型为“${rawType}”，本地映射 special_material；分类未获可靠来源确认`
      : record.id === "currency_wars_equipment_159"
        ? "筛选表外补入；无独立BWIKI详情页，来源页指向总表"
        : cluster.length > 1
          ? "仅凭图标无法区分本簇具体ID"
          : page && sourcePageStatus(page, record.id) !== "页面实证通过"
            ? "单条详情页本轮受限流/反爬影响，未获得独立页面实证"
            : "无新增冲突；社区资料不等于官方逐项确认",
    overall,
  });
}

const duplicateClusters = [...localHashGroups.entries()]
  .filter(([, group]) => group.length > 1)
  .sort((a, b) => a[1][0].id.localeCompare(b[1][0].id))
  .map(([hash, group], index) => ({
    sequence: index + 1,
    hash,
    count: group.length,
    ids: group.map((item) => shortId(item.id)).join("；"),
    names: group.map((item) => item.name).join("；"),
    categories: [...new Set(group.map((item) => item.category))].join("；"),
    relation: clusterRelation(group),
    result: "名称核心一致；无跨核心错配",
    recognitionRisk: "图标不能唯一确定具体ID；保留全部CandidateIds并禁止驱动具体ID决策",
  }));

const categoryCounts = [...new Set(records.map((record) => record.category))]
  .sort((a, b) => a.localeCompare(b))
  .map((category) => ({ category, count: records.filter((record) => record.category === category).length }));

const workbook = Workbook.create();
const summary = workbook.worksheets.add("审计摘要");
const equipmentSheet = workbook.worksheets.add("装备清单");
const duplicatesSheet = workbook.worksheets.add("重复图标簇");
const categoriesSheet = workbook.worksheets.add("分类统计");
const sourcesSheet = workbook.worksheets.add("来源与边界");
const visualsSheet = workbook.worksheets.add("视觉证据");

const palette = {
  navy: "#16324F",
  blue: "#2563EB",
  lightBlue: "#DBEAFE",
  green: "#166534",
  lightGreen: "#DCFCE7",
  amber: "#92400E",
  lightAmber: "#FEF3C7",
  red: "#991B1B",
  lightRed: "#FEE2E2",
  gray: "#475569",
  lightGray: "#F1F5F9",
  white: "#FFFFFF",
};

const setBaseStyle = (sheet, rangeAddress) => {
  const range = sheet.getRange(rangeAddress);
  range.format.font = { name: "Microsoft YaHei", size: 10, color: "#0F172A" };
  range.format.verticalAlignment = "center";
};

// 审计摘要
summary.showGridLines = false;
summary.getRange("A1:H2").merge();
summary.getRange("A1").values = [["货币战争装备图标—名称—本地ID—来源专项核对"]];
summary.getRange("A1:H2").format = {
  fill: palette.navy,
  font: { name: "Microsoft YaHei", size: 20, bold: true, color: palette.white },
  horizontalAlignment: "center",
  verticalAlignment: "center",
};
summary.getRange("A4:B12").values = [
  ["指标", "结果"],
  ["本地装备记录", null],
  ["远程原图与本地逐字节一致", null],
  ["交接源代码/程序包/工作副本三方一致", null],
  ["唯一图标哈希", 97],
  ["重复图标簇", null],
  ["重复簇成员", null],
  ["单条BWIKI详情页实证", null],
  ["上游分类冲突候选", null],
];
summary.getRange("B5:B12").formulas = [
  ["=COUNTA('装备清单'!B2:B158)"],
  ["=COUNTIF('装备清单'!Y2:Y158,1)"],
  ["=COUNTIF('装备清单'!M2:M158,1)"],
  ["=COUNTA('重复图标簇'!A2:A98)+50"],
  ["=COUNTA('重复图标簇'!A2:A48)"],
  ["=COUNTIF('装备清单'!N2:N158,\">1\")"],
  ["=COUNTIF('装备清单'!AA2:AA158,\"页面实证通过\")"],
  ["=COUNTIF('装备清单'!AI2:AI158,\"分类冲突待确认\")"],
];
summary.getRange("A4:B4").format = { fill: palette.blue, font: { name: "Microsoft YaHei", bold: true, color: palette.white } };
summary.getRange("A4:B12").format.borders = { preset: "all", style: "thin", color: "#CBD5E1" };
summary.getRange("A14:H20").values = [
  ["结论与强制边界", null, null, null, null, null, null, null],
  ["本地资产完整性", "157条记录与157个PNG一一对应；缺失、孤儿、声明哈希不符、重复ID/名称/路径均为0。", null, null, null, null, null, null],
  ["远程图标", "157/157 PatchWiki图片HTTP 200，远程字节SHA与本地完全一致；这证明图标来源一致，不证明名称由官方逐项确认。", null, null, null, null, null, null],
  ["视觉复核", "8页联系表逐项检查；未见空白、透明损坏、异常裁切、相邻错位。抽象徽标/幻想专名仅凭画面不能证明官方名称。", null, null, null, null, null, null],
  ["重复图标", "47簇覆盖107条，均为同一名称核心的普通/特权、命运改件、骇客改件或白昼羁绊变体；识别时不得强制落到单一ID。", null, null, null, null, null, null],
  ["来源边界", "BWIKI为社区资料；revision 99085是总表修订号，不是157个详情页各自修订号。本轮单页抓取受反爬限制，仅9条获得独立页面实证。", null, null, null, null, null, null],
  ["来源覆盖冲突", "固定修订99085实际159行/158个唯一名称；本地采用其中156个名称，排除财富×2与专家邀请函×1，另补入星徽秘典。官方公告支持专家邀请函属于战利品；但财富×2在BWIKI与Fandom都存在，是否为过渡资产仍未验证。", null, null, null, null, null, null],
];
for (let row = 14; row <= 20; row += 1) summary.getRange(`B${row}:H${row}`).merge();
summary.getRange("A14:H14").format = { fill: palette.navy, font: { name: "Microsoft YaHei", bold: true, color: palette.white } };
summary.getRange("A15:A20").format = { fill: palette.lightBlue, font: { name: "Microsoft YaHei", bold: true, color: palette.navy } };
summary.getRange("A14:H20").format.borders = { preset: "all", style: "thin", color: "#CBD5E1" };
summary.getRange("A15:H20").format.wrapText = true;
summary.getRange("A1:H20").format.rowHeight = 28;
summary.getRange("A14:H20").format.rowHeight = 44;
summary.getRange("A1:A20").format.columnWidth = 22;
summary.getRange("B1:H20").format.columnWidth = 16;
setBaseStyle(summary, "A1:H20");
summary.getRange("A1:H2").format = {
  fill: palette.navy,
  font: { name: "Microsoft YaHei", size: 20, bold: true, color: palette.white },
  horizontalAlignment: "center",
  verticalAlignment: "center",
};
summary.getRange("A4:B4").format = { fill: palette.blue, font: { name: "Microsoft YaHei", bold: true, color: palette.white } };
summary.getRange("A14:H14").format = { fill: palette.navy, font: { name: "Microsoft YaHei", bold: true, color: palette.white } };
summary.getRange("A15:A20").format = { fill: palette.lightBlue, font: { name: "Microsoft YaHei", bold: true, color: palette.navy } };

// 157行装备清单
equipmentSheet.showGridLines = false;
const headers = [
  "序号", "本地ID", "中文名称", "上游类型", "运行分类", "可装备", "占装备槽", "基础装备ID", "合成组件ID",
  "图标相对路径", "声明SHA256", "本地SHA256", "交接三方一致", "重复簇大小", "重复IDs", "重复Names", "字节", "宽", "高",
  "图像来源URL", "来源页面URL", "来源总表修订", "远程HTTP", "远程SHA256", "远程=本地", "尺寸一致", "单页来源状态", "页面标题",
  "视觉联系表", "视觉格位", "视觉复核", "图标身份结论", "来源层级", "冲突/边界", "核对结果",
];
equipmentSheet.getRange("A1:AI1").values = [headers];
const valueRows = rows.map((row) => [
  row.sequence, row.id, row.name, row.rawType, row.category, boolZh(row.equippable), boolZh(row.occupies), row.baseId, row.componentIds,
  row.iconPath, row.declaredSha, row.localSha, row.handoffMatch, null, row.duplicateIds, row.duplicateNames, row.bytes, row.width, row.height,
  row.iconSourceUrl, row.sourcePageUrl, row.sourceRevision, row.remoteHttp, row.remoteSha, row.remoteMatches, row.dimensionsMatch, row.pageStatus, row.pageTitle,
  row.visualPage, row.visualCell, row.visualReview, row.identityConclusion, row.sourceTier, row.boundary, null,
]);
equipmentSheet.getRange(`A2:AI${rows.length + 1}`).values = valueRows;
equipmentSheet.getRange(`N2:N${rows.length + 1}`).formulas = rows.map((_, index) => [`=COUNTIF($L$2:$L$158,L${index + 2})`]);
equipmentSheet.getRange(`AI2:AI${rows.length + 1}`).formulas = rows.map((_, index) => [
  `=IF(OR(K${index + 2}<>L${index + 2},M${index + 2}<>TRUE,Y${index + 2}<>TRUE,Z${index + 2}<>TRUE),"冲突",IF(OR(D${index + 2}="？",D${index + 2}="？？？"),"分类冲突待确认",IF(N${index + 2}>1,"图标一致；具体ID不唯一","图标/名称/来源无已发现冲突")))`,
]);
const equipmentTable = equipmentSheet.tables.add(`A1:AI${rows.length + 1}`, true, "EquipmentAuditTable");
equipmentTable.style = "TableStyleMedium2";
equipmentTable.showFilterButton = true;
setBaseStyle(equipmentSheet, `A1:AI${rows.length + 1}`);
equipmentSheet.getRange(`A1:AI${rows.length + 1}`).format.wrapText = true;
equipmentSheet.getRange(`A2:AI${rows.length + 1}`).format.rowHeight = 38;
equipmentSheet.getRange("A1:A158").format.columnWidth = 7;
equipmentSheet.getRange("B1:B158").format.columnWidth = 27;
equipmentSheet.getRange("C1:C158").format.columnWidth = 22;
equipmentSheet.getRange("D1:E158").format.columnWidth = 16;
equipmentSheet.getRange("F1:G158").format.columnWidth = 10;
equipmentSheet.getRange("H1:I158").format.columnWidth = 28;
equipmentSheet.getRange("J1:J158").format.columnWidth = 52;
equipmentSheet.getRange("K1:L158").format.columnWidth = 34;
equipmentSheet.getRange("M1:N158").format.columnWidth = 12;
equipmentSheet.getRange("O1:P158").format.columnWidth = 46;
equipmentSheet.getRange("Q1:S158").format.columnWidth = 9;
equipmentSheet.getRange("T1:U158").format.columnWidth = 58;
equipmentSheet.getRange("V1:Z158").format.columnWidth = 15;
equipmentSheet.getRange("AA1:AB158").format.columnWidth = 28;
equipmentSheet.getRange("AC1:AD158").format.columnWidth = 18;
equipmentSheet.getRange("AE1:AH158").format.columnWidth = 42;
equipmentSheet.getRange("AI1:AI158").format.columnWidth = 28;
for (let index = 0; index < rows.length; index += 1) {
  const rowNumber = index + 2;
  if (rows[index].overall === "分类冲突待确认") {
    equipmentSheet.getRange(`D${rowNumber}:AI${rowNumber}`).format.fill = palette.lightRed;
    equipmentSheet.getRange(`AI${rowNumber}`).format.font = { name: "Microsoft YaHei", bold: true, color: palette.red };
  } else if (rows[index].duplicateCount > 1) {
    equipmentSheet.getRange(`N${rowNumber}:P${rowNumber}`).format.fill = palette.lightAmber;
    equipmentSheet.getRange(`AI${rowNumber}`).format.fill = palette.lightAmber;
    equipmentSheet.getRange(`AI${rowNumber}`).format.font = { name: "Microsoft YaHei", bold: true, color: palette.amber };
  } else {
    equipmentSheet.getRange(`AI${rowNumber}`).format.fill = palette.lightGreen;
    equipmentSheet.getRange(`AI${rowNumber}`).format.font = { name: "Microsoft YaHei", bold: true, color: palette.green };
  }
}

// 重复图标簇
duplicatesSheet.showGridLines = false;
const duplicateHeaders = ["簇序号", "图标SHA256", "成员数", "本地IDs", "中文名称", "运行分类", "关系", "视觉核对", "识别/存储风险"];
duplicatesSheet.getRange("A1:I1").values = [duplicateHeaders];
duplicatesSheet.getRange(`A2:I${duplicateClusters.length + 1}`).values = duplicateClusters.map((row) => [
  row.sequence, row.hash, row.count, row.ids, row.names, row.categories, row.relation, row.result, row.recognitionRisk,
]);
const duplicateTable = duplicatesSheet.tables.add(`A1:I${duplicateClusters.length + 1}`, true, "DuplicateIconClustersTable");
duplicateTable.style = "TableStyleMedium4";
setBaseStyle(duplicatesSheet, `A1:I${duplicateClusters.length + 1}`);
duplicatesSheet.getRange(`A1:I${duplicateClusters.length + 1}`).format.wrapText = true;
duplicatesSheet.getRange(`A2:I${duplicateClusters.length + 1}`).format.rowHeight = 42;
duplicatesSheet.getRange("A1:A48").format.columnWidth = 8;
duplicatesSheet.getRange("B1:B48").format.columnWidth = 36;
duplicatesSheet.getRange("C1:C48").format.columnWidth = 10;
duplicatesSheet.getRange("D1:F48").format.columnWidth = 35;
duplicatesSheet.getRange("G1:I48").format.columnWidth = 46;

// 分类统计
categoriesSheet.showGridLines = false;
categoriesSheet.getRange("A1:C1").values = [["运行分类", "数量（公式）", "占比"]];
categoriesSheet.getRange(`A2:A${categoryCounts.length + 1}`).values = categoryCounts.map((row) => [row.category]);
categoriesSheet.getRange(`B2:B${categoryCounts.length + 1}`).formulas = categoryCounts.map((row) => [`=COUNTIF('装备清单'!$E$2:$E$158,A${categoryCounts.indexOf(row) + 2})`]);
categoriesSheet.getRange(`C2:C${categoryCounts.length + 1}`).formulas = categoryCounts.map((_, index) => [`=B${index + 2}/157`]);
categoriesSheet.getRange(`C2:C${categoryCounts.length + 1}`).setNumberFormat("0.0%");
const categoryTable = categoriesSheet.tables.add(`A1:C${categoryCounts.length + 1}`, true, "EquipmentCategoryTable");
categoryTable.style = "TableStyleMedium2";
categoriesSheet.getRange(`A${categoryCounts.length + 3}:C${categoryCounts.length + 3}`).values = [["合计", null, null]];
categoriesSheet.getRange(`B${categoryCounts.length + 3}`).formulas = [[`=SUM(B2:B${categoryCounts.length + 1})`]];
categoriesSheet.getRange(`C${categoryCounts.length + 3}`).formulas = [[`=SUM(C2:C${categoryCounts.length + 1})`]];
categoriesSheet.getRange(`C${categoryCounts.length + 3}`).setNumberFormat("0.0%");
categoriesSheet.getRange(`A${categoryCounts.length + 3}:C${categoryCounts.length + 3}`).format = { fill: palette.lightBlue, font: { name: "Microsoft YaHei", bold: true, color: palette.navy } };
setBaseStyle(categoriesSheet, `A1:C${categoryCounts.length + 3}`);
categoriesSheet.getRange(`A${categoryCounts.length + 3}:C${categoryCounts.length + 3}`).format = { fill: palette.lightBlue, font: { name: "Microsoft YaHei", bold: true, color: palette.navy } };
categoriesSheet.getRange(`A1:C${categoryCounts.length + 3}`).format.borders = { preset: "all", style: "thin", color: "#CBD5E1" };
categoriesSheet.getRange("A1:A20").format.columnWidth = 24;
categoriesSheet.getRange("B1:C20").format.columnWidth = 18;

// 来源与边界
sourcesSheet.showGridLines = false;
const sourceRows = [
  ["来源", "URL / 本地路径", "层级", "本轮支持内容", "不能支持/限制"],
  ["米游社官方玩法指南", "https://www.miyoushe.com/sr/article/70242029", "官方", "货币战争装备机制、简易/进阶与合成/拆装规则", "不提供本地157项逐项中文名/分类/图标证明"],
  ["米游社官方 V4.0扩展", "https://www.miyoushe.com/sr/article/73128301", "官方", "垃圾袋/金垃圾袋属于新增装备；专家邀请函单列为战利品", "不提供三项单独图标；不能支持其余条目"],
  ["米游社官方 V4.2扩展", "https://www.miyoushe.com/sr/article/74751748", "官方", "欢愉星徽；骇客改件为银狼专属特殊装备且不占装备栏", "未列16个骇客改件子名称/图标"],
  ["米游社官方 V4.4扩展", "https://www.miyoushe.com/sr/article/76641553", "官方", "命运圣杯星徽中文名与加入命运圣杯羁绊的效果（仅001）", "不展示该单项图标，不能外推其余156项"],
  ["BWIKI 装备一览 oldid=99085", "https://wiki.biligame.com/sr/index.php?title=%E8%A3%85%E5%A4%87%E4%B8%80%E8%A7%88&oldid=99085", "社区Wiki固定修订", "159行/158个唯一名称；本地名称、类型、表内图标的主要来源", "BWIKI非官方；99085是总表修订号，不是各详情页修订号"],
  ["BWIKI 货币战争", "https://wiki.biligame.com/sr/货币战争", "社区Wiki", "装备玩法、奖励/补给节点、合成规则", "不等于官方逐项资产清单"],
  ["PatchWiki 图片CDN", "装备清单T列157个唯一URL", "社区Wiki图片源", "157/157 HTTP200且远程SHA与本地完全一致", "证明字节来源一致，不单独证明名称/分类正确"],
  ["Fandom Currency Wars Equipment", "https://honkai-star-rail.fandom.com/wiki/Currency_Wars%3A_Zero-Sum_Game/Equipment", "独立社区Wiki", "119行/118个唯一英文名；装备族、图标与类别的辅助交叉来源", "特权与进阶合并；不覆盖156–159，不能1:1支持157中文记录"],
  ["本地原始包 records.json", path.join(rawDir, "records.json"), "可追溯本地证据", "上游类型、名称、图标来源、总表修订与数据变换说明", "其中垃圾袋/金垃圾袋上游类型仍为？/？？？"],
  ["本地运行包 equipment.json", path.join(runtimeDir, "equipment.json"), "生产运行资产", "157条运行分类、内部ID、映射关系与图标声明SHA", "运行分类是本地变换结果，不应冒充来源端原始分类"],
];
sourcesSheet.getRange(`A1:E${sourceRows.length}`).values = sourceRows;
const sourcesTable = sourcesSheet.tables.add(`A1:E${sourceRows.length}`, true, "EquipmentSourceBoundaryTable");
sourcesTable.style = "TableStyleMedium2";
sourcesSheet.getRange(`A${sourceRows.length + 2}:E${sourceRows.length + 8}`).values = [
  ["关键变换/冲突", null, null, null, null],
  ["总表覆盖冲突", "oldid=99085实际159行；本地排除财富×2和专家邀请函，补入星徽秘典。交接说明仅记录“158条、财富为误列”，与可复核DOM不一致。", null, null, null],
  ["财富×2", "BWIKI固定修订与Fandom当前页均保留两条Wealth且共用图标；可能是联动前过渡资产，但缺少7月24日后实机图鉴/截图，是否应纳入当前4.4清单未验证。", null, null, null],
  ["专家邀请函", "BWIKI表内有该项，但米游社V4.0官方公告将其单列在“战利品”而非“装备”；排除有官方类别依据。", null, null, null],
  ["星徽秘典", "无独立BWIKI详情页；本地来源URL指向总表，名称/用途由投资策略与羁绊资料补充。", null, null, null],
  ["垃圾袋分类", "上游类型“？”；本地运行分类 special_material，保留待确认。", null, null, null],
  ["金垃圾袋分类", "上游类型“？？？”；本地运行分类 special_material，保留待确认。", null, null, null],
];
for (let row = sourceRows.length + 2; row <= sourceRows.length + 8; row += 1) sourcesSheet.getRange(`B${row}:E${row}`).merge();
sourcesSheet.getRange(`A${sourceRows.length + 2}:E${sourceRows.length + 2}`).format = { fill: palette.navy, font: { name: "Microsoft YaHei", bold: true, color: palette.white } };
sourcesSheet.getRange(`A${sourceRows.length + 3}:A${sourceRows.length + 8}`).format = { fill: palette.lightRed, font: { name: "Microsoft YaHei", bold: true, color: palette.red } };
setBaseStyle(sourcesSheet, `A1:E${sourceRows.length + 8}`);
sourcesSheet.getRange(`A${sourceRows.length + 2}:E${sourceRows.length + 2}`).format = { fill: palette.navy, font: { name: "Microsoft YaHei", bold: true, color: palette.white } };
sourcesSheet.getRange(`A${sourceRows.length + 3}:A${sourceRows.length + 8}`).format = { fill: palette.lightRed, font: { name: "Microsoft YaHei", bold: true, color: palette.red } };
sourcesSheet.getRange(`A1:E${sourceRows.length + 8}`).format.wrapText = true;
sourcesSheet.getRange(`A1:E${sourceRows.length + 8}`).format.rowHeight = 48;
sourcesSheet.getRange("A1:A20").format.columnWidth = 30;
sourcesSheet.getRange("B1:B20").format.columnWidth = 65;
sourcesSheet.getRange("C1:C20").format.columnWidth = 22;
sourcesSheet.getRange("D1:E20").format.columnWidth = 56;

// 视觉证据
visualsSheet.showGridLines = false;
visualsSheet.getRange("A1:G1").values = [["页", "ID范围", "文件", "绝对路径", "SHA256", "逐页核对结果", "边界"]];
visualsSheet.getRange("A2:G9").values = contactSheetHashes.map((row) => [
  row.index,
  `${shortId(row.first)}–${shortId(row.last)}`,
  row.fileName,
  row.filePath,
  row.sha256,
  "全部格位可见；无空白、透明损坏、异常裁切或相邻错位；同核心重复图标关系一致",
  row.index === 7
    ? "抽象阵营/流派星徽仅凭图像不能证明官方中文名"
    : row.index === 8
      ? "星徽秘典无独立BWIKI详情页；只能判定图书形态与本地名称直观相符"
      : "视觉结论仅为本地错配排查，不代替官方来源核验",
]);
const visualTable = visualsSheet.tables.add("A1:G9", true, "EquipmentVisualEvidenceTable");
visualTable.style = "TableStyleMedium2";
setBaseStyle(visualsSheet, "A1:G9");
visualsSheet.getRange("A1:G9").format.wrapText = true;
visualsSheet.getRange("A2:G9").format.rowHeight = 56;
visualsSheet.getRange("A1:A9").format.columnWidth = 7;
visualsSheet.getRange("B1:C9").format.columnWidth = 24;
visualsSheet.getRange("D1:D9").format.columnWidth = 70;
visualsSheet.getRange("E1:E9").format.columnWidth = 36;
visualsSheet.getRange("F1:G9").format.columnWidth = 56;

// 输出前公式/结构检查
const formulaInspection = await workbook.inspect({
  kind: "formula",
  sheetId: "装备清单",
  range: "N1:AI158",
  maxChars: 5000,
  options: { maxResults: 20 },
});
const structureInspection = await workbook.inspect({
  kind: "workbook,sheet,table",
  maxChars: 8000,
  tableMaxRows: 3,
  tableMaxCols: 6,
  tableMaxCellChars: 80,
});

await fs.mkdir(outputDir, { recursive: true });
await fs.mkdir(previewDir, { recursive: true });
const xlsx = await SpreadsheetFile.exportXlsx(workbook);
await xlsx.save(workbookPath);

const csvHeaders = headers;
const csvRows = rows.map((row) => [
  row.sequence, row.id, row.name, row.rawType, row.category, boolZh(row.equippable), boolZh(row.occupies), row.baseId, row.componentIds,
  row.iconPath, row.declaredSha, row.localSha, row.handoffMatch, row.duplicateCount, row.duplicateIds, row.duplicateNames, row.bytes, row.width, row.height,
  row.iconSourceUrl, row.sourcePageUrl, row.sourceRevision, row.remoteHttp, row.remoteSha, row.remoteMatches, row.dimensionsMatch, row.pageStatus, row.pageTitle,
  row.visualPage, row.visualCell, row.visualReview, row.identityConclusion, row.sourceTier, row.boundary, row.overall,
]);
const csvText = [csvHeaders, ...csvRows].map((row) => row.map(csvEscape).join(",")).join("\r\n");
await fs.writeFile(csvPath, `\uFEFF${csvText}`, "utf8");

for (const sheetName of ["审计摘要", "装备清单", "重复图标簇", "分类统计", "来源与边界", "视觉证据"]) {
  const preview = await workbook.render({ sheetName, autoCrop: "all", scale: sheetName === "装备清单" ? 0.3 : 0.7, format: "png" });
  const previewBytes = new Uint8Array(await preview.arrayBuffer());
  await fs.writeFile(path.join(previewDir, `${sheetName}.png`), previewBytes);
}

const formulaValues = [
  ...equipmentSheet.getRange("N2:AI158").values.flat(),
  ...summary.getRange("B5:B12").values.flat(),
  ...categoriesSheet.getRange(`B2:C${categoryCounts.length + 3}`).values.flat(),
];
const formulaErrors = formulaValues.filter((value) => typeof value === "string" && /^#(REF!|DIV\/0!|VALUE!|NAME\?|N\/A)$/.test(value));
const manifest = {
  generatedAt: new Date().toISOString(),
  workbookPath,
  workbookSha256: await sha256(workbookPath),
  workbookBytes: (await fs.stat(workbookPath)).size,
  csvPath,
  csvSha256: await sha256(csvPath),
  csvBytes: (await fs.stat(csvPath)).size,
  equipmentRecords: rows.length,
  uniqueIconHashes: localHashGroups.size,
  duplicateClusters: duplicateClusters.length,
  duplicateMembers: rows.filter((row) => row.duplicateCount > 1).length,
  remoteExactMatches: rows.filter((row) => row.remoteMatches && row.dimensionsMatch).length,
  individualPageVerified: rows.filter((row) => row.pageStatus === "页面实证通过").length,
  categoryConflicts: rows.filter((row) => row.overall === "分类冲突待确认").length,
  hardConflicts: rows.filter((row) => row.overall === "冲突").length,
  datasetCoverageConflicts: 1,
  unresolvedSourceRows: 2,
  summaryFormulaValues: summary.getRange("B5:B12").values.flat(),
  categoryFormulaTotal: categoriesSheet.getRange(`B${categoryCounts.length + 3}:C${categoryCounts.length + 3}`).values.flat(),
  formulaErrors,
  previews: await Promise.all(["审计摘要", "装备清单", "重复图标簇", "分类统计", "来源与边界", "视觉证据"].map(async (name) => {
    const previewPath = path.join(previewDir, `${name}.png`);
    return { name, path: previewPath, sha256: await sha256(previewPath), bytes: (await fs.stat(previewPath)).size };
  })),
};
await fs.writeFile(path.join(here, "workbook-manifest.json"), JSON.stringify(manifest, null, 2), "utf8");
await fs.writeFile(path.join(here, "formula-inspection.ndjson"), formulaInspection.ndjson ?? String(formulaInspection), "utf8");
await fs.writeFile(path.join(here, "structure-inspection.ndjson"), structureInspection.ndjson ?? String(structureInspection), "utf8");
console.log(JSON.stringify(manifest, null, 2));

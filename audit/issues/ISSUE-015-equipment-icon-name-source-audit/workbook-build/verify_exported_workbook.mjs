import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { FileBlob, SpreadsheetFile } from "@oai/artifact-tool";

const here = path.dirname(fileURLToPath(import.meta.url));
const issueDir = path.resolve(here, "..");
const outputDir = path.join(issueDir, "outputs", "019fe1b0-c12f-7373-b6f1-e42697b1805d");
const workbookName = (await fs.readdir(outputDir)).find((name) => name.endsWith(".xlsx"));
if (!workbookName) throw new Error("The exported equipment audit workbook is missing.");

const workbookPath = path.join(outputDir, workbookName);
const workbook = await SpreadsheetFile.importXlsx(await FileBlob.load(workbookPath));
const sheets = [];
for (let index = 0; index < 6; index += 1) sheets.push(workbook.worksheets.getItemAt(index));

const inspections = [
  await workbook.inspect({
    kind: "workbook,sheet,table",
    maxChars: 14000,
    tableMaxRows: 3,
    tableMaxCols: 8,
    tableMaxCellChars: 120,
  }),
  await workbook.inspect({ kind: "region", sheetId: sheets[0].name, range: "A4:B20", maxChars: 6000 }),
  await workbook.inspect({ kind: "region", sheetId: sheets[1].name, range: "A1:AI6", maxChars: 6000 }),
  await workbook.inspect({ kind: "region", sheetId: sheets[1].name, range: "A154:AI158", maxChars: 6000 }),
  await workbook.inspect({ kind: "region", sheetId: sheets[3].name, range: "A1:C17", maxChars: 6000 }),
  await workbook.inspect({ kind: "region", sheetId: sheets[4].name, range: "A1:E12", maxChars: 6000 }),
];

const values = [
  ...sheets[0].getRange("B5:B12").values.flat(),
  ...sheets[1].getRange("N2:AI158").values.flat(),
  ...sheets[3].getRange("B2:C17").values.flat(),
];
const formulaErrors = values.filter((value) => (
  typeof value === "string" && /^#(REF!|DIV\/0!|VALUE!|NAME\?|N\/A)$/.test(value)
));

const result = {
  postExport: true,
  workbookPath,
  sheetNames: sheets.map((sheet) => sheet.name),
  formulaErrors,
};

const outputPath = path.join(here, "post-export-inspection.ndjson");
await fs.writeFile(
  outputPath,
  [...inspections.map((inspection) => inspection.ndjson ?? String(inspection)), JSON.stringify(result)]
    .filter(Boolean)
    .join("\n"),
  "utf8",
);

if (formulaErrors.length > 0) throw new Error(`Exported workbook contains formula errors: ${formulaErrors.join(", ")}`);
console.log(JSON.stringify(result, null, 2));

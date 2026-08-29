from __future__ import annotations

import json
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(r"D:\Codex-2\work\CurrencyWarsAssistant-0.2.839-audit-20260808")
ISSUE = ROOT / "audit" / "issues" / "ISSUE-015-equipment-icon-name-source-audit"
EQUIPMENT_DIR = ROOT / "data" / "runtime" / "1.0.0" / "4.4" / "equipment"
OUTPUT = ISSUE / "visual-local"
FONT_PATH = Path(r"C:\Windows\Fonts\msyh.ttc")

COLS = 5
ROWS = 4
CELL_W = 236
CELL_H = 208
MARGIN = 24
ICON_SIZE = 128


def font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    if FONT_PATH.exists():
        return ImageFont.truetype(str(FONT_PATH), size)
    return ImageFont.load_default()


TITLE_FONT = font(24)
NAME_FONT = font(17)
META_FONT = font(13)


def checkerboard(size: int) -> Image.Image:
    image = Image.new("RGB", (size, size), "#E5E7EB")
    draw = ImageDraw.Draw(image)
    step = 16
    for y in range(0, size, step):
        for x in range(0, size, step):
            if (x // step + y // step) % 2:
                draw.rectangle((x, y, x + step - 1, y + step - 1), fill="#CBD5E1")
    return image


def draw_sheet(records: list[dict], sheet_index: int, total_sheets: int) -> Path:
    width = MARGIN * 2 + COLS * CELL_W
    title_h = 56
    height = MARGIN * 2 + title_h + ROWS * CELL_H
    canvas = Image.new("RGB", (width, height), "#F8FAFC")
    draw = ImageDraw.Draw(canvas)
    draw.text(
        (MARGIN, MARGIN),
        f"货币战争装备本地图标逐项核对 {sheet_index:02d}/{total_sheets:02d}",
        font=TITLE_FONT,
        fill="#0F172A",
    )

    for position, record in enumerate(records):
        row, col = divmod(position, COLS)
        x0 = MARGIN + col * CELL_W
        y0 = MARGIN + title_h + row * CELL_H
        draw.rounded_rectangle(
            (x0 + 5, y0 + 5, x0 + CELL_W - 7, y0 + CELL_H - 7),
            radius=10,
            fill="#FFFFFF",
            outline="#CBD5E1",
            width=1,
        )

        icon_path = EQUIPMENT_DIR / record["icon"]["asset_path"]
        with Image.open(icon_path) as source:
            icon = source.convert("RGBA")
            icon.thumbnail((ICON_SIZE, ICON_SIZE), Image.Resampling.LANCZOS)
            background = checkerboard(ICON_SIZE).convert("RGBA")
            paste_x = (ICON_SIZE - icon.width) // 2
            paste_y = (ICON_SIZE - icon.height) // 2
            background.alpha_composite(icon, (paste_x, paste_y))
            canvas.paste(background.convert("RGB"), (x0 + 12, y0 + 12))

        numeric_id = record["id"].rsplit("_", 1)[-1]
        draw.text(
            (x0 + 150, y0 + 14),
            numeric_id,
            font=TITLE_FONT,
            fill="#1D4ED8",
        )
        draw.multiline_text(
            (x0 + 12, y0 + 146),
            record["name"],
            font=NAME_FONT,
            fill="#111827",
            spacing=2,
        )
        draw.text(
            (x0 + 12, y0 + 181),
            record["category"],
            font=META_FONT,
            fill="#475569",
        )

    path = OUTPUT / f"equipment-contact-{sheet_index:02d}.png"
    canvas.save(path, optimize=True)
    return path


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    payload = json.loads((EQUIPMENT_DIR / "equipment.json").read_text(encoding="utf-8"))
    records = payload["records"]
    page_size = COLS * ROWS
    total_sheets = math.ceil(len(records) / page_size)
    paths = []
    for index in range(total_sheets):
        start = index * page_size
        paths.append(draw_sheet(records[start : start + page_size], index + 1, total_sheets))
    print(json.dumps([str(path) for path in paths], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()

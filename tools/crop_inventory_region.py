#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""裁剪 133224 右侧物品栏区域(SimpleEquipmentInventory)并放大,叠网格列标,供用户实地指认冶金炉位置。"""
import os
from PIL import Image, ImageDraw, ImageFont

SRC = r"C:\\Users\\zzz81\\AppData\\Roaming\\reasonix\\global-workspace\\.reasonix\\attachments\\clipboard-20260819-133224.533687-000006.png"
OUT = r"D:\\CWAFix-20260814\\tools\\diag\\inventory_crop_133224_full.png"
os.makedirs(os.path.dirname(OUT), exist_ok=True)

# SimpleEquipmentInventory = (0.932, 0.105, 0.060, 0.335)
X0, Y0, W, H = 0.932, 0.105, 0.060, 0.335
im = Image.open(SRC).convert("RGB")
dw, dh = im.size
x0, y0 = int(X0 * dw), int(Y0 * dh)
x1, y1 = x0 + int(W * dw), y0 + int(H * dh)
crop = im.crop((x0, y0, x1, y1))
scale = 3  # 物品栏是窄竖条,放大3倍即可看清(153->459宽)
crop = crop.resize((crop.size[0] * scale, crop.size[1] * scale), Image.LANCZOS)
draw = ImageDraw.Draw(crop)
try:
    font = ImageFont.truetype("arialbd.ttf", 30)
except Exception:
    font = ImageFont.load_default()
# 简易网格:每格约宽 scale*40px,按 x 列标（横向1..N列）
c = crop.size[0]
ov = crop.size[1]
col = 0
xx = 0
while xx < c:
    draw.line([xx, 0, xx, ov], fill=(0, 255, 0), width=3)
    draw.text((xx + 6, 6), str(col + 1), fill=(255, 0, 0), font=font)
    draw.line([0, 40 * scale, c, 40 * scale], fill=(255, 255, 0), width=2)
    xx += 40 * scale
    col += 1
crop.save(OUT)
print("saved", OUT, crop.size, "crop_origin", x0, y0, "scale", scale)

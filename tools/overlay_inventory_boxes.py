#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在备战1-7截图(124021)上叠加 InventoryIconSlots 的 7 个识别框并编号,供用户指认冶金炉位置。"""
import os
from PIL import Image, ImageDraw, ImageFont

SRC = r"C:\\Users\\zzz81\\AppData\\Roaming\\reasonix\\global-workspace\\.reasonix\\attachments\\clipboard-20260819-133224.533687-000006.png"
OUT_DIR = r"D:\\CWAFix-20260814\\tools\\diag"
OUT = os.path.join(OUT_DIR, "inventory_boxes_133224_2-6_node.png")
os.makedirs(OUT_DIR, exist_ok=True)

# InventoryIconSlots = EvenSlots(0.940, 0.115, 0.040, 0.071, 7, 0, 0.075)
X0, Y0, W, H = 0.940, 0.115, 0.040, 0.071
PITCH_Y = 0.075
COUNT = 7

im = Image.open(SRC).convert("RGB")
dw, dh = im.size
draw = ImageDraw.Draw(im)
try:
    font = ImageFont.truetype("arialbd.ttf", 28)
    small = ImageFont.truetype("arialbd.ttf", 20)
except Exception:
    font = ImageFont.load_default()
    small = font

# 编号:程序 index=i 显示为 框号 i+1
for i in range(COUNT):
    x0 = int(X0 * dw)
    y0 = int((Y0 + i * PITCH_Y) * dh)
    x1 = int((X0 + W) * dw)
    y1 = int((Y0 + i * PITCH_Y + H) * dh)
    draw.rectangle([x0, y0, x1, y1], outline=(255, 0, 0), width=4)
    # 编号标在框左上,带底色便于看清
    label = str(i + 1)
    tb = draw.textbbox((0, 0), label, font=font)
    tw, th = tb[2] - tb[0], tb[3] - tb[1]
    pad = 4
    draw.rectangle([x0, y0 - th - 2 * pad, x0 + tw + 2 * pad, y0], fill=(255, 0, 0))
    draw.text((x0 + pad, y0 - th - pad), label, fill=(255, 255, 255), font=font)

# 标题
title = "备战2-6 物品栏识别框 InventoryIconSlots (框1-7,对应ItemKind Slot0-6)"
draw.rectangle([8, 6, dw - 8, 40], fill=(0, 0, 0))
draw.text((14, 8), title, fill=(255, 220, 0), font=small)

im.save(OUT)
print("saved:", OUT, im.size)

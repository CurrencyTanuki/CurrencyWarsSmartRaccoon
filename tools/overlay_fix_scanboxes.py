#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在备战2-6(133224)上叠加"修复后"的物品栏扫描框供用户审核:右列(黄)/左列(绿)/第一排横向格(洋红)。
坐标基于现有 InventoryIconSlots(0.940,0.115,0.040,0.071,7,pitch0.075) 扩展。"""
import os
from PIL import Image, ImageDraw, ImageFont

SRC = r"C:\\Users\\zzz81\\AppData\\Roaming\\reasonix\\global-workspace\\.reasonix\\attachments\\clipboard-20260819-133224.533687-000006.png"
OUT = r"D:\\CWAFix-20260814\\tools\\diag\\fix_scanboxes_133224_review.png"
os.makedirs(os.path.dirname(OUT), exist_ok=True)

X0, Y0, W, H = 0.940, 0.115, 0.040, 0.071
PITCH_Y = 0.075
COUNT = 7
# 用户审核修正V2(2026-08-19): 右列整体 下4px/右8px; 左列与右列对齐; 第一排横向格整体往左移一格
ROW_DX, ROW_DY = 8.0, 4.0          # 右列像素位移
TOP_LEFT = 3                        # 第一排横向扩展格数
TOPX_STEP_PX = 0.042 * 2559         # 横向格步进(像素,基准=0.042规格)

im = Image.open(SRC).convert("RGB")
dw, dh = im.size
draw = ImageDraw.Draw(im)
try:
    font = ImageFont.truetype("arialbd.ttf", 26)
    small = ImageFont.truetype("arialbd.ttf", 18)
except Exception:
    font = small = ImageFont.load_default()

# 折算像素基准
cw, ch = W * dw, H * dh                      # 格宽/格高(像素)
rX0 = X0 * dw + ROW_DX                        # 右列起点x(像素,已右移8)
rY0 = Y0 * dh + ROW_DY                        # 右列起点y(像素,已下移4)
pitchY = PITCH_Y * dh                          # 纵向pitch(像素)
px_per_y = lambda yy: int(rY0 + yy * pitchY)

# 第一排横向扩展格(洋红 E): 在右列从第2列(越过L)再往左移一格开始,整体E在R左侧至少2格处
# E0 起点x = rX0 - cw(进入左列L位) - TOPX_STEP_PX(再左移一格,避开L0重合)
e_x0 = rX0 - cw - TOPX_STEP_PX
for i in range(TOP_LEFT):
    ex = e_x0 - i * TOPX_STEP_PX
    x0, y0, x1, y1 = int(ex), int(rY0), int(ex + cw), int(rY0 + ch)
    draw.rectangle([x0, y0, x1, y1], outline=(255, 0, 255), width=4)
    draw.text((x0 + 4, y0 + 4), f"E{i}", fill=(255, 0, 255), font=font)

# 右列(黄 R0-R6): 基准(下4/右8)
for i in range(COUNT):
    x0, y0, x1, y1 = int(rX0), int(rY0 + i * pitchY), int(rX0 + cw), int(rY0 + i * pitchY + ch)
    draw.rectangle([x0, y0, x1, y1], outline=(255, 220, 0), width=3)
    draw.text((x0 + 3, y1 - 26), f"R{i}", fill=(255, 220, 0), font=font)

# 左列(绿 L0-L6): 与右列对齐(横纵都跟R,仅x向左一格)
l_x0 = rX0 - cw
for i in range(COUNT):
    x0, y0, x1, y1 = int(l_x0), int(rY0 + i * pitchY), int(l_x0 + cw), int(rY0 + i * pitchY + ch)
    draw.rectangle([x0, y0, x1, y1], outline=(0, 255, 80), width=3)
    draw.text((x0 + 3, y0 + 3), f"L{i}", fill=(0, 255, 80), font=font)

# 图注
draw.rectangle([8, 6, dw - 8, 66], fill=(0, 0, 0))
draw.text((16, 8), "审核V2: 右列=黄R0-6(下4/右8px修正后)  左列=绿L0-6(与R对齐)  第一排横向=洋红E(已整体左移一格)", fill=(255, 255, 255), font=small)
draw.text((16, 30), "请指认: 冶金炉(橙熔炉)落在哪个框? 黄/绿/洋红? 编号? 框是否对准物品栏?", fill=(255, 220, 0), font=small)
draw.text((16, 50), "PIX: R0=(%.0f,%.0f) 格=%dx%.0f pitchY=%.0f  L列x=%.0f  E0x=%.0f 步进=%.0f" %
          (rX0, rY0, cw, cw, pitchY, l_x0, e_x0, TOPX_STEP_PX), fill=(0, 255, 255), font=small)
im.save(OUT)
print("saved", OUT, im.size)


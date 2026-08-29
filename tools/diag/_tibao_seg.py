#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检测 011854 缇宝装备带非蓝段,定位第1件(追逐星辰)裁剪区域。"""
from PIL import Image

im = Image.open(r"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png").convert("RGB")
bx, by, bw, bh = 1106, 615, 179, 48
# 同程序 LocateEquipmentSegments: 每列非蓝像素数
col = []
for x in range(bw):
    n = 0
    for y in range(bh):
        r, g, b = im.getpixel((bx + x, by + y))
        if not (b > r + 25 and b > g + 10 and b > 70):
            n += 1
    col.append(n)
thr = max(2, bh * 0.30)
segs = []
start = None
for i, n in enumerate(col):
    if n > thr and start is None:
        start = i
    elif n <= thr and start is not None:
        segs.append((bx + start, bx + i, by, bh))
        start = None
if start is not None:
    segs.append((bx + start, bx + bw, by, bh))
print(f"装备带 x=[{bx},{bx+bw}] y=[{by},{by+bh}] w={bw}")
print(f"非蓝段数={len(segs)}")
for s in segs:
    print(f"  seg x=[{s[0]},{s[1]}] 宽={s[1]-s[0]} y=[{s[2]},{s[2]+s[3]}]")
    crop = im.crop((s[0], s[2], s[1], s[2] + s[3]))
    crop.save(rf"D:\CWAFix-20260814\tools\diag\tibao_seg_{s[0]}_{s[1]-s[0]}x{s[3]}.png")

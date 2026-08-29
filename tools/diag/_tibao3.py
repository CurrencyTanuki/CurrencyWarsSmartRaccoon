#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""诊断缇宝3装备: 用列投影检测装备带图标段,看为何只检测到2段。"""
from PIL import Image

im = Image.open(r"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png").convert("RGB")
bx, by, bw, bh = 1106, 615, 179, 48
# 每列非蓝像素数
cols = []
for x in range(bw):
    n = 0
    for y in range(bh):
        r, g, b = im.getpixel((bx + x, by + y))
        isBlue = b > r + 25 and b > g + 10 and b > 70
        if not isBlue:
            n += 1
    cols.append(n)
thr = max(2, bh * 0.30)  # 程序阈值 ~14
print(f"阈值={thr:.0f}")
# 打印每列非蓝数(低于阈值标.)
for i, n in enumerate(cols):
    if n > thr:
        print(f"x={bx+i}: {n}")
# 粗段
segs, start = [], None
for i, n in enumerate(cols):
    act = n > thr
    if act and start is None: start = i
    elif not act and start is not None:
        segs.append((bx+start, bx+i, max(cols[start:i])))
        start = None
if start is not None: segs.append((bx+start, bx+bw, max(cols[start:])))
print(f"粗段(阈值{thr:.0f}): {len(segs)}")
for s in segs: print(f"  [{s[0]},{s[1]}] 宽={s[1]-s[0]} 峰={s[2]}")

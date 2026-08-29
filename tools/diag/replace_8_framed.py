# -*- coding: utf-8 -*-
# 用用户审核过的 framed 8 张完整装备图，原样替换素材库对应 8 个简易装备图
import os, shutil, hashlib

SRC = r"C:/Users/zzz81/WorkBuddy/2026-08-22-07-13-05/currency_war_icons/framed"
ICON = r"D:/CWAFix-20260814/data/runtime/1.0.0/4.4/equipment/assets/currency_wars_equipment_icons"

# 中文名 -> 目标素材库文件名
mapping = {
    "以太钻头": "currency_wars_equipment_040.png",
    "光能电池": "currency_wars_equipment_041.png",
    "和平手枪": "currency_wars_equipment_042.png",
    "幸运星":   "currency_wars_equipment_043.png",
    "折叠小刀": "currency_wars_equipment_044.png",
    "生命之花": "currency_wars_equipment_045.png",
    "轮滑鞋":   "currency_wars_equipment_046.png",
    "量产型装甲": "currency_wars_equipment_047.png",
}

def sha(p):
    return hashlib.sha256(open(p,'rb').read()).hexdigest()[:12]

for cn, fn in mapping.items():
    src_p = os.path.join(SRC, cn + ".png")
    dst_p = os.path.join(ICON, fn)
    if not os.path.exists(src_p):
        print("!! 缺少源图:", cn); continue
    before = sha(dst_p) if os.path.exists(dst_p) else "NONE"
    shutil.copy(src_p, dst_p)
    after = sha(dst_p)
    print(f"{fn:42s} {cn:6s} 前={before} 后={after} {'✓' if before!=after else '(!)未变化'}")

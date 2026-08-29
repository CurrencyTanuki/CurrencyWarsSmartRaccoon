# -*- coding: utf-8 -*-
import os
from PIL import Image
SRC=r"C:/Users/zzz81/WorkBuddy/2026-08-22-07-13-05/currency_war_icons/framed"
DST=r"D:/CWAFix-20260814/data/runtime/1.0.0/4.4/equipment/assets/currency_wars_equipment_icons"
mapping={"以太钻头":"000","光能电池":"001","和平手枪":"002","幸运星":"003","折叠小刀":"004","生命之花":"005","轮滑鞋":"006","量产型装甲":"007"}  # 占位，实际见下面
for f in sorted(os.listdir(SRC)):
    if f.endswith(".png"):
        im=Image.open(os.path.join(SRC,f)); print(f"{f}: {im.size} mode={im.mode}")

# -*- coding: utf-8 -*-
import os, shutil
SRC = r"C:/Users/zzz81/WorkBuddy/2026-08-22-07-13-05/currency_war_icons/framed"
ICON = r"D:/CWAFix-20260814/data/runtime/1.0.0/4.4/equipment/assets/currency_wars_equipment_icons"
mapping = [("040","以太钻头"),("041","光能电池"),("042","和平手枪"),("043","幸运星"),
           ("044","折叠小刀"),("045","生命之花"),("046","轮滑鞋"),("047","量产型装甲")]
for n,cn in mapping:
    s=os.path.join(SRC,cn+".png"); d=os.path.join(ICON,"currency_wars_equipment_"+n+".png")
    shutil.copy(s,d)
    print(n, "replaced", "src="+str(os.path.getsize(s)), "dst="+str(os.path.getsize(d)))
print("DONE")

# -*- coding: utf-8 -*-
import os, hashlib
SRC = r"C:/Users/zzz81/WorkBuddy/2026-08-22-07-13-05/currency_war_icons/framed"
ICON = r"D:/CWAFix-20260814/data/runtime/1.0.0/4.4/equipment/assets/currency_wars_equipment_icons"
mapping = ["以太钻头","光能电池","和平手枪","幸运星","折叠小刀","生命之花","轮滑鞋","量产型装甲"]
nums = ["040","041","042","043","044","045","046","047"]
def sha(p):
    try: return hashlib.sha256(open(p,'rb').read()).hexdigest()[:12]
    except Exception as e: return "ERR:"+str(e)
for cn,n in zip(mapping,nums):
    s=os.path.join(SRC,cn+".png"); d=os.path.join(ICON,"currency_wars_equipment_"+n+".png")
    print(n, "src_sha=", sha(s), "dst_sha=", sha(d), "src_exists=", os.path.exists(s), "src_size=", (os.path.getsize(s) if os.path.exists(s) else '-'))

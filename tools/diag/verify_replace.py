# -*- coding: utf-8 -*-
import os, hashlib
SRC = r"C:/Users/zzz81/WorkBuddy/2026-08-22-07-13-05/currency_war_icons/framed"
ICON = r"D:/CWAFix-20260814/data/runtime/1.0.0/4.4/equipment/assets/currency_wars_equipment_icons"
mapping = {
    "以太钻头":"040","光能电池":"041","和平手枪":"042","幸运星":"043",
    "折叠小刀":"044","生命之花":"045","轮滑鞋":"046","量产型装甲":"047",
}
def sha(p): return hashlib.sha256(open(p,'rb').read()).hexdigest()[:12]
allok = True
for cn, num in mapping.items():
    s = os.path.join(SRC, cn+".png"); d = os.path.join(ICON, "currency_wars_equipment_"+num+".png")
    ok = sha(s)==sha(d)
    allok = allok and ok
    print(f"{num}: 源==目标 {ok}")
print("RESULT:", "ALL OK" if allok else "MISMATCH")

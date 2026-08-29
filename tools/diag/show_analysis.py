# -*- coding: utf-8 -*-
import json
f = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941\analysis-20260822-110518183-54591785dbab4582a9dc06a4ff2c6b7b.json"
d = json.load(open(f, encoding="utf-8-sig"))
print("=== analysis top keys ===")
print(list(d.keys()))
# 找 formation / formationSlots / 卡位细节
def find(o, names, path=""):
    if isinstance(o, dict):
        for k, v in o.items():
            if any(n in k.lower() for n in names):
                print(f"[{path}/{k}] =", json.dumps(v, ensure_ascii=False)[:500])
            else:
                find(v, names, path + "/" + k)
find(d, ["formation", "slot", "front", "back", "bench", "formationslot"])

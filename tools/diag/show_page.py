# -*- coding: utf-8 -*-
import json
f = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941\analysis-20260822-110518183-54591785dbab4582a9dc06a4ff2c6b7b.json"
d = json.load(open(f, encoding="utf-8-sig"))
snap = d.get("snapshot", {})
print("=== snapshot keys ===")
print(list(snap.keys()))
for k in snap:
    if "page" in k.lower() or "route" in k.lower() or "family" in k.lower() or "id" in k.lower():
        print(f"  {k} =", json.dumps(snap[k], ensure_ascii=False)[:300])

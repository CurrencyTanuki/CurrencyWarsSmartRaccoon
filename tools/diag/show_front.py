# -*- coding: utf-8 -*-
import json
f = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941\analysis-20260822-110518183-54591785dbab4582a9dc06a4ff2c6b7b.json"
d = json.load(open(f, encoding="utf-8-sig"))

# 1. operationalState.formation 完整值
print("=== operationalState.formation ===")
fm = d.get("operationalState", {}).get("formation", {})
print("status:", fm.get("status"))
print("value:")
for slot in fm.get("value", []):
    print("  ", slot.get("zone"), "slot", slot.get("slotIndex"), "=",
          slot.get("characterId"), "conf", round(slot.get("confidence", 0), 3),
          "state?", slot.get("standing"), "star", slot.get("starLevel"))
print("uncertainty:", json.dumps(fm.get("uncertainty", []), ensure_ascii=False)[:300])

# 2. formationSlotObservations 完整
print("\n=== formationSlotObservations ===")
for s in d.get("operationalState", {}).get("formationSlotObservations", []):
    print("  ", s.get("zone"), "slot", s.get("slotIndex"), "=", s.get("occupancy"))

# 3. 页面/routeCandidates
print("\n=== routeCandidates ===")
for rc in d.get("routeCandidates", []):
    print("  ", rc)
print("\n=== warnings ===")
for w in d.get("warnings", []):
    print("  ", w)

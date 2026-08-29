# -*- coding: utf-8 -*-
import json, os, collections

base = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941"
ev = os.path.join(base, "events.jsonl")
stat = collections.Counter()   # eventType -> (known count, unknown count, empty value count)
status = collections.Counter()
board_known = []
bench_known = []
for line in open(ev, encoding="utf-8-sig"):
    line = line.strip()
    if not line:
        continue
    try:
        e = json.loads(line)
    except Exception:
        continue
    et = e.get("eventType", "")
    if et not in ("boardObserved", "benchObserved", "lineupObserved", "synergiesObserved", "equipmentObserved"):
        continue
    payload = e.get("payload", {})
    st = payload.get("status", "?")
    status[(et, st)] += 1
    val = payload.get("value", [])
    if et == "boardObserved":
        board_known.append((st, len(val) if isinstance(val, list) else 0))
    if et == "benchObserved":
        bench_known.append((st, len(val) if isinstance(val, list) else 0))

print("=== (事件,状态) 分布 ===")
for k, v in status.most_common():
    print(v, "|", k)

print("\n=== boardObserved (前台) 状态/识别数量 ===")
print("known 条数:", sum(1 for s, _ in board_known if s == "known"))
print("unknown 条数:", sum(1 for s, _ in board_known if s != "known"))
print("known 时平均角色数:", round(sum(n for s, n in board_known if s == "known") / max(1, sum(1 for s, _ in board_known if s == "known")), 2))

print("\n=== benchObserved (备战席) ===")
print("known 条数:", sum(1 for s, _ in bench_known if s == "known"))
print("unknown 条数:", sum(1 for s, _ in bench_known if s != "known"))

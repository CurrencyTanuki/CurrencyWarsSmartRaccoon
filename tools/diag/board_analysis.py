# -*- coding: utf-8 -*-
import json, os, collections

base = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941"
ev = os.path.join(base, "events.jsonl")
cnt = collections.Counter()
details = collections.defaultdict(list)
for line in open(ev, encoding="utf-8-sig"):
    line = line.strip()
    if not line:
        continue
    try:
        e = json.loads(line)
    except Exception:
        continue
    if e.get("eventType") != "boardObserved":
        continue
    payload = e.get("payload", {})
    st = payload.get("status")
    unc = payload.get("uncertainty", [])
    val = payload.get("value", [])
    if st == "known" and len(val) == 0:
        # 前台 known 但空 value —— 判空? unknown?
        detail = "KNOWN-BUT-EMPTY unc=" + json.dumps(unc, ensure_ascii=False)[:150]
        cnt[(st, len(val))] += 1
        details["known-empty"].append((e.get("eventId","")[:30], unc))
    elif st == "unknown":
        cnt[("unknown", len(val))] += 1
        details["unknown"].append((e.get("eventId","")[:30], unc))

print("=== boardObserved 状态/空值 ===")
for k, v in cnt.most_common():
    print(v, "|", k)

print("\n=== known-empty 帧的 uncertainty ===")
for eid, unc in details["known-empty"][:5]:
    print(eid, "unc:", json.dumps(unc, ensure_ascii=False)[:200])

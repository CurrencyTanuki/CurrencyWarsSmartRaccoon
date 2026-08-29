# -*- coding: utf-8 -*-
import json, os

base = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941"
ev = os.path.join(base, "events.jsonl")
known_board = []
for line in open(ev, encoding="utf-8-sig"):
    line = line.strip()
    if not line:
        continue
    try:
        e = json.loads(line)
    except Exception:
        continue
    if e.get("eventType") not in ("boardObserved", "benchObserved"):
        continue
    st = e.get("payload", {}).get("status")
    if st == "known":
        val = e.get("payload", {}).get("value", [])
        print(e.get("eventType"), "ev:", e.get("eventId", "")[:40], "val:", json.dumps(val, ensure_ascii=False)[:400])
        print("----")

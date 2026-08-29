# -*- coding: utf-8 -*-
import json, glob
base = r'C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941'
print("分析文件 | pageId | conf | evidence源")
for f in sorted(glob.glob(base + r'\analysis-*.json')):
    try:
        d = json.load(open(f, encoding='utf-8-sig'))
    except Exception:
        continue
    snap = d.get('snapshot', {})
    page = snap.get('pageId', {})
    pid = page.get('value')
    ev = page.get('evidence', [])
    loc = "; ".join(e.get('locator','') for e in ev) if ev else "NO-EVID"
    print(f"{f.split(chr(92))[-1][:28]} | {pid} | {round(page.get('confidence',0),3)} | {loc}")

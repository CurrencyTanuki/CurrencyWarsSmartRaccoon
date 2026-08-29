# -*- coding: utf-8 -*-
import json, glob
base = r'C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941'
rows = []
for f in sorted(glob.glob(base + r'\analysis-*.json')):
    try:
        d = json.load(open(f, encoding='utf-8-sig'))
    except Exception:
        continue
    snap = d.get('snapshot', {})
    pid = snap.get('pageId', {}).get('value')
    conf = snap.get('pageId', {}).get('confidence', 0)
    src = [e.get('sourceId', '').split('/')[-1] for e in snap.get('pageId', {}).get('evidence', [])]
    board = snap.get('boardCharacterIds', {}).get('value', [])
    shop = snap.get('shopCharacterIds', {}).get('value', [])
    if pid in ('preparation_generic', 'reward_shop'):
        rows.append((pid, conf, src, len(board), len(shop)))
print("pageId | conf | 截图 | board数 | shop数")
for r in rows:
    print(r)

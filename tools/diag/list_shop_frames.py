# -*- coding: utf-8 -*-
import json, glob, os, shutil

base = r'C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941'
shots = os.path.join(base, 'screenshots')
misframes = []  # (疑似帧截图路径, 该帧实际判的页面)

for f in sorted(glob.glob(base + r'\analysis-*.json')):
    try:
        d = json.load(open(f, encoding='utf-8-sig'))
    except Exception:
        continue
    snap = d.get('snapshot', {})
    page = snap.get('pageId', {}).get('value')
    # 从 evidence 找该 analysis 对应的截图
    ev = snap.get('pageId', {}).get('evidence', [])
    src = None
    for e in ev:
        sid = (e.get('sourceId') or '')
        if 'screenshots' in sid:
            src = sid.split('/')[-1]
            break
    if page == 'reward_shop' and src:
        p = os.path.join(shots, src)
        if os.path.exists(p):
            misframes.append(p)

print(f"判为 reward_shop 的帧数: {len(misframes)}")
for p in misframes:
    print(" ", os.path.basename(p))

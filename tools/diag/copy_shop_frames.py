# -*- coding: utf-8 -*-
import json, glob, os, shutil

base = r'C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941'
shots = os.path.join(base, 'screenshots')
dest = r'D:\CWAFix-20260814\tools\diag\shop_misframes'
os.makedirs(dest, exist_ok=True)

copied = []
for f in sorted(glob.glob(base + r'\analysis-*.json')):
    try:
        d = json.load(open(f, encoding='utf-8-sig'))
    except Exception:
        continue
    snap = d.get('snapshot', {})
    page = snap.get('pageId', {}).get('value')
    if page != 'reward_shop':
        continue
    ev = snap.get('pageId', {}).get('evidence', [])
    for e in ev:
        sid = (e.get('sourceId') or '')
        if 'screenshots' in sid:
            src = sid.split('/')[-1]
            sp = os.path.join(shots, src)
            if os.path.exists(sp):
                dp = os.path.join(dest, src)
                shutil.copy(sp, dp)
                copied.append(src)
            break

print(f"已拷贝 {len(copied)} 张到: {dest}")
print("文件列表:")
for c in sorted(set(copied)):
    print("  ", c)

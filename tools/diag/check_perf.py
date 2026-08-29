# -*- coding: utf-8 -*-
import json, glob
fs = sorted(glob.glob(r'C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941\analysis-*.json'))
print(f"analysis 文件数: {len(fs)}")
# 每个文件看是否有计时/诊断
timed = 0
for i, f in enumerate(fs[:40]):
    try:
        d = json.load(open(f, encoding='utf-8-sig'))
    except Exception:
        continue
    diag = d.get('diagnostics')
    snap = d.get('snapshot', {})
    diag2 = snap.get('diagnostics')
    found = False
    for tag, dd in [('top', diag), ('snap', diag2)]:
        if dd:
            s = json.dumps(dd, ensure_ascii=False)
            if any(k in s.lower() for k in ['elapsed', 'millis', 'perf', 'ms']):
                print(f"[{tag}] {f.split(chr(92))[-1][:24]}: {s[:200]}")
                found = True
                timed += 1
    # 检查 snapshot 里的耗时字段散落
    if not found:
        for k, v in snap.items():
            if 'elapsed' in k.lower() or 'millis' in k.lower():
                print(f"[snap.{k}] {f.split(chr(92))[-1][:24]}: {v}")
                found = True
print(f"\n含耗时信息的文件数: {timed}")

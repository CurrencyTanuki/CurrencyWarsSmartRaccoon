import json
d = json.load(open(r"C:/Users/zzz81/AppData/Local/CurrencyWarsSmartRaccoon/runs/run-20260822-185941/nodes/node-1-8-final.json", encoding='utf-8-sig'))
print("=== node-1-8 keys ===")
for k in d.keys():
    v = d[k]
    vs = json.dumps(v, ensure_ascii=False)
    kl = k.lower()
    if any(s in kl for s in ['formation','preparation','snapshot','front','back','bench','character']):
        print(f"[{k}] =", vs[:500])
    else:
        print(f"  {k}: {vs[:100]}")

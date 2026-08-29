import json
p=r'D:\CWAFix-20260814\data\runtime\1.0.0\4.4\equipment\equipment.json'
d=json.load(open(p,encoding='utf-8'))
# 找到装备列表（schema: list）
def walk(o):
    if isinstance(o,list):
        for x in o:
            if isinstance(x,dict) and x.get('id','').startswith('currency_wars_equipment_'):
                yield x
            else:
                yield from walk(x)
    elif isinstance(o,dict):
        for v in o.values():
            yield from walk(v)
for ids in (['currency_wars_equipment_055','currency_wars_equipment_051','currency_wars_equipment_075','currency_wars_equipment_064']):
    found=[e for e in walk(d) if e.get('id')==ids]
    for e in found:
        print(ids[:20], '=', e.get('name'), '|', e.get('category'), '| eff:', str(e.get('effect'))[:60])

import json
d=json.load(open(r'D:\CWAFix-20260814\data\runtime\1.0.0\4.4\equipment\equipment.json',encoding='utf-8'))
seen=set()
def walk(o):
    if isinstance(o,list):
        for x in o:
            yield from walk(x)
    elif isinstance(o,dict):
        if 'id' in o and 'name' in o and o['id'].startswith('currency_wars_equipment_'):
            yield o
        for v in o.values():
            yield from walk(v)
for kw in ('轮滑鞋','财富宝钻','反重力皮靴','光速螺旋桨','胜利之旗','永动机'):
    hits=[e for e in walk(d) if kw in e.get('name','')]
    print(f'== {kw} ==')
    for e in hits: print('  ', e['id'], '=', e['name'])

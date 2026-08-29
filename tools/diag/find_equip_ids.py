# -*- coding: utf-8 -*-
import json
d = json.load(open('data/runtime/1.0.0/4.4/equipment/equipment.json', encoding='utf-8'))
names = ["以太钻头","光能电池","和平手枪","幸运星","折叠小刀","生命之花","轮滑鞋","量产型装甲"]

# 尝试多种结构
def walk(obj, path=""):
    hits = {}
    if isinstance(obj, dict):
        # 检查这个 dict 是否含 name+id
        for k in ("name","Name","EquipmentName","title"):
            if k in obj:
                v = str(obj[k])
                for n in names:
                    if n in v:
                        hits.setdefault(n, []).append((path, obj))
        for k, v in obj.items():
            hits.update(walk(v, path + "." + str(k)))
    elif isinstance(obj, list):
        for i, v in enumerate(obj):
            hits.update(walk(v, path + f"[{i}]"))
    return hits

hits = walk(d)
for n in names:
    items = hits.get(n, [])
    print(f"== {n}: {len(items)} 处")
    for path, obj in items[:3]:
        # 打印该对象里可能的 id 字段
        idv = {k: v for k, v in obj.items() if 'id' in k.lower() or k.lower() in ('code','index','icon','icon_path','asset')}
        print("   path:", path)
        print("   obj keys:", list(obj.keys()))
        print("   id-ish:", idv)

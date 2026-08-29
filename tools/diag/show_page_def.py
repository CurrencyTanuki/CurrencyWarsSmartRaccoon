# -*- coding: utf-8 -*-
import json, os
d = json.load(open(r'D:\CWAFix-20260814\config\page-recognition.1920x1080.json', encoding='utf-8-sig'))
pages = d.get('pages', [])
# 打印一个 reward_shop 和 preparation_generic 的完整定义看模板字段
for p in pages:
    if p.get('id') in ('reward_shop', 'preparation_generic', 'normal_hud'):
        print("===", p.get('id'), "===")
        print(json.dumps(p, ensure_ascii=False)[:800])
        print()
print("config keys:", [k for k in d if k != 'pages'])

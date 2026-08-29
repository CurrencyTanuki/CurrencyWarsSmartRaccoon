# -*- coding: utf-8 -*-
import json
d = json.load(open(r'D:\CWAFix-20260814\config\page-recognition.1920x1080.json', encoding='utf-8-sig'))
pages = d.get('pages', []) if isinstance(d, dict) else d
print("=== 页面配置里所有页面 id ===")
for p in pages:
    pid = p.get('id')
    tpl = p.get('template') or p.get('template_file') or p.get('model') or ''
    print(f"  {pid} | {tpl}")

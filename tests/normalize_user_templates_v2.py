# -*- coding: utf-8 -*-
"""用本轮用户提供的 5 张实机截图重新制作模板（覆盖之前错用的 000013/000014）
本轮 5 张：000015=4费银狼(紫)、000016=5费银狼(金)、000017=开拓者记忆、
000018=开拓者欢愉、000019=彦卿。归一化到 111x127。"""
import os, sys
from PIL import Image

sys.stdout.reconfigure(encoding='utf-8')
BASE = r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\CodexHandoff-20260802\CurrencyWarsSmartRaccoon-CodexHandoff-20260801'
ATT = r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments'
SRC = {
    '4费银狼(紫)': ('clipboard-20260807-082333.506950-000015.png', 'currency_wars_character_05', 'user_4cost'),
    '5费银狼(金)': ('clipboard-20260807-082357.193186-000016.png', 'currency_wars_character_05', 'user_5cost'),
    '开拓者记忆': ('clipboard-20260807-082421.522980-000017.png', 'currency_wars_character_trailblazer', 'user_记忆'),
    '开拓者欢愉': ('clipboard-20260807-082431.315502-000018.png', 'currency_wars_character_trailblazer', 'user_欢愉'),
    '彦卿': ('clipboard-20260807-082503.336031-000019.png', 'currency_wars_character_25', 'user'),
}
TARGET_W, TARGET_H = 111, 127
TARGET_RATIO = TARGET_W / TARGET_H
OUT_DIR = os.path.join(BASE, 'data', '4.4', 'character-card-templates')

# 先删掉之前错误命名的旧 user 模板（用旧图生成的）
for f in os.listdir(OUT_DIR):
    if '__user_' in f or f.endswith('__user.png'):
        p = os.path.join(OUT_DIR, f)
        os.remove(p)
        print('删除旧模板:', f)

for name, (fn, cid, variant) in SRC.items():
    src = os.path.join(ATT, fn)
    im = Image.open(src).convert('RGB')
    w, h = im.size
    if w / h > TARGET_RATIO:
        new_w = int(h * TARGET_RATIO)
        x0 = (w - new_w) // 2
        im = im.crop((x0, 0, x0 + new_w, h))
    else:
        new_h = int(w / TARGET_RATIO)
        y0 = (h - new_h) // 2
        im = im.crop((0, y0, w, y0 + new_h))
    im = im.resize((TARGET_W, TARGET_H), Image.LANCZOS)
    out = os.path.join(OUT_DIR, f'{cid}__{variant}.png')
    im.save(out)
    print(f'{name}: -> {cid}__{variant}.png')
print('完成')

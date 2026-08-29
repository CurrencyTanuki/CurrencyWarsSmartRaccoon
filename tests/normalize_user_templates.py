# -*- coding: utf-8 -*-
"""用户提供的 5 张实机截图 → 大头像模板（111x127，与 character-card-templates 一致）
保留原始截图，归一化产物命名 {id}__{variant}.png 放入模板目录。
归一化：按模板比例 111/127=0.874 中心裁剪（保留角色头肩主体）→ 缩放 111x127。"""
import os, sys
from PIL import Image

sys.stdout.reconfigure(encoding='utf-8')
BASE = r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\CodexHandoff-20260802\CurrencyWarsSmartRaccoon-CodexHandoff-20260801'
ATT = os.path.join(BASE, '.reasonix', 'attachments')  # fallback
SRC = {
    '4费银狼': (r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260807-082333.506950-000015.png',
                'currency_wars_character_05', 'user_4cost'),
    '5费银狼': (r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260807-082357.193186-000016.png',
                'currency_wars_character_05', 'user_5cost'),
    '开拓者记忆': (r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260807-082421.522980-000017.png',
                   'currency_wars_character_trailblazer', 'user_记忆'),
    '开拓者欢愉': (r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260807-082431.315502-000018.png',
                   'currency_wars_character_trailblazer', 'user_欢愉'),
    '彦卿': (r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260807-082503.336031-000019.png',
             'currency_wars_character_25', 'user'),
}

TARGET_W, TARGET_H = 111, 127
TARGET_RATIO = TARGET_W / TARGET_H  # 0.874
OUT_DIR = os.path.join(BASE, 'data', '4.4', 'character-card-templates')

for name, (src, cid, variant) in SRC.items():
    im = Image.open(src).convert('RGB')
    w, h = im.size
    # 中心裁剪到目标比例
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
    print(f'{name}: {src.split(chr(92))[-1]} ({w}x{h}) -> {cid}__{variant}.png (111x127)')
print('完成，原始截图未动，产物已写入模板目录')

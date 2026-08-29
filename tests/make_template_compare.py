# -*- coding: utf-8 -*-
"""生成 模板 vs 实机槽位 对比图（供用户肉眼核对 15号 识别率根因）"""
import os, sys
from PIL import Image, ImageDraw

sys.stdout.reconfigure(encoding='utf-8')
BASE = r'C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\CodexHandoff-20260802\CurrencyWarsSmartRaccoon-CodexHandoff-20260801'
TMPL = os.path.join(BASE, 'data', '4.4', 'character-card-templates')
SMALL = os.path.join(BASE, 'data', '4.4', 'character-small-avatar-templates')
FRAME = os.path.join(BASE, 'tests', 'CurrencyWarsAssistant.Tests', 'Fixtures', 'PageReplay', 'prep_3_7_142723217.png')

# 3-7 帧已知角色（从诊断测试）：Front#1=character_55, Front#2=character_07, Front#3=character_09, Back#5=character_02
# 失败槽位 Front#0 = 银狼(character_05) conf 0.294
slots = {
    'Front#0(失败-银狼)': (681, 329, 128, 140, 'currency_wars_character_05'),
    'Front#1(成功)': (827, 329, 122, 140, 'currency_wars_character_55'),
    'Front#2(成功)': (972, 329, 120, 140, 'currency_wars_character_07'),
    'Back#5(成功)': (535, 600, 140, 145, 'currency_wars_character_02'),
}

img = Image.open(FRAME)
sx = img.width / 1920
sy = img.height / 1080

cells = []
for name, (x, y, w, h, cid) in slots.items():
    # 实机槽位裁剪
    box = (int(x*sx), int(y*sy), int((x+w)*sx), int((y+h)*sy))
    crop = img.crop(box)
    # 大头像模板
    tmpl = None
    tpath = os.path.join(TMPL, f'{cid}__default.png')
    if os.path.exists(tpath):
        tmpl = Image.open(tpath).convert('RGB')
    # 小头像模板
    small = None
    spath = os.path.join(SMALL, f'{cid}__default.png')
    if os.path.exists(spath):
        small = Image.open(spath).convert('RGB')
    cells.append((name, cid, crop, tmpl, small))

# 拼图：每行 = 实机槽位 | 大头像模板 | 小头像模板
cell_w = 220
cell_h = 240
n = len(cells)
canvas = Image.new('RGB', (cell_w * 3, cell_h * n + 30), (24, 28, 40))
draw = ImageDraw.Draw(canvas)

# 列头
draw.text((10, 8), '实机槽位', fill=(255, 220, 100))
draw.text((cell_w + 10, 8), '大头像模板(正方形)', fill=(255, 220, 100))
draw.text((cell_w * 2 + 10, 8), '小头像模板(圆形)', fill=(255, 220, 100))

for i, (name, cid, crop, tmpl, small) in enumerate(cells):
    y0 = 30 + i * cell_h
    draw.text((10, y0), f'{name}  {cid}', fill=(180, 200, 230))
    y0 += 20
    # 实机槽位
    c = crop.copy()
    c.thumbnail((cell_w - 40, cell_h - 40), Image.LANCZOS)
    canvas.paste(c, (20, y0 + (cell_h - 60 - c.height) // 2))
    # 大头像
    if tmpl:
        t = tmpl.copy()
        t.thumbnail((cell_w - 40, cell_h - 40), Image.LANCZOS)
        canvas.paste(t, (cell_w + 20, y0 + (cell_h - 60 - t.height) // 2))
    else:
        draw.text((cell_w + 20, y0 + 40), '无模板', fill=(255, 100, 100))
    # 小头像
    if small:
        s = small.copy()
        s.thumbnail((cell_w - 40, cell_h - 40), Image.LANCZOS)
        canvas.paste(s, (cell_w * 2 + 20, y0 + (cell_h - 60 - s.height) // 2))
    else:
        draw.text((cell_w * 2 + 20, y0 + 40), '无模板', fill=(255, 100, 100))

out = os.path.join(os.environ['TEMP'], 'template_vs_live_37.png')
canvas.save(out)
print('已生成:', out, canvas.size)

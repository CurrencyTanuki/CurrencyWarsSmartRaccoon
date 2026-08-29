# -*- coding: utf-8 -*-
"""据软件真实坐标画 角色卡框 + 装备识别框。（后台固定按 7 格=七人口）
坐标系说明：
- 角色槽(1920系, pixel, x,y,w,h)：
    前台 = PreparationCharacterSlots1920[0..3]
    后台 = BackCharacterSlots1920(7)  → x=960+(offset)-65, y=600, w=130, h=145
            offsets = -435,-290,-145,0,145,290,435
- A 套装备(固定三槽)：
    xStart=卡X+卡W*starts; yCenter=卡Y+卡H*1.08; slotW=0.26*卡W; slotH=0.26*卡H
    Front starts=[0.03,0.39,0.72]; Back starts=[0.03,0.34,0.68]
- B 套装备(BuildEquipmentSlotsByCount)：
    count3: Front=[0.03,0.39,0.75] Back=[0.03,0.34,0.68]; slotW=0.33卡W; slotH=0.26卡H; yCenter=+1.08
    count2: 卡中心 ±21px; slotW=0.30卡W; slotH=0.26卡H; yCenter=+1.08
    count1: 卡中心; slotW=0.33卡W; slotH=0.26卡H
绘制：
- 角色卡框 = 红框(整卡)
- 装备框 = 红框(装备槽)，用与识别一致的两条平行竖边即可
"""
import os
from PIL import Image, ImageDraw

OUT = os.path.dirname(os.path.abspath(__file__))

# 两张底图(绝对路径)
IMGS = {
    "125924": r"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-2026-07-28\125924.png",
    "31": r"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260821-080042.753049-000001.png",
}

# ---- 角色槽 (1920 系 pixel) ----
FRONT = [
    (681, 329, 128, 140),
    (827, 329, 122, 140),
    (972, 329, 120, 140),
    (1114, 329, 120, 140),
]
# 后台格 = 6 + (人口 - 商店Lv)（软件 DeriveBackSlotCount 真实决策，2026-08-15 用户拍板）。
# 每张图用软件真实识别出的 backSlotCount：125924 -> 7格，3-1 -> 6格。
BACK_OFFSETS = {
    6: [-362, -217, -72, 72, 217, 362],
    7: [-435, -290, -145, 0, 145, 290, 435],
    8: [-507, -362, -217, -72, 72, 217, 362, 507],
    9: [-580, -435, -290, -145, 0, 145, 290, 435, 580],
}
# 每张底图软件真实后台格数（由 ClipboardFrameProbe 实测 StoreLevel/Population 推算）
IMG_BACK_COUNT = {
    "125924": 7,   # StoreLevel=5, Population=6  -> 6+(6-5)=7
    "31": 6,       # StoreLevel=8, Population=8  -> 6+(8-8)=6
}
def back_slots(count):
    offs = BACK_OFFSETS.get(count, BACK_OFFSETS[6])
    return [(960 + o - 65, 600, 130, 145) for o in offs]

def eq_slots(kind, count, zone, card):
    """kind: 'A' 固定三槽 / 'B' 按数量布局。返回 [(x,y,w,h) 1920系 pixel]"""
    cx, cy, cw, ch = card
    yc = cy + ch * 1.08
    sh = ch * 0.26
    if kind == "A":
        starts = [0.03, 0.34, 0.68] if zone == "Back" else [0.03, 0.39, 0.72]
        sw = cw * 0.26
        return [(cx + cw * f, yc - sh / 2, sw, sh) for f in starts]
    # B
    if count >= 3:
        starts = [0.03, 0.34, 0.68] if zone == "Back" else [0.03, 0.39, 0.75]
        sw = cw * 0.33
        return [(cx + cw * f, yc - sh / 2, sw, sh) for f in starts]
    if count == 1:
        sw = cw * 0.33
        return [(cx + cw / 2 - sw / 2, yc - sh / 2, sw, sh)]
    if count == 2:
        sw = cw * 0.30
        cc = cx + cw / 2
        return [(cc - 21 - sw / 2, yc - sh / 2, sw, sh), (cc + 21 - sw / 2, yc - sh / 2, sw, sh)]
    return []

def scale(b, W, H):
    """1920 系 pixel -> 图像实际 pixel"""
    return (b[0] * W / 1920, b[1] * H / 1080, b[2] * W / 1920, b[3] * H / 1080)

def draw(img_path, slots_red, out_path, W=None, H=None):
    im = Image.open(img_path).convert("RGB")
    if W is None or H is None:
        W, H = im.size
    d = ImageDraw.Draw(im)
    for sl in slots_red:
        x, y, w, h = scale(sl, W, H)
        d.rectangle([x, y, x + w, y + h], outline=(255, 0, 0), width=3)
    im.save(out_path)
    print("saved", out_path)

for name, p in IMGS.items():
    im = Image.open(p)
    W, H = im.size
    print(name, "orig", W, H, "backSlotCount=", IMG_BACK_COUNT[name])

    back = back_slots(IMG_BACK_COUNT[name])
    front_set = set(FRONT)
    back_set = set(back)
    slots = FRONT + back

    # 角色卡框：前台 + 后台(真实格数)
    draw(p, slots, os.path.join(OUT, f"char_{name}.png"), W, H)

    # 装备框: A/B x mode(2/3) 。B mode=2 用 count2, mode=3 用 count3。A 恒三槽。
    for kind in ("A", "B"):
        for mode in (3, 2):
            boxes = []
            for card in slots:
                zone = "Back" if card in back_set else "Front"
                cnt = mode if kind == "B" else 3
                boxes += eq_slots(kind, cnt, zone, card)
            draw(p, boxes, os.path.join(OUT, f"{kind}_{name}_mode{mode}.png"), W, H)

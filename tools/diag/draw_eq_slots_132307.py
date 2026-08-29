# -*- coding: utf-8 -*-
# 弹识别器实际用的装备槽（用户标定 3 件套四角，1920 参考系）到 132307 帧
import cv2, numpy as np, sys, os

IMG = r"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-2026-07-28\132307.png"
OUT_DIR = r"D:\CWAFix-20260814\tools\diag"
os.makedirs(OUT_DIR, exist_ok=True)

im = cv2.imread(IMG)
H, W = im.shape[:2]
sx, sy = W/1920.0, H/1080.0

# 前台 3 件套（FrontEquip[3]：4 卡，每卡 3 槽四角）
front3 = [
    [[680,463],[722,463],[765,463]],          # card0 阿格莱雅 3 槽 x 起点
    [[826,463],[869,463],[911,463]],
    [[972,463],[1015,463],[1057,463]],
    [[1118,463],[1161,463],[1203,463]],
]
# 每槽宽度 ~38(参考系)，高度 ~37；槽顶 y 463 底 500
def draw_slots(im_draw, cards, color, label, txt_sy=0):
    for ci, card in enumerate(cards):
        for si, xs in enumerate(card):
            x0 = xs[0]*sx; x1 = (xs[0]+38)*sx
            y0 = 463*sy;   y1 = 500*sy
            cv2.rectangle(im_draw, (int(x0),int(y0)), (int(x1),int(y1)), color, 2)
            cv2.putText(im_draw, f"{label}{ci}-{si}", (int(x0),int(y0-6)),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)

draw_slots(im, front3, (0,0,255), "F")

# 后台 3 件套 BackEquip[(3,7)]：card1(千冶刃 slot5=card1)。坐标待确认后单独画，
# 这里先只画前台（数据确定）。

full = os.path.join(OUT_DIR, "eq_slots_132307_front.png")
cv2.imwrite(full, im)

# 放大前台阿格莱雅 3 槽区（含 046 漏的 slot1）
crop = im.copy()
x0,y0,x1,y1 = int(600*sx-30), int(430*sy-30), int(900*sx+30), int(620*sy+30)
crop = crop[y0:y1, x0:x1]
crop = cv2.resize(crop, None, fx=2.8, fy=2.8, interpolation=cv2.INTER_LINEAR)
zoom = os.path.join(OUT_DIR, "eq_slots_132307_aglaia.png")
cv2.imwrite(zoom, crop)

print("WROTE", full)
print("WROTE", zoom)

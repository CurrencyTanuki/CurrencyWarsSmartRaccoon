# -*- coding: utf-8 -*-
# 严格对齐 Phase2OperationalScreenshotAnalyzer 的 calibAll 识别候选槽：
# FrontEquip[1]/[2]/[3] 该卡全部四角 → NormalizedRect(轴对齐) → ToPixels，
# 每个框都经 dHash 打分。前台四卡逐一画；颜色 1件=黄 2件=绿 3件=红。
import cv2, numpy as np, os

IMG = r"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-2026-07-28\132307.png"
OUT_DIR = r"D:\CWAFix-20260814\tools\diag"
os.makedirs(OUT_DIR, exist_ok=True)
im = cv2.imread(IMG)
H, W = im.shape[:2]
sy = H / 1080.0  # 注意：脚本用 2559 帧；四角 y 参考系 1080

def scaled(x, y): return (x / 1920.0 * W, y / 1080.0 * H)

# FrontEquip[ec][card] = list[slot] = 四个角 [x1,y1,x2,y1,x3,y2,x4,y2]
# 来自 CalibrationSlots.cs 65-85 节（card0-阿格莱雅）
front = {
    1: {
        0: [[[722,463],[760,463],[720,500],[758,500]]],
    },
    2: {
        0: [[[701,463],[739,463],[698,500],[736,500]],
            [[743,463],[781,463],[742,500],[779,500]]],
    },
    3: {
        0: [[[680,463],[717,463],[677,500],[715,500]],
            [[722,463],[760,463],[720,500],[758,500]],
            [[765,463],[803,463],[763,500],[801,500]]],
    },
}
colors = {1: (0, 255, 255), 2: (0, 200, 0), 3: (0, 0, 255)}
labels = {1: "1件", 2: "2件", 3: "3件"}

def draw(card):
    for ec in (1, 2, 3):
        slots = front[ec].get(card, [])
        for si, q in enumerate(slots):
            xs = [p[0] for p in q]; ys = [p[1] for p in q]
            x0, y0 = scaled(min(xs), min(ys))
            x1, y1 = scaled(max(xs), max(ys))
            cv2.rectangle(im, (int(x0), int(y0)), (int(x1), int(y1)), colors[ec], 2)
            cv2.putText(im, f"c{card} e{ec}s{si}", (int(x0), int(y0 - 6)),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, colors[ec], 2)

for c in (0,):   # 阿格莱雅 card0（本帧识别关键）
    draw(c)

full = os.path.join(OUT_DIR, "eq_slots_calibAll_aligned.png")
cv2.imwrite(full, im)

# 放大阿格莱雅槽区
crop = im[int(430*sy-40):int(615*sy+20), int(620*W/1920-40):int(880*W/1920+40)].copy()
crop = cv2.resize(crop, None, fx=2.8, fy=2.8, interpolation=cv2.INTER_LINEAR)
zoom = os.path.join(OUT_DIR, "eq_slots_calibAll_aglaia_zoom.png")
cv2.imwrite(zoom, crop)
print("WROTE", full)
print("WROTE", zoom)
print("图例: 黄=1件套 绿=2件套 红=3件套（=软件 calibAll 实际逐个 dHash 的候选框）")

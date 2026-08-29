# -*- coding: utf-8 -*-
# 模拟模板匹配：对比 PREP(0.44) 帧和 NONE(0.155) 帧的 prep_label 区域，
# 在『原始Canny边缘』 vs 『对比度增强后Canny边缘』下的匹配分数，评估能否增强。
import cv2, numpy as np

TPL = r"D:\CWAFix-20260814\config\templates\1920x1080\pages\preparation-stage-label.png"
BASE = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941\screenshots"
reg = (0.18, 0.00, 0.14, 0.08)  # x,y,w,h 归一化 (1920x1080 参考系)

def match_score(frame_path, tpl, reg):
    img = cv2.imread(frame_path)
    # 缩放到 1920x1080
    img = cv2.resize(img, (1920, 1080), interpolation=cv2.INTER_AREA)
    sx = int(reg[0]*1920); sy = int(reg[1]*1080)
    sw = int(reg[2]*1920); sh = int(reg[3]*1080)
    search = img[sy:sy+sh, sx:sx+sw]
    # 模板
    t = cv2.imread(tpl, cv2.IMREAD_GRAYSCALE)
    sg = cv2.cvtColor(search, cv2.COLOR_BGR2GRAY)
    results = []
    # 1. 原始 Canny 边缘匹配
    e_s = cv2.Canny(sg, 80, 180); e_t = cv2.Canny(t, 80, 180)
    r = cv2.matchTemplate(e_s, e_t, cv2.TM_CCOEFF_NORMED)
    results.append(("orig-edge", float(r.max())))
    # 2. 对比度增强(CLAHE)后 Canny
    clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8,8))
    s_clahe = clahe.apply(sg)
    e_s2 = cv2.Canny(s_clahe, 80, 180)
    r2 = cv2.matchTemplate(e_s2, e_t, cv2.TM_CCOEFF_NORMED)
    results.append(("clahe-edge", float(r2.max())))
    # 3. 原始灰度
    r3 = cv2.matchTemplate(sg, t, cv2.TM_CCOEFF_NORMED)
    results.append(("orig-gray", float(r3.max())))
    # 4. 高斯模糊降噪后灰度（对低对比文字可能提对比）
    s_blur = cv2.GaussianBlur(sg, (0,0), 1.5)
    s_sharp = cv2.addWeighted(sg, 1.5, s_blur, -0.5, 0)
    r4 = cv2.matchTemplate(s_sharp, t, cv2.TM_CCOEFF_NORMED)
    results.append(("sharpen-gray", float(r4.max())))
    return results

for tag, fn in [("PREP","20260822-110546337.png"),("NONE","20260822-110518183.png"),("NONE2","20260822-111806575.png")]:
    print(f"=== {tag} {fn} ===")
    for name, sc in match_score(BASE+"\\"+fn, TPL, reg):
        print(f"   {name}: {sc:.3f}")

# -*- coding: utf-8 -*-
# 裁剪两张备战帧的 preparation_stage_label 区域（searchRegion x0.18 y0 w0.14 h0.08），
# 一张判 prep(0.44)、一张判 NONE(0.155)，对比内容差异。
import cv2, os

base = r"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs\run-20260822-185941\screenshots"
out = r"D:\CWAFix-20260814\tools\diag"
os.makedirs(out, exist_ok=True)

# searchRegion: x=0.18 y=0 w=0.14 h=0.08 (归一化)
reg = (0.18, 0.0, 0.14, 0.08)
def crop_save(fn, tag):
    p = os.path.join(base, fn)
    im = cv2.imread(p)
    H, W = im.shape[:2]
    sx = int(reg[0]*W); sy = int(reg[1]*H)
    sw = int(reg[2]*W); sh = int(reg[3]*H)
    crop = im[sy:sy+sh, sx:sx+sw]
    cv2.imwrite(os.path.join(out, f"preplabel_{tag}_{fn}"), crop)
    # 放大 3x 便于查看
    big = cv2.resize(crop, None, fx=3, fy=3, interpolation=cv2.INTER_LINEAR)
    cv2.imwrite(os.path.join(out, f"preplabel_big_{tag}_{fn}"), big)
    print(f"preplabel {tag}: {fn} 保存 {crop.shape[1]}x{crop.shape[0]} W={W} H={H}")

# NONE 帧 (prep_label conf ~0.155)
crop_save("20260822-110518183.png", "NONE")
crop_save("20260822-111806575.png", "NONE2")
# prep 帧 (conf ~0.44)
crop_save("20260822-110546337.png", "PREP")
crop_save("20260822-111143808.png", "PREP2")

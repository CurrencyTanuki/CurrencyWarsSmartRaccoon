# -*- coding: utf-8 -*-
# 132307 阿格莱雅第二格装备槽（轮滑鞋该在的位置）
# 用用户手标四角做「正交投影」拉成纯正方形，装备数据即图片边缘（不画红框）。
import cv2, numpy as np, os

IMG = r"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-2026-07-28\132307.png"
OUT_ZOOM = r"D:\CWAFix-20260814\tools\diag\132307_aglaia_slot1_warp_square.png"

im = cv2.imread(IMG)
H, W = im.shape[:2]
sx, sy = W / 1920.0, H / 1080.0

# 用户手标 FrontEquip[3] card0(阿格莱雅) slot1 四角（1920x1080 参考系）
# 顺序 = TL, TR, BL, BR
quad = [[722, 463], [760, 463], [720, 500], [758, 500]]
src = np.array([[p[0] * sx, p[1] * sy] for p in quad], dtype=np.float32)

# 正交投影：把斜四角单应拉成正方形。边长取四角在帧中的平均边长。
# 源四角是斜视梯形（顶边斜、底边斜、侧边不垂直），单应可精确映射到正矩形/正方形。
side_px = max(
    np.hypot(*(src[1] - src[0])),   # 顶边
    np.hypot(*(src[3] - src[2])),   # 底边
    np.hypot(*(src[2] - src[0])),   # 左边
    np.hypot(*(src[3] - src[1])),   # 右边
)
side = int(round(side_px * 10))     # 10x 放大（高清），保持比例 = 纯正方形
dst = np.array([[0, 0], [side - 1, 0], [0, side - 1], [side - 1, side - 1]], dtype=np.float32)
Hm = cv2.getPerspectiveTransform(src, dst)
warped = cv2.warpPerspective(im, Hm, (side, side), flags=cv2.INTER_LINEAR,
                             borderMode=cv2.BORDER_REPLICATE)

cv2.imwrite(OUT_ZOOM, warped)
print("src px quad TL/TR/BL/BR =", src.astype(int).tolist())
print("warp dst = pure square side", side, "px (10x zoom)")
print("WROTE", OUT_ZOOM)

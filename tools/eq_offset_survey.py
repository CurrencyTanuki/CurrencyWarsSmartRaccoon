"""用非蓝前景几何法量化 前台/后台 各角色装备槽相对固定槽位的水平偏移。
原理：装备图标是非蓝彩色像素；在槽的y带内找非蓝连通域，其质心x 与 固定槽中心x 的差 = 偏移量。
"""
import cv2, numpy as np, glob, os

slots1920_front = [(681,329,128,140),(827,329,122,140),(972,329,120,140),(1114,329,120,140)]
back6 = [(960-362,600,130,145),(960-217,600,130,145),(960-72,600,130,145),
         (960+72,600,130,145),(960+217,600,130,145),(960+362,600,130,145)]

def eq_centroid(img, xs, ycen, sw, band_top, band_bot):
    """在 [band_top,band_bot)-1 高度、[xs-?, xs+?] 宽度找最大非蓝连通域质心；返回(cx, area)"""
    H,W=img.shape[:2]
    y0=max(0,int(ycen-16)); y1=min(H,int(ycen+16))
    x0=max(0,int(xs-40)); x1=min(W,int(xs+40))
    sub=img[y0:y1, x0:x1].astype(np.int16)
    B=sub[:,:,0]; G=sub[:,:,1]; R=sub[:,:,2]
    # 非蓝: B 不显著大于 R/G
    blue=(B > R+25) & (B > G+10) & (B>70)
    fg=(~blue).astype(np.uint8)
    n,lab,stats,cent=cv2.connectedComponentsWithStats(fg,8)
    best=(-1,None)
    for i in range(1,n):
        a=int(stats[i, cv2.CC_STAT_AREA])
        if a>20 and a>best[0]:
            best=(a,(int(cent[i,0])+x0, int(cent[i,1])+y0))
    return best

def scan_zone(img, refs, zone, fsx):
    H,W=img.shape[:2]; sx=W/1920.0; sy=H/1080.0
    out=[]
    for i,ref in enumerate(refs):
        RX,RY,RW,RH=ref
        for fn,f in fsx.items():
            xs=(RX+RW*f)*sx; yc=(RY+RH*1.08)*sy
            c=(int(round((RX+RW/2)*sx)), int(round(yc)))
            a,cc=eq_centroid(img, xs, yc, RW*0.26*sx, yc-18, yc+18)
            if cc:
                off=cc[0]-int(round(xs))
                out.append((f"{zone}slot{i}_{fn}",off,a))
            else:
                out.append((f"{zone}slot{i}_{fn}",None,0))
    return out

for fp in sorted(glob.glob("tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-2026-07-28/*.png")):
    img=cv2.imread(fp); H,W=img.shape[:2]
    print(f"\n=== {os.path.basename(fp)} {W}x{H} ===")
    resf=scan_zone(img,slots1920_front,"前",{0:0.03,1:0.39,2:0.72})
    resb=scan_zone(img,back6,"后",{0:0.03,1:0.34,2:0.68})
    print("前台: "+", ".join(f"{n}:{('+'if o and o>=0 else '')}{o}" if o is not None else f"{n}:nil" for n,o,a in resf))
    print("后台: "+", ".join(f"{n}:{('+'if o and o>=0 else '')}{o}" if o is not None else f"{n}:nil" for n,o,a in resb))

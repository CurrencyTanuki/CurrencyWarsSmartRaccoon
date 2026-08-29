"""画装备识别红框图：前台+后台，用软件当前固定坐标为基准画红框。
4张：6槽×3件、6槽×2件、7槽×3件、7槽×2件
"""
import cv2, numpy as np, os

slots1920_front = [(681,329,128,140),(827,329,122,140),(972,329,120,140),(1114,329,120,140)]
def back_slots(n):
    # 后台等距对称 960 ± i*145，宽130 高145 y=600 (1920系)
    c=960; slotw=130; sloth=145; half=145
    if n==6: offs=[-362,-217,-72,72,217,362]
    elif n==7: offs=[-435,-290,-145,0,145,290,435]
    elif n==8: offs=[-507,-362,-217,-72,72,217,362,507]
    elif n==9: offs=[-580,-435,-290,-145,0,145,290,435,580]
    else: offs=[]
    return [(c+o,600,slotw,sloth) for o in offs]

def to_pixels(ref, sx, sy):
    return (int(round(ref[0]*sx)), int(round(ref[1]*sy)), int(round(ref[2]*sx)), int(round(ref[3]*sy)))

def equip_frames_front(refPx, mode):
    RX,RY,RW,RH=refPx
    yc=RY+RH*1.08; slotH=RH*0.26
    if mode==3:  # 固定3槽 0.03/0.39/0.72 槽宽0.26
        fs=[0.03,0.39,0.72]
        return [(RX+RW*f, yc, RW*0.26, slotH) for f in fs]
    elif mode==2:  # 卡中心±21px 槽宽0.30
        cx=RX+RW/2.0
        return [(cx-21, yc, RW*0.30, slotH),(cx+21, yc, RW*0.30, slotH)]

def equip_frames_back(refPx, mode):
    RX,RY,RW,RH=refPx
    yc=RY+RH*1.08; slotH=RH*0.26
    if mode==3:
        fs=[0.03,0.34,0.68]
        return [(RX+RW*f, yc, RW*0.26, slotH) for f in fs]
    elif mode==2:
        cx=RX+RW/2.0
        return [(cx-21, yc, RW*0.30, slotH),(cx+21, yc, RW*0.30, slotH)]

def draw(img, frames, color=(0,0,255), label=""):
    for (x0,y0,w,h),i in zip(frames, range(len(frames))):
        xl=int(x0-w/2); yt=int(y0-h/2); xr=int(x0+w/2); yb=int(y0+h/2)
        cv2.rectangle(img,(xl,max(0,yt)),(xr,yb),color,3)
        cv2.putText(img,str(i+1),(xl,max(8,yt+20)),cv2.FONT_HERSHEY_SIMPLEX,1.2,color,2)
    return img

def make(frame_path, nback, mode, tag):
    img=cv2.imread(frame_path); H,W=img.shape[:2]
    sx=W/1920.0; sy=H/1080.0
    # 前台 4 格
    for ref in slots1920_front[:4]:
        p=to_pixels(ref,sx,sy); draw(img, equip_frames_front(p,mode))
    # 后台 n 格
    for ref in back_slots(nback):
        p=to_pixels(ref,sx,sy); draw(img, equip_frames_back(p,mode))
    out=f"tools/eq_box_back{nback}_mode{mode}.png"
    cv2.imwrite(out,img)
    print("saved",out)
    return out

for nback,frames in [(6,["130104.png","132328.png"]),(7,["125924.png","130112.png"])]:
    fp=os.path.join("tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-2026-07-28",frames[0])
    for mode in [3,2]:
        make(fp,nback,mode,f"{nback}槽")
    print(f"-- {nback}槽 done (mode3/mode2) --")

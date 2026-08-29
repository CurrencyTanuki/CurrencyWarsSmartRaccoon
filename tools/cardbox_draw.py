"""画软件识别角色卡牌的包围框（PreparationCharacterSlots1920 前台 + BackCharacterSlots1920 后台）
2张：6槽帧(130104)、7槽帧(125924)。绿框=角色卡框。
"""
import cv2, numpy as np, os

slots1920_front = [(681,329,128,140),(827,329,122,140),(972,329,120,140),(1114,329,120,140)]
def back_slots(n):
    c=960; slotw=130; sloth=145
    if n==6: offs=[-362,-217,-72,72,217,362]
    elif n==7: offs=[-435,-290,-145,0,145,290,435]
    elif n==8: offs=[-507,-362,-217,-72,72,217,362,507]
    elif n==9: offs=[-580,-435,-290,-145,0,145,290,435,580]
    else: offs=[]
    return [(c+o,600,slotw,sloth) for o in offs]

def draw_card_boxes(img, refs, sx, sy, color, tag_prefix):
    for i,(rx,ry,rw,rh) in enumerate(refs):
        x0=int(rx*sx); y0=int(ry*sy); x1=int((rx+rw)*sx); y1=int((ry+rh)*sy)
        cv2.rectangle(img,(x0,y0),(x1,y1),color,3)
        cv2.putText(img,f"{tag_prefix}{i}",(x0,max(10,y0+24)),cv2.FONT_HERSHEY_SIMPLEX,1.3,color,2)

def make(frame_path, nback, tag):
    img=cv2.imread(frame_path); H,W=img.shape[:2]
    sx=W/1920.0; sy=H/1080.0
    draw_card_boxes(img, slots1920_front[:4], sx, sy, (0,255,0), "F")
    draw_card_boxes(img, back_slots(nback), sx, sy, (0,0,255), "B")
    out=f"tools/cardbox_back{nback}_{tag}.png"
    cv2.imwrite(out, img)
    print("saved",out, f"{W}x{H}")
    return out

make("tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-2026-07-28/130104.png", 6, "6slot")
make("tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-2026-07-28/125924.png", 7, "7slot")

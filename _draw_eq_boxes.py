import cv2, numpy as np, os

OUT = r"D:\CWAFix-20260814\_eq_boxes"
os.makedirs(OUT, exist_ok=True)

front = [
    (681, 329, 128, 140), (827, 329, 122, 140),
    (972, 329, 120, 140), (1114, 329, 120, 140),
]
def back_slots(count):
    offs = [(-362,-217,-72,72,217,362),
            (-435,-290,-145,0,145,290,435),
            (-507,-362,-217,-72,72,217,362,507),
            (-580,-435,-290,-145,0,145,290,435,580)]
    idx = max(0, min(count - 6, 3))
    return [(960 + o - 65, 600, 130, 145) for o in offs[idx]]

def A_fixed(ref, is_back):
    x, y, w, h = ref
    starts = [0.03, 0.34, 0.68] if is_back else [0.03, 0.39, 0.72]
    yc = y + h * 1.08
    sw, sh = w * 0.26, h * 0.26
    return [(x + w * f, yc - sh / 2, sw, sh) for f in starts]

def B_count3(ref, is_back):
    x, y, w, h = ref
    starts = [0.03, 0.34, 0.68] if is_back else [0.03, 0.39, 0.75]
    yc = y + h * 1.08
    sw, sh = w * 0.33, h * 0.26
    return [(x + w * f, yc - sh / 2, sw, sh) for f in starts]

def B_count2(ref, is_back):
    x, y, w, h = ref
    yc = y + h * 1.08
    sw, sh = w * 0.30, h * 0.26
    cx = x + w / 2.0
    return [(cx - 21 - sw / 2, yc - sh / 2, sw, sh),
            (cx + 21 - sw / 2, yc - sh / 2, sw, sh)]

def scale(rect, W, H):
    x, y, w, h = rect
    return (int(round(x * W / 1920)), int(round(y * H / 1080)),
            int(round(w * W / 1920)), int(round(h * H / 1080)))

def draw_equip(img, slots, is_back_list, boxes, label, color, lw):
    for ref, bk in zip(slots, is_back_list):
        for (bx, by, bw, bh) in boxes(ref, bk):
            sx, sy, sw, sh = scale((bx, by, bw, bh), img.shape[1], img.shape[0])
            cv2.rectangle(img, (sx, sy), (sx+sw, sy+sh), color, lw)

def draw_char_feid(img, slots, is_back_list):
    for ref, _ in zip(slots, is_back_list):
        sx, sy, sw, sh = scale(ref, img.shape[1], img.shape[0])
        cv2.rectangle(img, (sx, sy), (sx+sw, sy+sh), (0,0,255), 4)

def slots_for(back_n):
    return front + back_slots(back_n)

def load(img, back_n):
    return (img, slots_for(back_n), [False]*4+[True]*len(back_slots(back_n)))

base = cv2.imread(r"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260821-080042.753049-000001.png")
f125 = cv2.imread(r"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-2026-07-28\125924.png")
BCK = 6

for tag, imgin, name in [("31", base, "31"), ("125", f125, "125924")]:
    img, slots, isb = load(imgin, BCK)
    # A 套 (3件/2件相同固定三槽, 分开画)
    for mode, outn in [("3","A_"+name+"_mode3.png"), ("2","A_"+name+"_mode2.png")]:
        m = img.copy()
        draw_equip(m, slots, isb, A_fixed, "A", (0,0,255), 4)
        cv2.imwrite(os.path.join(OUT,outn), m)
    # B 套
    for mode, boxf, outn in [("3",B_count3,"B_"+name+"_mode3.png"), ("2",B_count2,"B_"+name+"_mode2.png")]:
        m = img.copy()
        draw_equip(m, slots, isb, boxf, "B", (0,0,255), 4)
        cv2.imwrite(os.path.join(OUT,outn), m)

# 角色框（只框角色卡，不框装备，红框）
for tag, imgin, name in [("31", base, "31"), ("125", f125, "125924")]:
    img, slots, isb = load(imgin, BCK)
    m = img.copy()
    draw_char_feid(m, slots, isb)
    cv2.imwrite(os.path.join(OUT, "char_"+name+".png"), m)

print("generated:", sorted(n for n in os.listdir(OUT) if n.endswith('.png')))

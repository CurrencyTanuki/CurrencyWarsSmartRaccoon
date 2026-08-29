import cv2
def crop(src, area, out):
    im=cv2.imread(src)
    h,w=im.shape[:2]
    x=int(area[0]*w); y=int(area[1]*h); rx=int(area[2]*w); ry=int(area[3]*h)
    c=im[y:y+ry, x:x+rx]
    c=cv2.resize(c,None,fx=3,fy=3,interpolation=cv2.INTER_CUBIC)
    cv2.imwrite(out,c)
    print('saved',out,c.shape)
base='D:/CWAFix-20260814/tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-live-2026-07-29/'
t='D:/CWAFix-20260814/tools/'
crop(base+'preparation-1-3-gold-23-user.png',(0.43,0.198,0.18,0.12),t+'pop_prep13.png')
crop(base+'preparation-1-7-user.png',(0.43,0.198,0.18,0.12),t+'pop_prep17.png')
crop(base+'preparation-1-2-blank-board.png',(0.43,0.198,0.18,0.12),t+'pop_prep12.png')
crop(base+'preparation-1-4-user-2026-08-01.png',(0.43,0.198,0.18,0.12),t+'pop_prep14.png')

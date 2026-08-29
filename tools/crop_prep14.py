import cv2
im=cv2.imread('D:/CWAFix-20260814/tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-live-2026-07-29/preparation-1-4-user-2026-08-01.png')
h,w=im.shape[:2]
# 人口区域 0.43,0.198,0.180,0.120 -> x像素、y像素
x=int(0.43*w); y=int(0.198*h); rx=int(0.18*w); ry=int(0.12*h)
c=im[y:y+ry, x:x+rx]
# 放大8倍
big=cv2.resize(c,None,fx=6,fy=6,interpolation=cv2.INTER_CUBIC)
cv2.imwrite('D:/CWAFix-20260814/tools/pop_prep14_big.png',big)
print('saved',big.shape)
# 灰度直方图看是否有数字(区分前景)
g=cv2.cvtColor(c,cv2.COLOR_BGR2GRAY)
print('mean',g.mean().round(1),'min',g.min(),'max',g.max())

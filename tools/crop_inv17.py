import cv2
im=cv2.imread('D:/CWAFix-20260814/tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-live-2026-07-29/preparation-1-7-user.png')
h,w=im.shape[:2]
# 物品栏：相对坐标(取自 InventoryIconSlots 归一化包围，约 x 0.924-0.972, y 0.115-0.64)
# 直接按 132307 的像素相对化转换为相对比例：(2405/2559, 165/1439, 103/2559, 750/1439)
rm=im[ int(0.1147*h):int(0.636*h), int(0.9398*w):int(0.9802*w) ]
big=cv2.resize(rm,None,fx=3,fy=3,interpolation=cv2.INTER_CUBIC)
cv2.imwrite('D:/CWAFix-20260814/tools/inv_prep17.png',big)
print('saved',big.shape,'img',w,h)

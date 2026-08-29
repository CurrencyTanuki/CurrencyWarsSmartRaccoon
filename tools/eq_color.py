import cv2
import numpy as np
p=r'D:\CWAFix-20260814\data\runtime\1.0.0\4.4\equipment\assets\currency_wars_equipment_icons\currency_wars_equipment_046.png'
img=cv2.imread(p)
b,g,r = img[:,:,0].astype(float), img[:,:,1].astype(float), img[:,:,2].astype(float)
# 主色调（BGR）：蓝背景游戏里 icon 背景可能是透明。取非透明/彩色区域均值
h,s,v=cv2.split(cv2.cvtColor(img,cv2.COLOR_BGR2HSV))
mask=(s>40)
if mask.sum()==0:
    print('046 主色调 不饱和(可能无色/灰蓝)')
else:
    mb=float(b[mask].mean()); mg=float(g[mask].mean()); mr=float(r[mask].mean())
    print(f'046 彩色区均值 R={mr:.0f} G={mg:.0f} B={mb:.0f} (蓝主导= B>G,R)')
for name,val in [('反重力皮靴 055','currency_wars_equipment_055.png'),('光速螺旋桨 051','currency_wars_equipment_051.png'),('财富宝钻 086','currency_wars_equipment_086.png')]:
    ip=r'D:\CWAFix-20260814\data\runtime\1.0.0\4.4\equipment\assets\currency_wars_equipment_icons\\'+val
    im=cv2.imread(ip)
    bs,gs,rs=im[:,:,0].astype(float),im[:,:,1].astype(float),im[:,:,2].astype(float)
    hs,vs,ss=cv2.split(cv2.cvtColor(im,cv2.COLOR_BGR2HSV))
    m=(ss>40)
    if m.sum()==0: print(f'{name}: 低饱和'); continue
    print(f'{name}: R={rs[m].mean():.0f} G={gs[m].mean():.0f} B={bs[m].mean():.0f}')

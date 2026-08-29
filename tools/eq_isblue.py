import cv2
import numpy as np, sys
io=sys.stdout
base=r'D:\CWAFix-20260814\data\runtime\1.0.0\4.4\equipment\assets\currency_wars_equipment_icons\\'
for name,fn in [('轮滑鞋046','currency_wars_equipment_046.png'),('财富宝钻086','currency_wars_equipment_086.png'),('永动机064','currency_wars_equipment_064.png'),('反重力皮靴055','currency_wars_equipment_055.png'),('光速螺旋桨051','currency_wars_equipment_051.png'),('胜利之旗075','currency_wars_equipment_075.png')]:
    im=cv2.imread(base+fn)
    if im is None: print(name,'缺失'); continue
    B=im[:,:,0].astype(float); G=im[:,:,1].astype(float); R=im[:,:,2].astype(float)
    # 非透明/有内容像素（全图，背景多为透明→0,0,0 会被 isBlue 当非蓝；这里只看有颜色像素）
    h,s,v=cv2.split(cv2.cvtColor(im,cv2.COLOR_BGR2HSV))
    m=(h>0)|(s>30)  # 有色像素
    if m.sum()==0: continue
    nb=(B[m]-R[m]).mean(); ng=(B[m]-G[m]).mean()
    isblue= (B[m]>R[m]+25)&(B[m]>G[m]+10)&(B[m]>70)
    print(f'{name}: 平均B-R={nb:+.0f} B-G={ng:+.0f} isBlue像素占比={isblue.mean()*100:.0f}%')

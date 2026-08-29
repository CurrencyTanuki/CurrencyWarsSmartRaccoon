import cv2, numpy as np, sys
io=sys.stdout
def analyze(label,p):
    im=cv2.imread(p)
    if im is None: print(label,'缺失'); return
    B=im[:,:,0].astype(float);G=im[:,:,1].astype(float);R=im[:,:,2].astype(float)
    s=cv2.split(cv2.cvtColor(im,cv2.COLOR_BGR2HSV))[1]
    isBlue=(B>R+25)&(B>G+10)&(B>70)
    sat=(s>60)
    nonBarren=(~isBlue)  # 段检测"非蓝"
    # 非蓝段按列占比>30%的行
    coldom=((~isBlue).sum(axis=0)>= im.shape[0]*0.30).sum()
    print(f'{label}: {im.shape[1]}x{im.shape[0]} isBlue={isBlue.mean()*100:.0f}% 非蓝={nonBarren.mean()*100:.0f}% 饱和={sat.mean()*100:.0f}% B-R均值={ (B[R>22]-R[B>22]).mean():.0f} 非蓝列数={coldom}')
for i in range(3):
    analyze(f'后排装备带 idx{i}', rf'D:\CWAFix-20260814\tools\crop_backband_{i}.png')
analyze('物品栏',r'D:\CWAFix-20260814\tools\crop_inventory.png')

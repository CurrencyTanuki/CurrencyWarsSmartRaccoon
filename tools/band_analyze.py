import cv2, numpy as np, sys
ioed=sys.stdout
def analyze(label,p):
    im=cv2.imread(p)
    if im is None: print(label,'缺失'); return
    B=im[:,:,0].astype(float);G=im[:,:,1].astype(float);R=im[:,:,2].astype(float)
    h,s,v=cv2.split(cv2.cvtColor(im,cv2.COLOR_BGR2HSV))
    # isBlue 段检测判据
    isBlue=(B>R+25)&(B>G+10)&(B>70)
    # 高饱和(彩色=图标内容, 非灰底)
    sat=(s>60)
    print(f'{label}: {im.shape[1]}x{im.shape[0]} isBlue占比={isBlue.mean()*100:.0f}% 高饱和占比={sat.mean()*100:.0f}% R={R.mean():.0f} G={G.mean():.0f} B={B.mean():.0f}')
    print(f'   高饱和区域B-R均值={ (B[sat]-R[sat]).mean():.0f} B-G={ (B[sat]-G[sat]).mean():.0f}  高饱和色相中位数H={np.median(h[sat]):.0f}')
analyze('千冶刃后排装备带(3x)',r'D:\CWAFix-20260814\tools\crop_qiantye_back_band.png')
analyze('物品栏',r'D:\CWAFix-20260814\tools\crop_inventory.png')

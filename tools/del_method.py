p=r'D:\CWAFix-20260814\src\CurrencyWarsAssistant.Tasks\Phase2SlotDetector.cs'
ls=open(p,encoding='utf-8').read().split('\n')
# 删除 26..162 行（1-based），注意 split 后 index 0 = 行1
# 保留 1-25 和 >=163
del ls[26-1:163-1]  # 删除 index 25..161 (行26..162)
open(p,'w',encoding='utf-8').write('\n'.join(ls))
print('done, new line count =', len(ls))
# 校验 AlignSlotColumns 还在
big='\n'.join(ls)
print('AlignSlotColumns present:', 'AlignSlotColumns' in big)
print('DetectBackSlotCount method gone (only注释引用 none):', 'public static int DetectBackSlotCount' in big)

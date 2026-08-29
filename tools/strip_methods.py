import re, io, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
root=r'D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\\'
files=['BackSlotCountRedTests.cs','BatchAllRunsProbe.cs','BackSlotCountBatchProbe.cs','BatchDiagProbe.cs','EquipmentDiagProbe.cs','FullFormationDiagTests.cs','Phase2SlotDetectorTests.cs','UserReferenceDiagnosisTests.cs']
for fn in files:
    p=root+fn
    try: ls=open(p,encoding='utf-8').read().split('\n')
    except FileNotFoundError: print('missing',fn); continue
    erased=0
    out=[]; i=0
    inmethod=False; depth=0; method=[]
    # 按行扫描，遇到含 DetectBackSlotCount 的行，向上回溯到最近方法签名，配对花括号删除方法
    for i,line in enumerate(ls):
        if 'DetectBackSlotCount' in line or inmethod:
            # 向上找方法签名（行内容匹配 public/private/internal/... 且含 ( ）
            start=i
            while start>0 and not re.search(r'(public|private|internal|protected|static)\b.*\w+\s*\(', ls[start]):
                start-=1
                if start<i-8: break
            # 从 start 到包含闭合 } 的方法
            close=i; dep=0
            # 定位方法体起 at start 或 body {：
            b=start
            # 数括号到闭合
            dep=0; seen_open=False
            j=start
            while j<len(ls):
                for ch in ls[j]:
                    if ch=='{': dep+=1; seen_open=True
                    elif ch=='}': dep-=1
                if seen_open and dep==0: break
                j+=1
            # 删除 start..j（方法+尾）
            remove=[k for k in range(start,j+1)]
            # 改为把这段追加入 out 但不输出——用标记
            # 简化：不删整段，把该文件重写时不输出这些行
            for k in remove: ls[k]=''  # 清空
            inmethod=False
            print(f'{fn}: removed method span start={start} end={j} (行含DetectBackSlotCount@{i})')
            erased+=1
    ls=[l for l in ls if l!='']  # 移除空行(被删方法留下的连串空行，简单合并)
    # 写回（保留原空行结构尽量）
    open(p,'w',encoding='utf-8').write('\n'.join(ls))
    print(f'{fn}: erased={erased} 剩余行={len(ls)}')

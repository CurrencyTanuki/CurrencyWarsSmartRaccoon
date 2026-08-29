import io, sys, re
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
root=r'D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\\'
files=['BackSlotCountRedTests.cs','BatchAllRunsProbe.cs','BackSlotCountBatchProbe.cs','BatchDiagProbe.cs','EquipmentDiagProbe.cs','FullFormationDiagTests.cs','Phase2SlotDetectorTests.cs','UserReferenceDiagnosisTests.cs']
# 分组：以"属性行([Fact]/[Theory]/[InlineData]/[MemberData])开头"到"下一个属性行 或 前)"完整方法
for fn in files:
    p=root+fn
    try: ls=open(p,encoding='utf-8').read().split('\n')
    except FileNotFoundError: print('missing',fn); continue
    # 找每行是否含 DetectBackSlotCount
    tok=[i for i,l in enumerate(ls) if 'DetectBackSlotCount' in l]
    if not tok: print(fn,'无引用,跳过'); continue
    # 删除每个引用所在的方法块：向上回溯到最近的属性行([Fact]等)或文件头，向下到这行的闭合方法(花括号配对)
    remove=set()
    for ref in tok:
        start=ref
        # 向上找最近属性行或空行前的签名
        while start>0:
            if re.match(r'\s*\[(Fact|Theory|InlineData|MemberData)', ls[start]) or ls[start].startswith('['):
                break
            start-=1
        # 找到方法体起：start 之后最近的 '{' 所在行 = body start
        body=None
        for k in range(start,len(ls)):
            if '{' in ls[k]: body=k; break
        # 花括号配对到闭合
        if body is None: 
            # 无 body（可能是属性注释），保守删 start..ref+2
            for k in range(start,ref+3): remove.add(k)
            continue
        dep=0
        j=body
        while j<len(ls):
            for ch in ls[j]:
                if ch=='{': dep+=1
                elif ch=='}': dep-=1
            j+=1
            if dep==0: break
        for k in range(start,j): remove.add(k)  # 含方法属性行到闭合
    # 行号递减删除
    newls=[l for i,l in enumerate(ls) if i not in remove]
    open(p,'w',encoding='utf-8').write('\n'.join(newls))
    print(f'{fn}: 删除引用方法;引用行={tok}; 剩余行 {len(ls)}->{len(newls)}')

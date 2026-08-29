import sys
p=r'D:\CWAFix-20260814\src\CurrencyWarsAssistant.Vision\CharacterCardRecognition.cs'
ls=open(p,encoding='utf-8').read().split('\n')
depth=0
for idx,ln in enumerate(ls,1):
    if idx>=310 and idx<=536:
        for ch in ln:
            if ch=='{': depth+=1
            elif ch=='}': depth-=1
        print(f'{idx:4} d={depth:3} {ln.strip()[:72]}')

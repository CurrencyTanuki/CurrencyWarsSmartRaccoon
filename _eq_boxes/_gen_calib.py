# 从三份标定 JSON 精确生成 C# 硬编码常量类（零改动、零遗漏）
import json
from collections import Counter

B=r"C:/Users/zzz81/AppData/Roaming/reasonix/global-workspace/.reasonix/attachments"
FILES=[
    B+"/clipboard-20260821-211755.295820-000002.json",
    B+"/clipboard-20260821-211755.303214-000003.json",
    B+"/clipboard-20260821-211755.310181-000004.json",
]
docs=[json.load(open(p,encoding="utf-8")) for p in FILES]

def ints(quad): return [[int(round(v)) for v in pt] for pt in quad]

# front/back quads 跨三份投票取多数
def pick(counter):
    best=None;bc=-1
    for k,v in counter.items():
        if v>bc:best=k;bc=v
    x,y=best.split(",");return [int(x),int(y)]

front_all=[[Counter() for _ in range(4)] for _ in range(4)]
back_all={c:[[Counter() for _ in range(4)] for _ in range(int(c))] for c in ["6","7","8","9"]}
for d in docs:
    for card,q in enumerate(d["frontQuads"]):
        for vi,pt in enumerate(ints(q)):
            front_all[card][vi]["%d,%d"%tuple(pt)]=front_all[card][vi].get("%d,%d"%tuple(pt),0)+1
    for c,qs in d["backQuads"].items():
        for card,q in enumerate(qs):
            for vi,pt in enumerate(ints(q)):
                k="%d,%d"%tuple(pt)
                back_all[c][card][vi][k]=back_all[c][card][vi].get(k,0)+1

front_quads=[[pick(c) for c in card] for card in front_all]
back_quads={c:[[pick(cv) for cv in card] for card in cards] for c,cards in back_all.items()}

def quad_cs(q):  # -> new int[][]{ {x,y},... }
    return "new int[][]{"+",".join("new[]{%d,%d}"%(c[0],c[1]) for c in q)+"}"
def list_cs(qs, indent):
    pad=" "*indent
    return (",\n"+pad).join(quad_cs(q) for q in qs)

# 装备槽：按 (equipCount, zone, back_pop, card) -> {slot: quad}
equip={}
for d in docs:
    f0=max(int(k.split(":")[-1]) for k in d["equipOverrides"] if k.startswith("front:0:"))+1
    for k,quad in d["equipOverrides"].items():
        p=k.split(":")
        zone=p[0]
        if zone=="front": key=(f0,"front",None,int(p[1])); slot=int(p[2])
        else: key=(f0,"back",int(p[1]),int(p[2])); slot=int(p[3])
        equip.setdefault(key,{})[slot]=ints(quad)

def slots_for(key_pred):
    cards=sorted({k[3] for k in equip if key_pred(k)})
    rows=[]
    for card in cards:
        kv={k:qs for k,qs in equip.items() if key_pred(k) and k[3]==card}
        slots={}
        for qs in kv.values():
            for s,q in qs.items(): slots[s]=q
        rows.append([slots[s] for s in sorted(slots)])  # card -> [slot quad...]
    return rows  # list of cards, each list of slot quads

def cards_cs(cardSlots, indent):
    """cardSlots: list of (list of slot-quad). 返回 int[][][][] 的数组文本（每 card = int[][][]{slots}）。"""
    pad=" "*indent
    items=[]
    for slots in cardSlots:
        # 一个 card = new int[][][]{ slotQuad0, slotQuad1, ... }
        inner=",".join(quad_cs(sq) for sq in slots)
        items.append("new int[][][]{"+inner+"}")
    return (",\n"+pad).join(items)

L=[]
L.append("using System.Collections.Generic;")
L.append("")
L.append("namespace CurrencyWarsAssistant.Tasks")
L.append("{")
L.append("    /// <summary>")
L.append("    /// 用户 2026-08-21 手标标定（正交投影坐标，1920×1080 参考系，已四舍五入到整数）。")
L.append("    /// 阵营/装备识别只能按这些坐标裁剪；全部为四角 [[TLx,TLy],[TRx,TRy],[BLx,BLy],[BRx,BRy]]。")
L.append("    /// BackQuads 按后台人口 6/7/8/9 分套；BackEquip 按 (装备数量, 后台人口) 分套。")
L.append("    /// </summary>")
L.append("    public static class CalibrationSlots")
L.append("    {")
L.append("        /// 前台 4 张角色卡四角。")
L.append("        public static readonly int[][][] FrontQuads1920 = new int[][][]")
L.append("        {")
for q in front_quads:
    L.append("            "+list_cs([q],14)+",")
L.append("        };")
L.append("")
L.append("        /// 后台角色：人口 6/7/8/9 各一套四角。")
L.append("        public static readonly Dictionary<int,int[][][]> BackQuads1920 = new()")
L.append("        {")
for c in ["6","7","8","9"]:
    L.append("            [%s] = new int[][][] {"%c)
    rows=[list_cs([q],20) for q in back_quads[c]]
    L.append("                "+(",\n                ").join(rows)+",")
    L.append("            },")
L.append("        };")
L.append("")
L.append("        /// 前台装备槽：装备数量(1/2/3) 一套，每套 card→slot→四角。")
L.append("        public static readonly Dictionary<int,int[][][][]> FrontEquip = new()")
L.append("        {")
for ec in sorted({k[0] for k in equip if k[1]=="front"}):
    rows=slots_for(lambda k:k[0]==ec and k[1]=="front")
    L.append("            [%d] = new int[][][][] {"%ec)
    L.append("                "+(",\n                ").join("new int[][][]{"+",".join(quad_cs(r) for r in row)+"}" for row in rows)+",")
    L.append("            },")
L.append("        };")
L.append("")
L.append("        /// 后台装备槽：键=(装备数量, 后台人口)，值为 card→slot→四角。")
L.append("        public static readonly Dictionary<(int EquipCount,int Population),int[][][][]> BackEquip = new()")
L.append("        {")
for ec in sorted({k[0] for k in equip if k[1]=="back"}):
    for pop in sorted({k[2] for k in equip if k[1]=="back" and k[0]==ec}):
        rows=slots_for(lambda k:k[0]==ec and k[1]=="back" and k[2]==pop)
        L.append("            [(%d,%d)] = new int[][][][] {"%(ec,pop))
        L.append("                "+(",\n                ").join("new int[][][]{"+",".join(quad_cs(r) for r in row)+"}" for row in rows)+",")
        L.append("            },")
L.append("        };")
L.append("    }")
L.append("}")

src="\n".join(L)
out=r"D:/CWAFix-20260814/src/CurrencyWarsAssistant.Tasks/CalibrationSlots.cs"
open(out,"w",encoding="utf-8").write(src)
print("写回字节:",len(src))
print("front equip groups:",sorted({k[0] for k in equip if k[1]=="front"}))
print("back (ec,pop):",sorted({(k[0],k[2]) for k in equip if k[1]=="back"}))
print("frontQuads[3]:",front_quads[3])

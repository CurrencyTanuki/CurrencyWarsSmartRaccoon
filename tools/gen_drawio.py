# -*- coding: utf-8 -*-
# 生成排版干净的 drawio 决策树（三列布局，尽量少交叉）
import html

def esc(s): return html.escape(s, quote=True)

# ---- 节点: id -> (title, x, y, w, h, shape) ----
# 列: 重刷列xC=250 / 主链xC=620 / 右列1xC=1180 / 右列2xC=1750
N = {
    # 主链
    "A":  ("开 始\n读取:投资环境/金币/hpNow", 445, 40, 360, 70, "start"),
    "B":  ("D1 环境可推进?\n英雄登场/契约/邀请任一", 470, 160, 300, 100, "dec"),
    "C":  ("主线起手\n英雄登场→昔涟 / 圣杯线→集圣杯", 480, 320, 280, 76, "proc"),
    "D":  ("D2 血量节点①\n选投资策略前 hpInvest≥88?", 470, 450, 300, 100, "dec"),
    "G":  ("BUY 买角色【组件】", 470, 700, 300, 100, "proc"),
    "H":  ("D3 凑齐 4 圣杯?\n圣杯成员+星徽/圣杯转", 470, 850, 300, 100, "dec"),
    "I":  ("D4 本次哪条线?\n是否需要已有5费本体", 470, 1000, 300, 100, "dec"),
    "K":  ("D5 血量节点②\n浮现「奇迹代偿(扣88)」?", 470, 1150, 300, 110, "dec"),
    "P":  ("达成终点(凑齐即判可成)\n无限之釜→三星Archer\n奇迹代偿→8投影+5费本体", 460, 1320, 320, 92, "proc"),
    "Q":  ("D7 用户目标?", 500, 1480, 240, 80, "dec"),
    # 右列1 —— 补血分支 (D2否) + D5是分支
    "D21":("本次达成终点?", 1000, 610, 260, 90, "dec"),
    "X1": ("必须刷「二极管」\nSTR276 +10 → 94≥88", 985, 760, 290, 80, "proc"),
    "X2": ("刷到? (可迂回再凹)", 1000, 900, 260, 90, "dec"),
    "H2": ("hp ≥ 88? 血够才可点", 1000, 1420, 260, 90, "dec"),
    "N1": ("选「奇迹代偿」\n扣88血+全金币→8投影", 990, 1570, 280, 84, "proc"),
    "N2": ("图标不可点·改选另一", 1000, 1710, 260, 80, "proc"),
    "C2": ("另一也非目标\n且血量难回升?", 1000, 1840, 260, 90, "dec"),
    # 右列2 —— D5否分支 + 收工
    "L":  ("D6 第1、2个诅咒\n都没刷到想要的?", 1600, 1150, 300, 100, "dec"),
    "M1": ("随机选 · 默认选左边\n(与选择模块一致,不重刷)", 1580, 1300, 340, 84, "proc"),
    "M2": ("选目标试炼", 1620, 1440, 260, 70, "proc"),
    "EAA":("收工A\n已拿到要求的8完美投影仪\n交你手动拖8投影→三星", 1580, 1610, 360, 84, "end"),
    "EAB":("收工B\n同样只拿到8投影即停\n与A同处理", 1580, 1750, 340, 84, "end"),
    # 重刷列
    "R1": ("重刷\n退出结算·重新开局\n本局不作数", 90, 1360, 230, 100, "retry"),
}

# ---- 边: (from, to, label) ----
E = [
    ("A","B","开始"),
    ("B","C","是"),
    ("B","R1","否"),
    ("C","D",""),
    ("D","G","是(已≥88)"),
    ("D","D21","否(仅84血)"),
    ("D21","X1","奇迹代偿线"),
    ("X1","X2",""),
    ("X2","G","是(94≥88)"),
    ("X2","R1","否"),
    ("D21","G","无限之釜线(不需88血)"),
    ("G","H",""),
    ("H","R1","否"),
    ("H","I","是"),
    ("I","J1","奇迹代偿线"),
    ("I","K","无限之釜线直接给三星"),
    ("K","L","否(一般试炼)"),
    ("L","M1","是"),
    ("L","M2","否"),
    ("M1","P",""),
    ("M2","P",""),
    ("K","H2","是(出现奇迹代偿)"),
    ("H2","N1","是"),
    ("H2","N2","否"),
    ("N1","P",""),
    ("N2","C2",""),
    ("C2","R1","是"),
    ("C2","P","否"),
    ("P","Q",""),
    ("Q","EAA","A 只要1个"),
    ("Q","EAB","B 全员"),
    # 补上 J1/J2 (本体分支) —— 用右列1中间
]
# J1/J2 本体分支：放在右列1 X2 下方与 H 之间？为清晰，放本体区
J1 = ("确保≥1个5费本体\n登场Archer/聘书/英雄登场", 985, 1050, 290, 84, "proc")
J2 = ("本体到手? 先本体后押币", 1000, 1190, 260, 90, "dec")
N["J1"]=J1; N["J2"]=J2
# 本体边
E.append(("I","J1","奇迹代偿线"))
E.append(("J1","J2",""))
E.append(("J2","R1","否"))
E.append(("J2","K","是"))

SIZE_STYLE = {
    "start": "rounded=1;arcSize=50;fillColor=#d5f5e3;strokeColor=#1e8449;fontColor=#145a32;fontSize=13;fontStyle=1;html=1;whiteSpace=wrap;verticalAlign=middle;",
    "end":   "rounded=1;arcSize=50;fillColor=#d5f5e3;strokeColor=#1e8449;fontColor=#145a32;fontSize=12;fontStyle=1;html=1;whiteSpace=wrap;verticalAlign=middle;",
    "proc":  "rounded=1;fillColor=#eaf3fb;strokeColor=#3b82c4;fontColor=#1f3a63;fontSize=12;fontStyle=1;html=1;whiteSpace=wrap;verticalAlign=middle;",
    "dec":   "shape=rhombus;fillColor=#fff7e0;strokeColor=#d69a08;fontColor=#6e5200;fontSize=12;fontStyle=1;html=1;whiteSpace=wrap;verticalAlign=middle;",
    "retry": "rounded=1;arcSize=50;fillColor=#fdecec;strokeColor=#e5484d;fontColor=#a91b1b;fontSize=13;fontStyle=1;html=1;whiteSpace=wrap;verticalAlign=middle;",
}

cells=['<mxCell id="0"/>','<mxCell id="1" parent="0"/>']
idmap={}; nid=2
for key,(title,x,y,w,h,shape) in N.items():
    val=esc(title).replace("\n","&#10;")
    st=SIZE_STYLE[shape]
    cells.append(f'<mxCell id="n{nid}" value="{val}" style="{st}html=1;whiteSpace=wrap;verticalAlign=middle;" vertex="1" parent="1"><mxGeometry x="{x}" y="{y}" width="{w}" height="{h}" as="geometry"/></mxCell>')
    idmap[key]=f"n{nid}"; nid+=1
for frm,to,label in E:
    st=("edgeStyle=orthogonalEdgeStyle;rounded=0;orthogonalLoop=1;jettySize=auto;html=1;"
        "endArrow=block;strokeWidth=2;fontSize=11;fontColor=#333;")
    cells.append(f'<mxCell id="e{nid}" value="{esc(label)}" style="{st}" edge="1" parent="1" source="{idmap[frm]}" target="{idmap[to]}"><mxGeometry relative="1" as="geometry"/></mxCell>')
    nid+=1

xml=('<mxfile host="app.diagrams.net" agent="0.0" version="21.0.0"><diagram id="s" name="决策树3star5cost">'
     '<mxGraphModel dx="0" dy="0" grid="1" gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="2600" pageHeight="2600" math="0" shadow="0"><root>'
     + "\n".join(cells) + '</root></mxGraphModel></diagram></mxfile>')
out=r"D:/CWAFix-20260814/docs/决策树_3star5cost.drawio"
open(out,"w",encoding="utf-8").write(xml)
import xml.etree.ElementTree as ET
t=ET.parse(out); cells2=t.getroot().findall('.//mxCell')
ns={c.get('id') for c in cells2}
bad=[e.get('id') for e in cells2 if e.get('edge')=='1' and (e.get('source') not in ns or e.get('target') not in ns)]
print("written",out,"| nodes",len([c for c in cells2 if c.get('vertex')=='1']),"edges",len([c for c in cells2 if c.get('edge')=='1']),"| 悬空:",bad if bad else "无")

# -*- coding: utf-8 -*-
# 全夜 32 局不变量扫描器(数据整形;判读由值守 AI 人工完成)——2026-09-08
import re, io, os

RESULT = r"C:\Users\zzz81\Desktop\CurrencyWarsAssistant-测试台-当前\指令测试-result.txt"
BASE = r"C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826\artifacts\audit-prep"

lines = []
with io.open(RESULT, encoding="utf-8") as f:
    for line in f:
        m = re.match(r"\[(\d\d:\d\d:\d\d)\] (.+?) ⇒ (OK|失败)：(.*)", line.rstrip("\n"))
        if m:
            lines.append((m.group(1), m.group(2), m.group(3), m.group(4)))
            continue
        m2 = re.match(r"\[(\d\d:\d\d:\d\d)\] (.+?) ⇢", line.rstrip("\n"))
        if m2:
            lines.append((m2.group(1), m2.group(2), "RX", ""))

# 按局切窗:命中环境= 开局;判定结束= 收官
games = []
cur = None
for ts, cmd, kind, summary in lines:
    m = re.search(r"命中环境=(\S+?)\s", cmd + " " + summary)
    if m and ("M8" in cmd + " " + summary):
        if cur:
            games.append(cur)
        cur = {"start": ts, "env": m.group(1), "ev": [], "end": None, "endlabel": ""}
    if cur is None:
        continue
    cur["ev"].append((ts, cmd, kind, summary))
    me = re.search(r"本局判定结束（(.+?)）", summary)
    if me and ("判定结束" in summary):
        cur["end"] = ts
        cur["endlabel"] = me.group(1)
        games.append(cur)
        cur = None
if cur:
    games.append(cur)

out = []
viol = []
for gi, g in enumerate(games, 1):
    bond = None
    bond_timeline = []
    buys = []
    deploys = []
    sales = []
    prot = []
    m3true = 0
    m3false = 0
    stalls = 0
    i10fail = 0
    i10ok = 0
    bond_drop = None
    prev_bond = None
    for ts, cmd, kind, summary in g["ev"]:
        mb = re.search(r"羁绊=(\d+) 金=(\d+) 血=(\d+)", summary)
        if "I10" in cmd and kind == "OK" and mb:
            i10ok += 1
            bond = int(mb.group(1))
            bond_timeline.append((ts, bond, int(mb.group(2))))
            if prev_bond is not None and bond < prev_bond:
                bond_drop = (ts, prev_bond, bond)
                viol.append("G%d 羁绊下降 %d→%d @%s（=有成员离开前台:交换/卖出/换下）" % (gi, prev_bond, bond, ts))
            prev_bond = bond
        elif "I10" in cmd:
            i10fail += 1
            if "陈旧" in summary or "无新帧" in summary:
                stalls += 1
        m = re.search(r"买到=(\S*)", summary)
        if m is not None and ("M5" in cmd):
            buys.append((ts, m.group(1) if m.group(1) else "(未具名)"))
        m = re.search(r"A1 (\S+) (前台|后台) (\d+)", cmd)
        if m:
            deploys.append((ts, m.group(1), m.group(2), m.group(3)))
        m = re.search(r"部署(\S+?)(?:到| )", summary)
        if m and "已部署" in summary:
            deploys.append((ts, m.group(1), "自动", "-"))
        m = re.search(r"A2 前台 (\d+)|A3 (\d+)", cmd)
        if m and kind == "OK":
            slot = m.group(1) or m.group(2)
            nm = re.search(r"已出售(?:备战席 ?|前台)?\d*号?位?角色?\s*(\S+)?", summary)
            sales.append((ts, cmd.split()[0] if cmd else "?", slot, nm.group(1) if nm and nm.group(1) else "(未具名)"))
        for pm in re.finditer(r"卖人跳过「([^」]+)」：([^（]*)", summary):
            prot.append((ts, pm.group(1), pm.group(2).strip()))
        if "M3" in cmd and kind == "OK":
            mm = re.search(r"应答=(\w+) 累计=(\d+)", summary)
            if mm:
                if mm.group(1) == "True":
                    m3true += 1
                else:
                    m3false += 1
        if "追帧" in summary and "长尾" in summary:
            stalls += 1
    out.append("== G%d %s→%s 环境=%s 策略线=%s ==" % (
        gi, g["start"], g["end"] or "?", g["env"],
        "、".join(re.findall(r"已选择投资策略“(.+?)”", " ".join(s for _, _, _, s in g["ev"]))) or "(未选)"))
    out.append("  买到: " + (", ".join("%s@%s" % (n, t) for t, n in buys) or "无"))
    out.append("  上场: " + (", ".join("%s@%s→%s%s" % d for d in deploys) or "无"))
    out.append("  卖出: " + (", ".join("%s@%s槽%s(%s)" % s for s in sales) or "无"))
    out.append("  保护(跳过卖): " + (", ".join("%s@%s(%s)" % p for p in prot) or "无"))
    out.append("  羁绊快照: " + (", ".join("%s=%d" % b for b in bond_timeline) or "无"))
    out.append("  M3应答 True/False=%d/%d  I10 OK/失败=%d/%d(陈旧类%d)  结局=%s" % (
        m3true, m3false, i10ok, i10fail, stalls, g["endlabel"] or "(进行中)"))
    # 不变量判读
    carrier_bench = [p for p in prot if "星徽" in p[2]]
    if carrier_bench:
        last_carrier_ts = carrier_bench[-1][0]
        dep_after = [d for d in deploys if d[0] > last_carrier_ts]
        out.append("  !! 星徽携带者在板凳(保护×%d),之后上场动作 %d 次——需人工核是否把携带者换回" % (len(carrier_bench), len(dep_after)))
    if bond_drop:
        out.append("  !! 羁绊下降=交换/成员离场签名(见违规清单)")
    out.append("")

with io.open(BASE + r"\night_sweep.txt", "w", encoding="utf-8") as f:
    f.write("\n".join(out))
with io.open(BASE + r"\violations.txt", "w", encoding="utf-8") as f:
    f.write("\n".join(viol) if viol else "(无羁绊下降)")
print("games=%d violations=%d" % (len(games), len(viol)))

#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成《货币战争对局报告》HTML 原型（数据来自 completed-run 存档 + data/4.4 名称映射）。"""
import json
import os
import sys
import html as html_mod

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # 项目根
RUN_DIR = os.environ.get(
    "REPORT_RUN_DIR",
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "CurrencyWarsSmartRaccoon", "runs"))
RUN_ID = os.environ.get("REPORT_RUN_ID", "run-20260802-131519")
DATA4 = os.path.join(BASE, "data", "4.4")


def load_json(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def name_map(path, id_field="id", name_field="name"):
    try:
        data = load_json(path)
        if isinstance(data, list):
            return {item[id_field]: item.get(name_field, item[id_field])
                    for item in data if isinstance(item, dict) and id_field in item}
        items = (data.get("characters") or data.get("items")
                 or data.get("environments") or data.get("affixes")
                 or data.get("competitors") or data.get("strategies") or data)
        if isinstance(items, dict):
            return {k: (v.get(name_field) if isinstance(v, dict) else v)
                    for k, v in items.items()}
        return {item[id_field]: item.get(name_field, item[id_field])
                for item in items if isinstance(item, dict) and id_field in item}
    except Exception:
        return {}


def known_value(obs):
    if obs is None:
        return None
    if isinstance(obs, dict):
        # 上游语义：status=Unknown 表示未识别到（不画该点）；
        # status=Known 且 value=0 表示真实数值就是 0（正常画）。
        return obs.get("value") if obs.get("status") == "known" else None
    return obs


def normalize(d):
    """把 JSON 键统一为首字母大写（兼容 C# 的 camelCase 与 PascalCase）。
    observation（含 status 键）保持原样，其内部 status/value 键不变。"""
    if isinstance(d, dict):
        if "status" in d or "Status" in d:
            return d
        return {k[:1].upper() + k[1:]: normalize(v) for k, v in d.items()}
    if isinstance(d, list):
        return [normalize(x) for x in d]
    return d


def main():
    # 命令行参数：python gen_report.py <存档目录> <存档ID> <输出html路径>
    run_dir = RUN_DIR
    run_id = RUN_ID
    out_path = os.path.join(BASE, "report-demo", "report.html")
    if len(sys.argv) >= 2:
        run_dir = sys.argv[1]
    if len(sys.argv) >= 3:
        run_id = sys.argv[2]
    if len(sys.argv) >= 4:
        out_path = sys.argv[3]
    cr_path = os.path.join(run_dir, run_id, "completed-run.v1.json")
    if not os.path.exists(cr_path):
        print("存档不存在:", cr_path)
        sys.exit(1)
    cr = normalize(load_json(cr_path))

    chars = name_map(os.path.join(DATA4, "currency-wars-characters.json"))
    envs = name_map(os.path.join(DATA4, "investment-environments.json"))
    affs = name_map(os.path.join(DATA4, "enemy-affixes.json"))
    comps = name_map(os.path.join(DATA4, "competitors.json"))
    strs = name_map(os.path.join(DATA4, "investment-strategies.json"))
    str_rarity = {}
    try:
        with open(os.path.join(DATA4, "investment-strategies.json"), encoding="utf-8-sig") as fp:
            for item in json.load(fp):
                if isinstance(item, dict) and item.get("id"):
                    str_rarity[item["id"]] = item.get("rarity", "")
    except Exception:
        str_rarity = {}

    def strategy_label(sid):
        name = strs.get(sid, sid)
        rarity = str_rarity.get(sid, "")
        return f"[{rarity}] {name}" if rarity else name
    bonds = {}
    try:
        with open(os.path.join(DATA4, "currency-wars-characters.json"), encoding="utf-8-sig") as fp:
            bond_catalog = json.load(fp).get("bond_catalog") or []
        bonds = {b.get("id"): b.get("name", b.get("id"))
                 for b in bond_catalog if isinstance(b, dict) and b.get("id")}
    except Exception:
        bonds = {}
    specials = {}
    try:
        with open(os.path.join(DATA4, "phase2-icon-assets", "asset-manifest.jsonl"), encoding="utf-8-sig") as fp:
            for line in fp:
                line = line.strip()
                if not line:
                    continue
                entry = json.loads(line)
                if entry.get("category") == "special_item" and entry.get("id"):
                    specials[entry["id"]] = entry.get("name", entry["id"])
    except Exception:
        specials = {}

    identity = cr.get("IdentityEvidence") or {}
    env_id = identity.get("InvestmentEnvironmentId")
    affix_ids = identity.get("EnemyAffixIds") or []
    enemy_ids = identity.get("EnemyIds") or []
    strat_ids = identity.get("InvestmentStrategyIds") or []

    # 只保留战斗/备战节点（去掉纯功能页：investment_strategy_selection / node_complete / node_failed / opening_* / normal_hud / reward_shop）
    skip_ids = {"investment_strategy_selection", "node_complete", "node_failed",
                "opening_enemy_overview", "opening_investment_environment",
                "normal_hud", "reward_shop", "1-5", "2-5", "3-5"}
    nodes = []
    for n in cr.get("Nodes", []):
        nid = n.get("NodeId", "")
        if nid in skip_ids:
            continue
        nodes.append(n)

    def icon(cat, iid):
        if not iid:
            return ""
        if cat == "character":
            p = f"../data/4.4/character-card-templates/{iid}__default.png"
        elif cat == "environment":
            p = f"../data/4.4/phase2-icon-assets/standardized/investment_environment/{iid}.png"
        elif cat == "affix":
            p = f"../data/4.4/phase2-icon-assets/standardized/enemy_affix/{iid}.png"
        elif cat == "competitor":
            return ""  # 阵营无可用图标（数据只有名称），只显示名称
        elif cat == "strategy":
            p = f"../data/4.4/phase2-icon-assets/standardized/investment_strategy/{iid}.png"
        elif cat == "equipment":
            p = f"../data/raw/4.4/equipment/890ae486642e979b/assets/currency_wars_equipment_icons/{iid}.png"
        elif cat == "special":
            p = f"../data/4.4/phase2-icon-assets/standardized/special_item/{iid}.png"
        else:
            return ""
        return f'<img class="icon" src="{p}" onerror="this.style.display=&#39;none&#39;" />'

    # ---- 数值序列（伤害/理论/金币/行动）----
    gold_series, health_series, dmg_series, act_series = [], [], [], []
    for n in nodes:
        snap = n.get("FinalPreparationSnapshot") or {}
        battle = n.get("FinalBattle")
        gold_series.append(known_value(snap.get("Economy")))
        health_series.append(known_value(snap.get("Health")))
        act_series.append(known_value(snap.get("ActionPoints")))
        dmg_series.append(battle.get("TotalDamage") if battle else None)

    def svg_line(series, color, label, fmt):
        vals = [v for v in series if v is not None]
        if not vals:
            return f'<div class="chart-empty">{label}：无数据</div>'
        w, h, pad = 560, 120, 8
        vmax = max(vals) or 1
        vmin = min(vals)
        span = (vmax - vmin) or 1
        pts = []
        for i, v in enumerate(series):
            x = pad if len(series) <= 1 else pad + i * (w - 2 * pad) / (len(series) - 1)
            y = h - pad - ((v - vmin) / span) * (h - 2 * pad) if v is not None else None
            pts.append((x, y))
        prev = None
        lines = []
        # 辅助网格线（4 条横线）
        for g in range(5):
            gy = pad + g * (h - 2 * pad) / 4
            lines.append(f'<line x1="{pad}" y1="{gy:.1f}" x2="{w - pad}" y2="{gy:.1f}" class="grid" />')
        # 节点横坐标标签
        labels = ""
        for i, n in enumerate(nodes):
            x = pad if len(nodes) <= 1 else pad + i * (w - 2 * pad) / (len(nodes) - 1)
            labels += f'<text x="{x:.1f}" y="{h - 2}" class="xlabel" text-anchor="middle">{html_mod.escape(n.get("NodeId", ""))}</text>'
        # 纵坐标刻度（4 段）
        yticks = ""
        for g in range(5):
            gy = pad + g * (h - 2 * pad) / 4
            val = vmax - (vmax - vmin) * g / 4
            yticks += f'<text x="{pad - 5}" y="{gy + 3:.1f}" class="ylabel" text-anchor="end">{val:,.0f}</text>'
        # 折线（缺失延续）
        seg = []
        for x, y in pts:
            if y is not None:
                seg.append(f"{x:.1f},{y:.1f}")
        poly = f'<polyline points="{" ".join(seg)}" fill="none" stroke="{color}" stroke-width="2" />'
        # 数据点
        dots = "".join(
            f'<circle cx="{x:.1f}" cy="{y:.1f}" r="2.5" fill="{color}" />'
            for x, y in pts if y is not None)
        return (f'<div class="chart"><div class="chart-title">{label}</div>'
                f'<svg viewBox="0 0 {w} {h}" width="100%" preserveAspectRatio="xMidYMid meet">'
                f'{"".join(lines)}{yticks}{poly}{dots}{labels}</svg></div>')

    # ---- 节点卡片 ----
    cards = []
    current_plane = None
    for i, n in enumerate(nodes):
        nid = n.get("NodeId", "?")
        plane = nid.split("-")[0]
        if plane != current_plane:
            current_plane = plane
            plane_name = {"1": "第一面", "2": "第二面", "3": "第三面"}.get(plane, f"第{plane}面")
            cards.append(f'<div class="plane-title">{plane_name}</div>')
        snap = n.get("FinalPreparationSnapshot") or {}
        state = n.get("FinalPreparationState") or {}
        battle = n.get("FinalBattle")
        prev_snap = (nodes[i - 1].get("FinalPreparationSnapshot") or {}) if i > 0 else {}

        gold = known_value(snap.get("Economy"))
        prev_gold = known_value(prev_snap.get("Economy"))
        gold_txt = f"{gold}" if gold is not None else "未记录"
        if gold is not None and prev_gold is not None and gold != prev_gold:
            gold_txt = f"{prev_gold} → {gold}"

        health = known_value(snap.get("Health"))
        prev_health = known_value(prev_snap.get("Health"))
        health_txt = f"{health}" if health is not None else "未记录"
        if health is not None and prev_health is not None:
            delta = health - prev_health
            health_txt = f"{health}（{delta:+d}）"

        act = known_value(snap.get("ActionPoints"))
        act_txt = f"{act}" if act is not None else "未记录"
        difficulty = known_value(state.get("EnemyDifficulty"))
        difficulty_txt = f"{difficulty}" if difficulty is not None else "未记录"
        store = known_value(snap.get("StoreLevel"))
        store_txt = f"{store}" if store is not None else "未记录"
        is_boss = bool((battle or {}).get("IsBossNode"))
        is_reward = bool((battle or {}).get("IsRewardNode"))
        is_encounter = bool((battle or {}).get("IsEncounterNode"))
        node_chip = ""
        if is_boss:
            node_chip = '<span class="boss-chip">首领关</span>'
        elif is_reward:
            node_chip = '<span class="reward-chip">奖励关</span>'
        elif is_encounter:
            node_chip = '<span class="encounter-chip">遭遇关</span>'
        dmg = battle.get("TotalDamage") if battle else None
        dmg_txt = f"{dmg / 10000:.1f}万" if dmg and dmg >= 10000 else (f"{dmg}" if dmg is not None else "未记录")

        reward = battle.get("GoldReward") if battle else None
        reward_txt = f"+{reward}" if reward is not None else "未记录"
        synergy_ids = (snap.get("SynergyIds") or {}).get("value") or []
        synergy_txt = " / ".join(bonds.get(s, s) for s in synergy_ids) or "未记录"
        dismantle = known_value(state.get("DismantleToolCount"))
        dismantle_txt = f"{dismantle}" if dismantle is not None else "未记录"
        special_ids = (snap.get("SpecialItemIds") or {}).get("value") or []
        prev_special_ids = []
        if i > 0:
            prev_special_ids = ((nodes[i - 1].get("FinalPreparationSnapshot") or {}).get("SpecialItemIds") or {}).get("value") or []
        new_items = [s for s in special_ids if s not in prev_special_ids]
        used_items = [s for s in prev_special_ids if s not in special_ids]
        special_icons = "".join(
            f'<span class="preset-item" style="margin-right:10px">{icon("special", s)}'
            f'<span style="font-size:12px">{html_mod.escape(specials.get(s, s))}</span></span>'
            for s in special_ids)
        special_changes = ""
        if new_items:
            names = "、".join(specials.get(s, s) for s in new_items)
            special_changes += f'<span style="color:#9be08a;font-size:12px;margin-right:12px">★ 新获得：{html_mod.escape(names)}</span>'
        if used_items:
            names = "、".join(specials.get(s, s) for s in used_items)
            special_changes += f'<span style="color:#e06060;font-size:12px">✗ 已使用：{html_mod.escape(names)}</span>'
        cur_strats = (snap.get("InvestmentStrategyIds") or {}).get("value") or []
        prev_snap_d = (nodes[i - 1].get("FinalPreparationSnapshot") or {}) if i > 0 else {}
        prev_strats = (prev_snap_d.get("InvestmentStrategyIds") or {}).get("value") or []
        new_strats = [s for s in cur_strats if s not in prev_strats]
        strat_gain_html = ""
        if new_strats:
            names = "、".join(strategy_label(s) for s in new_strats)
            strat_gain_html = f'<div class="strategy-gain">★ 在此节点获得了【{html_mod.escape(names)}】</div>'

        formation = (state.get("Formation") or {}).get("value") or []
        zones = {"Front": [], "Back": [], "Bench": []}
        for c in formation:
            if not isinstance(c, dict):
                continue
            cid = c.get("CharacterId")
            if not cid:
                continue
            zone = c.get("Zone", "Front")
            zone = {"Front": "Front", "Back": "Back", "Bench": "Bench"}.get(zone, "Front")
            equip_icons = "".join(icon("equipment", e) for e in (c.get("EquipmentIds") or [])[:3])
            star = c.get("StarLevel")
            star_html = f'<div class="unit-star">{"★" * int(star)}</div>' if star else ""
            zones[zone].append(
                f'<div class="unit"><div class="unit-card">{icon("character", cid)}'
                f'{star_html}'
                f'<div class="unit-name">{html_mod.escape(chars.get(cid, cid))}</div>'
                f'<div class="unit-equip">{equip_icons}</div></div></div>')

        def zone_row(label, zone):
            if not zones[zone]:
                return f'<div class="zone-row"><span class="zone-label">{label}</span><span class="zone-empty">未记录</span></div>'
            return f'<div class="zone-row"><span class="zone-label">{label}</span>{"".join(zones[zone])}</div>'

        bag = (snap.get("EquipmentIds") or {}).get("value") if isinstance(snap.get("EquipmentIds"), dict) else (snap.get("EquipmentIds") or [])
        bag_icons = "".join(icon("equipment", e) for e in (bag or [])[:10])

        cards.append(f"""
        <div class="node-card">
          <div class="node-head"><span class="node-id">{html_mod.escape(nid)}</span>{node_chip}{strat_gain_html}</div>
          <div class="node-stats">
            <div class="stat"><span class="stat-label">金币</span><span class="stat-value">{gold_txt}</span></div>
            <div class="stat"><span class="stat-label">血量</span><span class="stat-value">{health_txt}</span></div>
            <div class="stat"><span class="stat-label">行动</span><span class="stat-value">{act_txt}</span></div>
            <div class="stat"><span class="stat-label">商店</span><span class="stat-value">Lv{store_txt}</span></div>
            <div class="stat"><span class="stat-label">难度</span><span class="stat-value">{difficulty_txt}</span></div>
            <div class="stat"><span class="stat-label">结算</span><span class="stat-value">{reward_txt}</span></div>
            <div class="stat"><span class="stat-label">总伤害</span><span class="stat-value">{dmg_txt}</span></div>
          </div>
          <div class="zone-row"><span class="zone-label">羁绊</span><span style="font-size:12px;color:#91a3b4">{html_mod.escape(synergy_txt)}</span></div>
          <div class="zones">
            {zone_row("前台", "Front")}
            {zone_row("后台", "Back")}
            {zone_row("备战席", "Bench")}
          </div>
          <div class="bag"><span class="zone-label">物品栏未装备</span>{bag_icons if bag_icons else '<span class="zone-empty">无</span>'}</div>
          <div class="bag"><span class="zone-label">拆解工具</span><span style="font-size:12px;color:#91a3b4">{dismantle_txt}</span><span class="zone-label" style="margin-left:18px">特殊物品</span>{special_icons if special_icons else '<span class="zone-empty">无</span>'}{special_changes}</div>
        </div>""")

    env_name = envs.get(env_id, env_id) if env_id else "未记录"
    enemy_html = "".join(
        f'<span class="preset-item">{icon("competitor", c)}{html_mod.escape(comps.get(c, c))}</span>'
        for c in enemy_ids if c.startswith("competitor"))
    affix_html = "".join(
        f'<span class="preset-item">{icon("affix", a)}{html_mod.escape(affs.get(a, a))}</span>'
        for a in affix_ids)
    strat_html = "".join(
        f'<span class="preset-item">{icon("strategy", s)}{html_mod.escape(strategy_label(s))}</span>'
        for s in strat_ids) or '<span class="zone-empty">未记录（投资策略于 1-3 / 2-2 / 3-2 固定获取）</span>'

    # 专家顾问：识别到的专家角色（使用邀请函后进入商店，识别到即视为已解锁；
    # 程序只需识别专家角色本身，不需要识别邀请函使用时机）
    advisor_count = 0
    for n in nodes:
        a = ((n.get("FinalPreparationSnapshot") or {}).get("ExpertAdvisorIds") or {}).get("value") or []
        advisor_count = max(advisor_count, len(a))
    advisor_html = (f'<span style="font-size:13px;color:#f3f7fa">已解锁 {advisor_count} 名专家（进入商店）</span>'
                    if advisor_count else '<span class="zone-empty">未记录</span>')

    technical = [
        ("运行 ID", cr.get("RunId", "")),
        ("数据 Schema", cr.get("SchemaVersion", "")),
        ("完成时间", cr.get("CompletedAt", "")),
        ("结束页面", cr.get("CompletionPageId", "")),
        ("结束节点", cr.get("CompletionNodeId", "")),
        ("结束截图", cr.get("CompletionScreenshotFile", "")),
        ("对局评级", cr.get("RatingText", "")),
        ("来源分析文件", ", ".join(cr.get("SourceAnalysisFiles", []) or [])),
    ]
    tech_html = "".join(
        f'<div class="tech-row"><span class="tech-label">{html_mod.escape(k)}</span><span class="tech-value">{html_mod.escape(str(v))}</span></div>'
        for k, v in technical)

    html_doc = f"""<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8" />
<meta http-equiv="X-UA-Compatible" content="IE=edge" />
<title>货币战争对局报告 · {html_mod.escape(run_id)}</title>
<style>
  body {{ margin:0; background:#0e1621; color:#f3f7fa; font-family:"Microsoft YaHei UI",sans-serif; }}
  .wrap {{ max-width:980px; margin:0 auto; padding:28px 24px 60px; }}
  h1 {{ font-size:30px; margin:0 0 4px; color:#e7ca82; }}
  .sub {{ color:#91a3b4; font-size:14px; margin-bottom:22px; }}
  .section {{ border-left:3px solid #e7ca82; padding:4px 0 4px 14px; margin:26px 0 12px; }}
  .section h2 {{ margin:0; font-size:19px; color:#e7ca82; }}
  .preset {{ background:#111c28; border:1px solid #2a3a4c; border-radius:8px; padding:14px 16px; }}
  .preset-row {{ margin:8px 0; }}
  .preset-label {{ color:#83d4e3; font-size:13px; margin-right:10px; }}
  .preset-item {{ display:inline-block; margin-right:16px; font-size:14px; vertical-align:middle; }}
  .icon {{ width:26px; height:26px; margin-right:5px; border-radius:4px; }}
  .charts {{ font-size: 0; }}
  .chart {{ display:inline-block; width:49%; vertical-align:top; font-size:12px; box-sizing:border-box; background:#111c28; border:1px solid #2a3a4c; border-radius:8px; padding:10px 12px; margin-bottom:12px; }}
  .chart-title {{ color:#91a3b4; font-size:13px; margin-bottom:6px; }}
  .chart-empty {{ color:#5a6b7d; font-size:13px; padding:12px 4px; }}
  .grid {{ stroke:#22303f; stroke-width:1; }}
  .xlabel {{ fill:#5a6b7d; font-size:10px; }}
  .ylabel {{ fill:#5a6b7d; font-size:10px; }}
  .node-card {{ background:#111c28; border:1px solid #2a3a4c; border-radius:8px; padding:14px 16px; margin-bottom:14px; }}
  .node-head {{ margin-bottom:8px; }}
  .node-id {{ color:#e7ca82; font-size:18px; font-weight:bold; }}
  .reward-chip {{ display:inline-block; margin-left:10px; padding:1px 8px; border-radius:3px; background:#5a3a2a; color:#ffd9a0; font-size:11px; vertical-align:middle; }}
  .boss-chip {{ display:inline-block; margin-left:10px; padding:1px 8px; border-radius:3px; background:#5a2020; color:#ffb0a0; font-size:11px; vertical-align:middle; }}
  .encounter-chip {{ display:inline-block; margin-left:10px; padding:1px 8px; border-radius:3px; background:#1f4a3a; color:#a0e0c0; font-size:11px; vertical-align:middle; }}
  .plane-title {{ margin:18px 0 10px; font-size:17px; color:#83d4e3; font-weight:600; border-bottom:1px solid #2a3a4c; padding-bottom:4px; }}
  .node-stats {{ margin-bottom:10px; }}
  .stat {{ display:inline-block; margin-right:24px; }}
  .stat-label {{ color:#83d4e3; font-size:12px; margin-right:6px; }}
  .stat-value {{ font-size:16px; font-weight:600; }}
  .zone-row {{ margin:6px 0; }}
  .zone-label {{ color:#83d4e3; font-size:12px; display:inline-block; width:64px; vertical-align:middle; }}
  .zone-empty {{ color:#5a6b7d; font-size:12px; }}
  .unit {{ margin-right:10px; }}
  .unit-card {{ text-align:center; }}
  .unit-card .icon {{ width:46px; height:46px; }}
  .unit-name {{ font-size:11px; color:#91a3b4; max-width:52px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }}
  .unit-equip .icon {{ width:16px; height:16px; margin:1px; }}
  .unit-star {{ color:#e7ca82; font-size:11px; line-height:1; }}
  .strategy-gain {{ margin-top:4px; color:#e7ca82; font-size:13px; font-weight:600; }}
  .bag {{ display:flex; align-items:center; margin-top:8px; }}
  .bag .icon {{ width:24px; height:24px; }}
  .technical {{ margin-top:34px; background:#0a1118; border:1px dashed #2a3a4c; border-radius:8px; padding:10px 14px; }}
  .technical summary {{ color:#5a6b7d; cursor:pointer; font-size:13px; }}
  .tech-row {{ display:flex; margin:4px 0; }}
  .tech-label {{ color:#5a6b7d; font-size:11px; width:110px; flex:none; }}
  .tech-value {{ color:#71849a; font-size:11px; word-break:break-all; }}
</style></head><body><div class="wrap">
  <h1>货币战争 · 对局报告</h1>
  <div class="sub">对局时间 {html_mod.escape((cr.get("CompletedAt") or "")[:19].replace("T", " "))} · 评级 {html_mod.escape(cr.get("RatingText") or "未记录")} · 结束 {html_mod.escape(cr.get("CompletionNodeId") or "未记录")}</div>

  <div class="section"><h2>本局预设</h2></div>
  <div class="preset">
    <div class="preset-row"><span class="preset-label">投资环境</span>{icon("environment", env_id)}<span style="font-size:15px">{html_mod.escape(env_name)}</span></div>
    <div class="preset-row"><span class="preset-label">敌人阵营</span>{enemy_html or '<span class="zone-empty">未记录</span>'}</div>
    <div class="preset-row"><span class="preset-label">负面词条</span>{affix_html or '<span class="zone-empty">未记录</span>'}</div>
    <div class="preset-row"><span class="preset-label">投资策略</span>{strat_html}</div>
    <div class="preset-row"><span class="preset-label">专家顾问</span>{advisor_html}</div>
  </div>

  <div class="section"><h2>数值趋势</h2></div>
  <div class="charts">
    {svg_line(dmg_series, "#e7ca82", "总伤害", "万")}
    {svg_line(gold_series, "#83d4e3", "金币", "")}
    {svg_line(health_series, "#e06060", "血量", "")}
    {svg_line(act_series, "#9be08a", "行动值", "")}
  </div>

  <div class="section"><h2>节点明细</h2></div>
  {''.join(cards)}

  <details class="technical"><summary>调试信息（运行 ID / 来源文件等）</summary>
    {tech_html}
  </details>
  <div style="margin-top:26px;color:#7f8ba5;font-size:12px;text-align:center">交流QQ群：726898246 · 官网：https://taskflowai.cn</div>
</div></body></html>"""

    out = out_path
    out_dir = os.path.dirname(out)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        f.write(html_doc)
    print("已生成:", out)


if __name__ == "__main__":
    main()

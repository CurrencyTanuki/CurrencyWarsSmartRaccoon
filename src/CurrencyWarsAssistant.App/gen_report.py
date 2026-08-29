#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成《货币战争对局报告》HTML 原型（数据来自 completed-run 存档 + data/4.4 名称映射）。"""
import json
from collections import Counter
import os
import sys
import glob
import html as html_mod
import urllib.parse

# 数据目录解析（2026-08-06 修复）：部署时 gen_report.py 与 data/ 同级
#（csproj Link="data\..." 拷到输出目录）；源码开发时数据在项目根 data/。
# 从脚本目录逐级向上找第一个含 data/4.4 的目录（部署=脚本目录本身，
# 源码=项目根），避免旧逻辑 dirname(__file__) 两次在部署环境解析到
# bin/Debug（少一级）导致 bonds/characters 全部 name_map 加载失败
#（羁绊显示原始 id、角色名显示原始 id/未知——用户实测）。
def _resolve_base(start_dir: str) -> str:
    current = start_dir
    while True:
        if os.path.isdir(os.path.join(current, "data", "4.4")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return start_dir
        current = parent

_SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BASE = _resolve_base(_SCRIPT_DIR)
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


def unique_character_cost_map(path):
    """只返回静态目录中费用唯一的角色；多费用角色必须依赖当前卡面识别。"""
    try:
        data = load_json(path)
        items = data if isinstance(data, list) else data.get("characters")
        if not isinstance(items, list):
            return {}
        result = {}
        for item in items:
            if not isinstance(item, dict) or not item.get("id"):
                continue
            costs = item.get("costs")
            if not isinstance(costs, list) or not costs:
                continue
            unique_costs = {int(cost) for cost in costs}
            if len(unique_costs) == 1:
                result[str(item["id"])] = next(iter(unique_costs))
        return result
    except (OSError, TypeError, ValueError, json.JSONDecodeError):
        return {}


def equipment_name_map():
    """读取正式 runtime 装备名称；缺失或损坏时由调用方使用可读降级文案。"""
    path = os.path.join(
        BASE, "data", "runtime", "1.0.0", "4.4", "equipment", "equipment.json")
    try:
        data = load_json(path)
        records = data.get("records") if isinstance(data, dict) else None
        if not isinstance(records, list):
            return {}
        return {
            str(item["id"]): str(item["name"])
            for item in records
            if isinstance(item, dict) and item.get("id") and item.get("name")
        }
    except Exception:
        return {}


def obs_value(obs):
    """Observation 的 value，兼容 PascalCase（Value）与 camelCase（value）。
    纯 list（如 SpecialItemIds 直接就是 string[]）原样返回。"""
    if isinstance(obs, list):
        return obs
    if not isinstance(obs, dict):
        return None
    return obs.get("value") if "value" in obs else obs.get("Value")

def known_value(obs):
    if obs is None:
        return None
    if isinstance(obs, dict):
        # 兼容大小写：实时报告（PascalCase "Status"/"Known"）与
        # completed-run（camelCase "status"/"known"）两种序列化。
        # 上游语义：status=Unknown 表示未识别到（不画该点）；
        # status=Known 且 value=0 表示真实数值就是 0（正常画）。
        status = obs.get("status") or obs.get("Status")
        value = obs.get("value") if "value" in obs else obs.get("Value")
        return value if str(status).lower() == "known" else None
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

    character_path = os.path.join(DATA4, "currency-wars-characters.json")
    chars = name_map(character_path)
    character_costs = unique_character_cost_map(character_path)
    envs = name_map(os.path.join(DATA4, "investment-environments.json"))
    affs = name_map(os.path.join(DATA4, "enemy-affixes.json"))
    comps = name_map(os.path.join(DATA4, "competitors.json"))
    strs = name_map(os.path.join(DATA4, "investment-strategies.json"))
    equipment_names = equipment_name_map()
    chars["special_unit_peipei"] = "佩佩"
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
    bond_tiers = {}
    try:
        with open(os.path.join(DATA4, "currency-wars-characters.json"), encoding="utf-8-sig") as fp:
            bond_catalog = json.load(fp).get("bond_catalog") or []
        bonds = {b.get("id"): b.get("name", b.get("id"))
                 for b in bond_catalog if isinstance(b, dict) and b.get("id")}
        # 分析器（Phase2OperationalScreenshotAnalyzer）的羁绊目录 id 造为
        # "bond_{中文名}"（如 bond_头号玩家），与 bond_catalog 的
        # "currency_wars_bond_NN" 是两套体系——补一套 "bond_{name}" 键，
        # 否则 SynergyIds 里的 bond_xxx 显示原始 id（用户实测 0.2.836）。
        for b in bond_catalog:
            if isinstance(b, dict) and b.get("name"):
                bonds[f"bond_{b['name']}"] = b["name"]
            if not isinstance(b, dict):
                continue
            thresholds = sorted(
                tier.get("required_members")
                for tier in (b.get("tier_effects") or [])
                if isinstance(tier, dict)
                and isinstance(tier.get("required_members"), int))
            for key in (b.get("id"), f"bond_{b.get('name')}", b.get("name")):
                if key:
                    bond_tiers[key] = thresholds
    except Exception:
        bonds = {}
        bond_tiers = {}
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
    # 用户 2026-08-11：补给节点 = 1-5、2-3、3-3（仅页面无备战/战斗，不显示）；
    # 2-5 / 3-5 是遭遇关（有备战+战斗），必须显示。
    skip_ids = {"investment_strategy_selection", "node_complete", "node_failed",
                "opening_enemy_overview", "opening_investment_environment",
                "normal_hud", "reward_shop", "1-5", "2-3", "3-3"}
    nodes = []
    for n in cr.get("Nodes", []):
        nid = n.get("NodeId", "")
        if nid in skip_ids:
            continue
        nodes.append(n)

    def icon(cat, iid):
        if not iid:
            return ""
        # 头像/图标用绝对 file:/// 路径（基于 DATA4，部署/源码都正确）。
        # 旧逻辑用相对路径 ../data/...，而实时报告输出在 %TEMP% 下，
        # 相对路径解析失败 → onerror 隐藏 → 无头像/图标（用户实测
        # "原本已做完的头像功能又丢失"，P2-10）。
        _data4_abs = os.path.abspath(DATA4)
        if cat == "character":
            # 多数角色模板是 {iid}__default.png，但 character_24/48/trailblazer
            # 只有 {iid}__幽梦翩跹.png 等变体（无 __default）——用 glob 兜底
            # 找第一个 {iid}__*.png（review 抓到，否则这些角色头像仍丢失）。
            candidates = sorted(glob.glob(
                os.path.join(_data4_abs, "character-card-templates", f"{iid}__*.png")))
            p = candidates[0] if candidates else os.path.join(
                _data4_abs, "character-card-templates", f"{iid}__default.png")
        elif cat == "environment":
            p = os.path.join(_data4_abs, "phase2-icon-assets", "standardized", "investment_environment", f"{iid}.png")
        elif cat == "affix":
            p = os.path.join(_data4_abs, "phase2-icon-assets", "standardized", "enemy_affix", f"{iid}.png")
        elif cat == "competitor":
            return ""  # 阵营无可用图标（数据只有名称），只显示名称
        elif cat == "strategy":
            p = os.path.join(_data4_abs, "phase2-icon-assets", "standardized", "investment_strategy", f"{iid}.png")
        elif cat == "equipment":
            # 装备图标在 data/raw/4.4/...（csproj 拷 data\**），不是 data/4.4
            # 的上级——旧实现 dirname(DATA4)/raw 少一层 data/ 必失败（review 抓到）。
            p = os.path.join(
                BASE, "data", "raw", "4.4", "equipment",
                "890ae486642e979b", "assets",
                "currency_wars_equipment_icons", f"{iid}.png")
        elif cat == "special" and str(iid).startswith("currency_wars_equipment_"):
            # 实战识别会把垃圾袋、冶金炉、拆装扳手等特殊物品保存为
            # currency_wars_equipment_*；它们的真实图片仍位于装备资源目录。
            p = os.path.join(
                BASE, "data", "raw", "4.4", "equipment",
                "890ae486642e979b", "assets",
                "currency_wars_equipment_icons", f"{iid}.png")
        elif cat == "special":
            p = os.path.join(_data4_abs, "phase2-icon-assets", "standardized", "special_item", f"{iid}.png")
        else:
            return ""
        p = "file:///" + urllib.parse.quote(p.replace("\\", "/"))
        return f'<img class="icon" src="{p}" onerror="this.style.display=&#39;none&#39;" />'

    unknown_equipment_name = "未知装备（待核对）"
    virus_firewall_ids = {
        "currency_wars_equipment_034",
        "currency_wars_equipment_035",
    }

    def field(item, pascal_name, camel_name, default=None):
        if not isinstance(item, dict):
            return default
        if pascal_name in item:
            return item.get(pascal_name)
        return item.get(camel_name, default)

    def final_action_value(battle, snapshot):
        """Return the settled action value, not a preparation-page surrogate."""
        if isinstance(battle, dict):
            remaining = field(
                battle, "RemainingActionValue", "remainingActionValue")
            return field(
                remaining, "TotalActionValue", "totalActionValue")
        return known_value(
            snapshot.get("ActionPoints") or snapshot.get("actionPoints"))

    def safe_equipment_name(equipment_id):
        return equipment_names.get(str(equipment_id), unknown_equipment_name)

    def equipment_item(equipment_id, is_privileged=False):
        known = str(equipment_id) in equipment_names
        # 用户 2026-08-11：装备名称一律不显示，只显示图标。
        # 图标缺失（未知装备）时不渲染，避免重新出现空框。
        if not known:
            return ""
        equipment_icon = icon("equipment", equipment_id) if known else ""
        privilege_badge = (
            '<span class="privilege-badge">特权</span>'
            if is_privileged else '')
        return (f'<span class="equipment-item">{equipment_icon}'
                f'{privilege_badge}'
                f'</span>')

    def candidate_names(candidate_ids, display_names=None, special=False):
        result = []
        for equipment_id in candidate_ids or []:
            name = safe_equipment_name(equipment_id)
            if special and str(equipment_id) in virus_firewall_ids:
                name = "病毒防火墙（版本不区分）"
            if name not in result:
                result.append(name)
        if not result:
            for name in display_names or []:
                safe_name = str(name).strip()
                if (not safe_name or
                        safe_name.startswith("currency_wars_equipment_")):
                    safe_name = unknown_equipment_name
                if special and safe_name.startswith("病毒防火墙"):
                    safe_name = "病毒防火墙（版本不区分）"
                if safe_name not in result:
                    result.append(safe_name)
        return result or [unknown_equipment_name]

    def candidate_equipment_item(candidate_ids, display_names=None):
        # 候选只是诊断证据，不是已装备物品；前台不渲染未知空框。
        return ''

    def special_equipment_view(special_state, legacy_ids):
        equipment_ids = []
        display_names = []
        if isinstance(special_state, dict):
            occupancy = str(field(
                special_state,
                "Occupancy",
                "occupancy",
                "")).lower()
            equipment_id = field(
                special_state,
                "EquipmentId",
                "equipmentId")
            candidates = field(
                special_state,
                "CandidateEquipmentIds",
                "candidateEquipmentIds",
                []) or []
            display_names = field(
                special_state,
                "CandidateDisplayNames",
                "candidateDisplayNames",
                []) or []
            if occupancy == "equipped" and equipment_id:
                equipment_ids = [equipment_id]
            # Candidate identities describe one unresolved physical slot.
        elif legacy_ids:
            equipment_ids = list(legacy_ids)

        if not equipment_ids and not display_names:
            return "", ""

        # 用户 2026-08-11：特殊装备不用显示名称，只显示图标。
        icons = "".join(
            icon("equipment", equipment_id)
            for equipment_id in equipment_ids
            if str(equipment_id) in equipment_names)
        if not icons:
            return "", ""
        return icons, ""

    def has_action_limit(node_id):
        return node_id not in {"1-1", "1-2", "1-8", "2-6", "3-6"}

    def compact_damage(value):
        if value is None:
            return "未记录"
        if value >= 100_000_000:
            return f"{value / 100_000_000:.2f}".rstrip("0").rstrip(".") + "亿"
        if value >= 10_000:
            return f"{value / 10_000:.1f}".rstrip("0").rstrip(".") + "万"
        return f"{value}"

    # ---- 数值序列（伤害/理论/金币/行动）----
    gold_series, theory_series, dmg_series, act_series = [], [], [], []
    for n in nodes:
        snap = n.get("FinalPreparationSnapshot") or {}
        battle = n.get("FinalBattle")
        node_id = n.get("NodeId", "")
        gold_series.append(known_value(snap.get("Economy")))
        theory_series.append(
            battle.get("TheoreticalDamageLimit")
            if battle and has_action_limit(node_id)
            else None)
        act_series.append(final_action_value(battle, snap))
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
        gold_txt = f"{gold}" if gold is not None else "未记录"

        streak = known_value(snap.get("CumulativeSpend"))
        streak_txt = f"{streak}" if streak is not None else "未记录"

        health = known_value(snap.get("Health"))
        health_txt = f"{health}" if health is not None else "未记录"

        act = final_action_value(battle, snap)
        act_txt = f"{act}" if act is not None else "未记录"
        difficulty = known_value(state.get("EnemyDifficulty"))
        difficulty_txt = f"{difficulty}" if difficulty is not None else "未记录"
        population = known_value(state.get("Population"))
        population_txt = f"{population}" if population is not None else "未记录"
        store = known_value(snap.get("StoreLevel"))
        store_txt = f"{store}" if store is not None else "未记录"
        is_boss = bool((battle or {}).get("IsBossNode"))
        # 奖励关判定用节点号硬编码规则（用户 2026-08-06 确认）：
        # 1-1、1-2、1-8、2-6、3-6 是奖励关。不能再用 battle.IsRewardNode
        # 字段（页面类型语义：reward_battle/battle_generic 实机几乎全 True，
        # 会把 1-3/1-4 等普通战斗节点全部误标成奖励关——用户实测 0.2.836）。
        is_reward = nid in {"1-1", "1-2", "1-8", "2-6", "3-6"}
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
        theory = battle.get("TheoreticalDamageLimit") if battle else None
        theory_txt = compact_damage(theory) if has_action_limit(nid) else "不适用"

        # SynergyIds 是含 status 的 observation，normalize 不转换其键
        #（保持 camelCase "synergyIds"），与 snapshot 顶层（PascalCase）不同，
        # 必须兼容大小写，否则羁绊显示原始 id（用户实测，P2-9）。
        active_synergies = obs_value(
            state.get("ActiveSynergies") or state.get("activeSynergies")) or []
        synergy_parts = []
        for synergy in active_synergies:
            if not isinstance(synergy, dict):
                continue
            synergy_id = synergy.get("SynergyId") or synergy.get("synergyId")
            active_count = synergy.get("ActiveCount")
            if active_count is None:
                active_count = synergy.get("activeCount")
            next_threshold = synergy.get("NextThreshold")
            if next_threshold is None:
                next_threshold = synergy.get("nextThreshold")
            if not synergy_id:
                continue
            thresholds = bond_tiers.get(synergy_id, [])
            thresholds = [t for t in thresholds if isinstance(t, int)] or []
            tier = sum(1 for threshold in thresholds
                       if isinstance(active_count, int)
                       and active_count >= threshold)
            # 用户 2026-08-11：羁绊显示 "羁绊名 当前数/下一档数"（如 欢愉 3/4）；
            # 已满档显示最高档数字（如 欢愉 4/4），不再用分析器给的 NextThreshold
            #（可能与本地 bond_tiers 档位不一致）。
            if isinstance(active_count, int) and thresholds:
                max_tier = thresholds[-1]
                target = max_tier if active_count >= max_tier else next(
                    (t for t in thresholds if t > active_count), max_tier)
                ratio_text = f"{active_count}/{target}"
            elif isinstance(active_count, int):
                # 无档位数据降级：只显示人数，不带 /下一档
                ratio_text = f"{active_count}人"
            else:
                ratio_text = "未记录"
            tier_text = f"第{tier}档" if tier > 0 else "未激活"
            synergy_parts.append(
                f"{html_mod.escape(bonds.get(synergy_id, synergy_id))} {ratio_text} · {tier_text}")
        if synergy_parts:
            synergy_txt = " / ".join(synergy_parts)
        else:
            synergy_ids = obs_value(
                snap.get("SynergyIds") or snap.get("synergyIds")) or []
            synergy_txt = " / ".join(
                html_mod.escape(bonds.get(s, s)) for s in synergy_ids) or "未记录"
        inventory_special_ids = obs_value(snap.get("SpecialItemIds")) or []
        prev_inventory_special_ids = []
        if i > 0:
            prev_inventory_special_ids = obs_value((nodes[i - 1].get("FinalPreparationSnapshot") or {}).get("SpecialItemIds")) or []
        new_items = [s for s in inventory_special_ids if s not in prev_inventory_special_ids]
        used_items = [s for s in prev_inventory_special_ids if s not in inventory_special_ids]
        # 用户 2026-08-11：备战席特殊物品（专家邀请函/武装箱/聘用书）与
        # 物品栏特殊物品（特权卡/好运令牌/冶金炉/扳手）是不同字段、分开显示。
        # 备战席物品直接合并显示到备战席栏；其余归"特殊物品"栏。
        bench_special_ids = {
            "special_item_001",   # 专家邀请函
            "special_item_004",   # 简易武装箱
            "special_item_005",   # 进阶武装箱
            "special_item_006",   # 特权武装箱
            "special_item_020",   # 3费聘用书
            "special_item_021",   # 4费聘用书
            "special_item_022",   # 5费聘用书
        }
        bench_special_items = [s for s in inventory_special_ids if s in bench_special_ids]
        inventory_special_ids = [s for s in inventory_special_ids if s not in bench_special_ids]
        # 用户 2026-08-11：可堆叠特殊物品（冶金炉/扳手）只渲染一个图标 +
        # 左下角堆叠数量（像游戏里一样）；不可堆叠（好运令牌/特权赋予卡）
        # 多个时分开渲染多个图标。
        stackable_special_ids = {
            "special_item_007",   # 拆装扳手
            "special_item_008",   # 精密拆装扳手
            "special_item_010",   # 冶金炉
            "currency_wars_equipment_149",  # 冶金炉（实战识别 ID）
            "currency_wars_equipment_153",  # 拆装扳手（实战识别 ID）
        }
        special_counts = Counter(inventory_special_ids)
        inventory_special_icons_parts = []
        for sid, count in special_counts.items():
            item_icon = icon("special", sid)
            count_badge = (
                f'<span class="stack-count">{count}</span>'
                if count > 1 and sid in stackable_special_ids else '')
            if count > 1 and sid in stackable_special_ids:
                # 可堆叠：一个图标 + 数量（左下角）
                inventory_special_icons_parts.append(
                    f'<span class="preset-item stackable special" style="margin-right:10px">'
                    f'{item_icon}{count_badge}</span>')
            else:
                # 不可堆叠：每个一个图标
                for _ in range(count):
                    inventory_special_icons_parts.append(
                        f'<span class="preset-item special" style="margin-right:10px">{item_icon}</span>')
        inventory_special_icons = "".join(inventory_special_icons_parts)
        special_changes = ""
        if new_items:
            gained_icons = "".join(
                f'<span class="preset-item special" style="margin-right:6px">'
                f'{icon("special", item_id)}</span>'
                for item_id in new_items)
            special_changes += (
                '<span style="color:#9be08a;font-size:14px;margin-right:6px">'
                '★ 新获得：</span>' + gained_icons)
        if used_items:
            used_icons = "".join(
                f'<span class="preset-item special" style="margin-right:6px">'
                f'{icon("special", item_id)}</span>'
                for item_id in used_items)
            special_changes += (
                '<span style="color:#e06060;font-size:14px;margin-right:6px">'
                '✗ 已使用：</span>' + used_icons)
        cur_strats = obs_value(snap.get("InvestmentStrategyIds")) or []
        prev_snap_d = (nodes[i - 1].get("FinalPreparationSnapshot") or {}) if i > 0 else {}
        prev_strats = obs_value(prev_snap_d.get("InvestmentStrategyIds")) or []
        new_strats = [s for s in cur_strats if s not in prev_strats]
        strat_gain_html = ""
        if new_strats:
            names = "、".join(strategy_label(s) for s in new_strats)
            strat_gain_html = f'<div class="strategy-gain">★ 在此节点获得了【{html_mod.escape(names)}】</div>'

        # Formation 是含 status 的 observation dict，normalize() 不转换其键
        #（保持 camelCase "formation"），而 state 顶层经 normalize 后是
        # "FinalPreparationState"——这里必须同时兼容大小写，否则取不到阵容，
        # 所有节点前台/后台/备战席都显示"未记录"（用户实测）。
        formation = obs_value(
            state.get("Formation") or state.get("formation")) or []
        zones = {"Front": [], "Back": [], "Bench": []}
        for c in formation:
            if not isinstance(c, dict):
                continue
            # formation value 元素是 camelCase（characterId/zone/starLevel），
            # 因为 observation dict 含 status → normalize 不转换其内部 value；
            # 顶层 state 经 normalize 是 PascalCase。这里全部兼容两种大小写。
            cid = c.get("CharacterId") or c.get("characterId")
            if not cid:
                continue
            # 未识别占位槽不属于可确认阵容。保留在识别证据中供跨帧融合，
            # 但不能在玩家报告里伪装成真实角色。
            if str(cid).startswith("unknown-formation-unit"):
                continue
            zone = c.get("Zone") or c.get("zone") or "Front"
            # 序列化既有 PascalCase（"Front"）也有 camelCase（"front"/"back"/
            # "bench"）两种；统一归一化。佩佩/狸猫等特殊单位(z=Special)在报告中
            # 不是竞技席角色：用户方案(2026-08-18)——它们只由后台识别并仅标"后台"；
            # 历史/旧数据残留的 zone=Special 不当作竞技席角色渲染（避免"佩佩跑竞技席"）。
            zone = str(zone).strip().capitalize()
            if zone in ("Special", "Horizontal"):
                continue
            zone = {
                "Front": "Front",
                "Back": "Back",
                "Bench": "Bench",
            }.get(zone, "Front")
            # 从 equipmentSlots 渲染已确认装备图标；empty/unknown 不渲染。
            slots_raw = c.get("EquipmentSlots") or c.get("equipmentSlots") or []
            equip_cells = []
            for s in slots_raw[:3]:
                if not isinstance(s, dict):
                    continue
                occ = str(s.get("Occupancy") or s.get("occupancy") or "").lower()
                eid = s.get("TemplateId") or s.get("templateId") or s.get("EquipmentId") or s.get("equipmentId")
                if occ == "equipped" and eid:
                    is_privileged = field(
                        s, "IsPrivileged", "isPrivileged") is True
                    equip_cells.append(equipment_item(eid, is_privileged))
            # 用户 2026-08-11：不再补足 3 个空框——角色带几个装备就显示
            # 几个图标，未装备的槽位不渲染（避免一排空框不美观）。
            equip_icons = "".join(equip_cells)
            # 用户 2026-08-11：备战席角色未扫描装备，不显示装备区
            #（避免无数据空区误导）。
            if zone == "Bench":
                equip_icons = None
            star = c.get("StarLevel") or c.get("starLevel")
            star_html = f'<div class="unit-star">{"★" * int(star)}</div>' if star else ""
            current_cost = field(c, "CurrentCost", "currentCost")
            if current_cost is None:
                current_cost = character_costs.get(cid)
            cost_html = (
                f'<span class="cost-badge">{int(current_cost)}费</span>'
                if current_cost else "")
            # 打call应援标记（用户 2026-08-07）：被开拓者•欢愉加持的角色
            #（卡牌两侧应援棒特效）显示"应援"徽标。
            cheered = bool(c.get("IsCheered") or c.get("isCheered"))
            cheer_html = '<span class="cheer-badge">应援</span>' if cheered else ""
            # 猎星人标记（星核猎手羁绊，非装备）：卡牌左上角金色徽章。
            hunter = bool(c.get("IsHunterStar") or c.get("isHunterStar"))
            hunter_html = '<span class="hunter-badge">猎星人</span>' if hunter else ""
            # 特殊装备（不占槽位，卡牌右上角：骇客改件等）——图标渲染
            #（特权版 icon 自带 V字+彩色描边）。
            character_special_ids = c.get("SpecialEquipmentIds") or c.get("specialEquipmentIds") or []
            special_state = c.get("SpecialEquipment") or c.get("specialEquipment")
            character_special_icons, special_label = special_equipment_view(
                special_state,
                character_special_ids)
            zones[zone].append(
                f'<div class="unit"><div class="unit-card">{icon("character", cid)}'
                f'{star_html}'
                f'<div class="unit-special">{character_special_icons}</div>'
                f'<div class="unit-name">{cheer_html}{hunter_html}{cost_html}</div>'
                + (f'<div class="unit-equip">{equip_icons}</div>' if equip_icons else '')
                + f'{special_label}</div></div>')

        def zone_row(label, zone):
            if not zones[zone]:
                return f'<div class="zone-row"><span class="zone-label">{label}</span><span class="zone-empty">未记录</span></div>'
            return f'<div class="zone-row"><span class="zone-label">{label}</span>{"".join(zones[zone])}</div>'

        bench_special_icons = "".join(
            f'<span class="preset-item special" style="margin-right:10px">{icon("special", s)}'
            f'<span style="font-size:14px">{html_mod.escape(specials.get(s, s))}</span></span>'
            for s in bench_special_items)
        bench_special_html = (
            f'<div class="zone-row"><span class="zone-label">备战席物品</span>'
            f'{bench_special_icons}</div>'
            if bench_special_icons else '')

        inventory_observation = (
            state.get("InventorySlots") or state.get("inventorySlots")
            or snap.get("InventorySlots") or snap.get("inventorySlots"))
        inventory_slots = obs_value(inventory_observation)
        bag_known = isinstance(inventory_slots, list)
        bag = []
        if bag_known:
            for slot in inventory_slots:
                if not isinstance(slot, dict):
                    continue
                occupancy = str(field(slot, "Occupancy", "occupancy", "")).lower()
                item_id = field(slot, "ItemId", "itemId")
                if occupancy == "equipped" and item_id and str(item_id) in equipment_names:
                    bag.append(item_id)
        bag_icons = "".join(equipment_item(e) for e in bag[:10])
        bag_empty = "无" if bag_known else "未记录"
        cards.append(f"""
        <div class="node-card">
          <div class="node-head"><span class="node-id">{html_mod.escape(nid)}</span>{node_chip}{strat_gain_html}</div>
          <div class="node-stats">
            <div class="stat"><span class="stat-label">阵容理论出伤极限</span><span class="stat-value">{theory_txt}</span></div>
            <div class="stat"><span class="stat-label">金币</span><span class="stat-value">{gold_txt}</span></div>
            <div class="stat"><span class="stat-label">连胜</span><span class="stat-value">{streak_txt}</span></div>
            <div class="stat"><span class="stat-label">血量</span><span class="stat-value">{health_txt}</span></div>
            <div class="stat"><span class="stat-label">行动</span><span class="stat-value">{act_txt}</span></div>
            <div class="stat"><span class="stat-label">商店</span><span class="stat-value">Lv{store_txt}</span></div>
            <div class="stat"><span class="stat-label">人口</span><span class="stat-value">{population_txt}</span></div>
            <div class="stat"><span class="stat-label">难度</span><span class="stat-value">{difficulty_txt}</span></div>
            <div class="stat"><span class="stat-label">总伤害</span><span class="stat-value">{dmg_txt}</span></div>
          </div>
          <div class="zone-row"><span class="zone-label">羁绊</span><span style="font-size:14px;color:#91a3b4">{html_mod.escape(synergy_txt)}</span></div>
          <div class="zones">
            {zone_row("前台", "Front")}
            {zone_row("后台", "Back")}
            {zone_row("备战席", "Bench")}
            {bench_special_html}
          </div>
          <div class="bag"><span class="zone-label">物品栏未装备</span>{bag_icons if bag_icons else f'<span class="zone-empty">{bag_empty}</span>'}</div>
          <div class="bag"><span class="zone-label">特殊物品</span>{inventory_special_icons if inventory_special_icons else '<span class="zone-empty">无</span>'}{special_changes}</div>
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
  body {{ margin:0; background:#0e1621; color:#f3f7fa; font-family:"Bahnschrift","黑体","SimHei","Microsoft YaHei UI","Microsoft YaHei",sans-serif; }}
  .wrap {{ max-width:980px; margin:0 auto; padding:28px 24px 60px; }}
  h1 {{ font-size:36px; margin:0 0 4px; color:#e7ca82; }}
  .sub {{ color:#91a3b4; font-size:16px; margin-bottom:22px; }}
  .section {{ border-left:3px solid #e7ca82; padding:4px 0 4px 14px; margin:26px 0 12px; }}
  .section h2 {{ margin:0; font-size:24px; color:#e7ca82; }}
  .preset {{ background:#111c28; border:1px solid #2a3a4c; border-radius:8px; padding:14px 16px; }}
  .preset-row {{ margin:8px 0; }}
  .preset-label {{ color:#83d4e3; font-size:15px; margin-right:10px; }}
  .preset-item {{ display:inline-block; margin-right:16px; font-size:16px; vertical-align:middle; }}
  /* 特殊物品图标放大（用户 2026-08-11）：比普通装备图标大，便于辨认 */
  .preset-item.special .icon {{ width:40px; height:40px; margin-right:6px; border-radius:6px; }}
  .icon {{ width:26px; height:26px; margin-right:5px; border-radius:4px; }}
  .charts {{ font-size: 0; }}
  .chart {{ display:inline-block; width:49%; vertical-align:top; font-size:14px; box-sizing:border-box; background:#111c28; border:1px solid #2a3a4c; border-radius:8px; padding:10px 12px; margin-bottom:12px; }}
  .chart-title {{ color:#91a3b4; font-size:15px; margin-bottom:6px; }}
  .chart-empty {{ color:#5a6b7d; font-size:15px; padding:12px 4px; }}
  .grid {{ stroke:#22303f; stroke-width:1; }}
  .xlabel {{ fill:#5a6b7d; font-size:12px; }}
  .ylabel {{ fill:#5a6b7d; font-size:12px; }}
  .node-card {{ background:#111c28; border:1px solid #2a3a4c; border-radius:8px; padding:14px 16px; margin-bottom:14px; }}
  .node-head {{ margin-bottom:8px; }}
  .node-id {{ color:#e7ca82; font-size:21px; font-weight:bold; }}
  .reward-chip {{ display:inline-block; margin-left:10px; padding:1px 8px; border-radius:3px; background:#5a3a2a; color:#ffd9a0; font-size:13px; vertical-align:middle; }}
  .boss-chip {{ display:inline-block; margin-left:10px; padding:1px 8px; border-radius:3px; background:#5a2020; color:#ffb0a0; font-size:13px; vertical-align:middle; }}
  .encounter-chip {{ display:inline-block; margin-left:10px; padding:1px 8px; border-radius:3px; background:#1f4a3a; color:#a0e0c0; font-size:13px; vertical-align:middle; }}
  .plane-title {{ margin:18px 0 10px; font-size:20px; color:#83d4e3; font-weight:600; border-bottom:1px solid #2a3a4c; padding-bottom:4px; }}
  .node-stats {{ margin-bottom:10px; }}
  .stat {{ display:inline-block; margin-right:24px; }}
  .stat-label {{ color:#83d4e3; font-size:14px; margin-right:6px; }}
  .stat-value {{ font-size:18px; font-weight:600; }}
  .zone-row {{ margin:6px 0; }}
  .zone-label {{ color:#83d4e3; font-size:14px; display:inline-block; width:64px; vertical-align:middle; }}
  .zone-empty {{ color:#5a6b7d; font-size:14px; }}
  .unit {{ display:inline-block; margin-right:10px; vertical-align:top; }}
  .unit-card {{ text-align:center; position:relative; }}
  .unit-card .icon {{ width:46px; height:46px; }}
  .unit-name {{ font-size:13px; color:#91a3b4; max-width:52px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }}
  .cheer-badge {{ display:inline-block; background:#e8692e; color:#fff; font-size:11px;
    padding:0 3px; border-radius:3px; margin-right:3px; vertical-align:1px; }}
  .hunter-badge {{ display:inline-block; background:#b8860b; color:#fff; font-size:11px;
    padding:0 3px; border-radius:3px; margin-right:3px; vertical-align:1px; }}
  .cost-badge {{ display:inline-block; background:#8a5a2b; color:#fff; font-size:11px;
    padding:0 3px; border-radius:3px; margin-right:3px; vertical-align:1px; }}
  .unit-equip .icon {{ width:16px; height:16px; margin:1px; }}
  .unit-equip {{ max-width:230px; text-align:left; }}
  .equipment-item {{ display:inline-flex; align-items:center; gap:3px;
    margin:2px 4px 2px 0; vertical-align:middle; }}
  .equipment-name {{ color:#c8d3df; font-size:12px; line-height:1.2; }}
  .privilege-badge {{ background:#2589d8; color:#fff; font-size:11px;
    padding:0 3px; border-radius:3px; }}
  .equipment-candidates .equipment-name {{ color:#e7ca82; }}
  .equip-slot {{ display:inline-block; width:16px; height:16px; margin:1px;
    border:1px solid #3a4a63; border-radius:3px; background:#1a2333; }}
  .unit-special {{ position:absolute; top:2px; right:2px; }}
  .unit-special .icon {{ width:18px; height:18px; border-radius:3px; }}
  .unit-special-label {{ color:#d9b8ff; font-size:12px; margin-top:2px;
    max-width:180px; text-align:left; }}
  .unit-star {{ color:#e7ca82; font-size:13px; line-height:1; }}
  .strategy-gain {{ margin-top:4px; color:#e7ca82; font-size:15px; font-weight:600; }}
  .bag {{ display:flex; align-items:center; margin-top:8px; }}
  .bag .icon {{ width:24px; height:24px; }}
  .stackable {{ position:relative; display:inline-block; }}
  .stack-count {{ position:absolute; left:1px; bottom:1px; background:#1a2333cc;
    color:#ffd9a0; font-size:12px; font-weight:600; border-radius:2px;
    padding:0 2px; line-height:1.2; }}
  .technical {{ margin-top:34px; background:#0a1118; border:1px dashed #2a3a4c; border-radius:8px; padding:10px 14px; }}
  .technical summary {{ color:#5a6b7d; cursor:pointer; font-size:15px; }}
  .tech-row {{ display:flex; margin:4px 0; }}
  .tech-label {{ color:#5a6b7d; font-size:13px; width:110px; flex:none; }}
  .tech-value {{ color:#71849a; font-size:13px; word-break:break-all; }}
</style></head><body><div class="wrap">
  <h1>货币战争 · 对局报告</h1>
  <div class="sub">对局时间 {html_mod.escape((cr.get("CompletedAt") or "")[:19].replace("T", " "))} · 评级 {html_mod.escape(cr.get("RatingText") or "未记录")} · 结束 {html_mod.escape(cr.get("CompletionNodeId") or "未记录")}</div>

  <div class="section"><h2>本局预设</h2></div>
  <div class="preset">
    <div class="preset-row"><span class="preset-label">投资环境</span>{icon("environment", env_id)}<span style="font-size:15px">{html_mod.escape(env_name)}</span></div>
    <div class="preset-row"><span class="preset-label">敌人阵营</span>{enemy_html or '<span class="zone-empty">未记录</span>'}</div>
    <div class="preset-row"><span class="preset-label">负面词条</span>{affix_html or '<span class="zone-empty">未记录</span>'}</div>
    <div class="preset-row"><span class="preset-label">投资策略</span>{strat_html}</div>
  </div>

  <div class="section"><h2>数值趋势</h2></div>
  <div class="charts">
    {svg_line(dmg_series, "#e7ca82", "总伤害", "万")}
    {svg_line(gold_series, "#83d4e3", "金币", "")}
    {svg_line(theory_series, "#bc8cff", "阵容理论出伤极限", "万")}
    {svg_line(act_series, "#9be08a", "行动值", "")}
  </div>

  <div class="section"><h2>节点明细</h2></div>
  {''.join(cards)}

  <details class="technical"><summary>调试信息（运行 ID / 来源文件等）</summary>
    {tech_html}
  </details>
  <div style="margin-top:26px;color:#7f8ba5;font-size:14px;text-align:center">交流QQ群：726898246 · 官网：https://taskflowai.cn</div>
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

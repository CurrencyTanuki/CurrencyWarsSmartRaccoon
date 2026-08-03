#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成一份"每一项都有数据"的模拟对局存档（1-1~3-9，补给节点跳过，符合货币战争实际）。"""
import json
import os

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(BASE, "report-demo", "sample-run")
OUT = os.path.join(OUT_DIR, "completed-run.v1.json")


def obs(value, confidence=0.7, ts="2026-08-02T06:00:00.0000000+00:00"):
    return {"status": "known", "value": value, "confidence": confidence,
            "evidence": [], "uncertainty": [], "observedAt": ts}


def unknown_obs(ts="2026-08-02T06:00:00.0000000+00:00"):
    return {"status": "unknown", "value": 0, "confidence": 0,
            "evidence": [], "uncertainty": [], "observedAt": ts}


CHAR = lambda n: f"currency_wars_character_{n:02d}"
EQ = lambda n: f"currency_wars_equipment_{n:03d}"

# 角色池：编号 -> (站位倾向, 星级, 装备3槽)。前台只能放前台、后台只能放后台。
ROSTER = {
    CHAR(1): ("FrontOnly", 2, [EQ(1), EQ(2), EQ(3)]),
    CHAR(2): ("FrontOnly", 3, [EQ(4), EQ(5), EQ(6)]),
    CHAR(3): ("FrontOnly", 2, [EQ(7), EQ(8)]),
    CHAR(4): ("BackOnly", 2, [EQ(9)]),
    CHAR(5): ("BackOnly", 1, [EQ(10)]),
    CHAR(6): ("FrontOnly", 3, [EQ(18), EQ(19)]),
    CHAR(7): ("BackOnly", 1, []),
    CHAR(8): ("BackOnly", 1, [EQ(11)]),
    CHAR(9): ("FrontOnly", 3, [EQ(12), EQ(13), EQ(14)]),
    CHAR(10): ("FrontOnly", 2, [EQ(15), EQ(16)]),
    CHAR(11): ("BackOnly", 2, [EQ(17)]),
    CHAR(12): ("FrontOnly", 1, [EQ(20)]),
    CHAR(13): ("BackOnly", 2, [EQ(21), EQ(22)]),
    CHAR(14): ("FrontOnly", 4, [EQ(23), EQ(24), EQ(25)]),
    CHAR(15): ("BackOnly", 3, [EQ(26), EQ(27)]),
    CHAR(16): ("FrontOnly", 2, [EQ(28)]),
    CHAR(17): ("BackOnly", 1, []),
    CHAR(18): ("FrontOnly", 3, [EQ(29), EQ(30), EQ(31)]),
    CHAR(19): ("BackOnly", 2, [EQ(32), EQ(33)]),
    CHAR(20): ("FrontOnly", 4, [EQ(34), EQ(35), EQ(36)]),
}
FRONT_ONLY = [cid for cid, (pos, _, _) in ROSTER.items() if pos == "FrontOnly"]
BACK_ONLY = [cid for cid, (pos, _, _) in ROSTER.items() if pos == "BackOnly"]

# 节点列表：1-1~3-9，补给节点（1-5/2-5/3-5）跳过
NODE_IDS = []
for plane in (1, 2, 3):
    for i in range(1, 10):
        if i == 5:
            continue
        NODE_IDS.append(f"{plane}-{i}")

# 节点类型（用户确认）：
# 奖励关（装备来源，伤害低、无行动值）：1-1 / 1-2 / 1-8 / 2-8 / 3-8
# 遭遇关（战斗）：1-7 / 2-7 / 3-7
# 首领关（高伤害战斗）：1-9 / 2-9 / 3-9
# 补给节点（1-5 / 2-5 / 3-5）：在 NODE_IDS 中已跳过（纯补给选择，不记数据）
# 余下为普通战斗关
REWARD_NODES = {"1-1", "1-2", "1-8", "2-8", "3-8"}
ENCOUNTER_NODES = {"1-7", "2-7", "3-7"}
BOSS_NODES = {"1-9", "2-9", "3-9"}


def store_level_for(idx):
    """商店等级：备战可升级，每 3 节点升 1 级，最高 10。"""
    return min(10, 1 + idx // 3)


def lineup_for(idx):
    """角色发育：前台最多 4 个，后台/备战席递增（备战席上限 9 + 财富宝钻人口）。"""
    front_count = min(4, 2 + idx // 6)
    back_count = min(3, 1 + idx // 8)
    bench_count = min(9, 2 + idx // 2)
    front = FRONT_ONLY[:front_count]
    back = BACK_ONLY[:back_count]
    bench = FRONT_ONLY[front_count:front_count + bench_count - back_count] + BACK_ONLY[back_count:]
    bench = bench[:bench_count]
    lineup = front + back + bench
    return front, back, bench, lineup


def special_items_for(idx):
    """备战席特殊物品随节点变化（获得/使用）。"""
    seq = [
        ["special_item_001"],  # 1-1 专家邀请函
        ["special_item_001", "special_item_004"],
        ["special_item_004", "special_item_009"],  # 1-3 使用邀请函
        ["special_item_004", "special_item_009", "special_item_010"],
        ["special_item_004", "special_item_009", "special_item_010"],  # 1-6
        ["special_item_004", "special_item_005", "special_item_009", "special_item_010"],
        ["special_item_004", "special_item_005", "special_item_009"],  # 1-8 使用冶金炉
        ["special_item_004", "special_item_005", "special_item_009", "special_item_015"],  # 1-9 财富宝钻
        ["special_item_004", "special_item_005", "special_item_009", "special_item_015", "special_item_020"],  # 2-1 3费聘用书
        ["special_item_004", "special_item_005", "special_item_009", "special_item_015", "special_item_020"],
        ["special_item_005", "special_item_009", "special_item_015", "special_item_020"],  # 2-3 使用简易武装箱
        ["special_item_005", "special_item_009", "special_item_015", "special_item_020", "special_item_021"],  # 2-4 4费聘用书
        ["special_item_005", "special_item_009", "special_item_015", "special_item_020", "special_item_021"],
        ["special_item_005", "special_item_006", "special_item_015", "special_item_020", "special_item_021"],  # 2-7 特权武装箱
        ["special_item_005", "special_item_006", "special_item_015", "special_item_020", "special_item_021"],
        ["special_item_005", "special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022"],  # 2-9 5费聘用书
        ["special_item_005", "special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022"],  # 3-1
        ["special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022"],  # 3-2 使用进阶武装箱
        ["special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022"],
        ["special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022", "special_item_016"],  # 3-4 红钻
        ["special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022", "special_item_016"],
        ["special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022", "special_item_016", "special_item_002"],  # 3-7 员工投影仪
        ["special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022", "special_item_016", "special_item_002"],
        ["special_item_006", "special_item_015", "special_item_020", "special_item_021", "special_item_022", "special_item_016", "special_item_002", "special_item_012"],  # 3-9 特权赋予卡
    ]
    return seq[min(idx, len(seq) - 1)]


def damage_for(idx, node_id):
    """总伤害：普通战斗随发育增长（带波动）；奖励关大幅减少（基本无战斗）；首领关极高。"""
    if node_id in REWARD_NODES:
        return 90000 + idx * 6000
    if node_id in BOSS_NODES:
        return 48000000 * (idx + 1) + idx * 2200000
    base = 280000 * (idx + 1) + (idx * idx * 42000)
    dip = 900000 if idx in (6, 20) else 0  # 个别低谷
    return base - dip + (idx % 4) * 230000


def gold_for(idx):
    return 3 + idx * 6 + (idx // 5) * 4


def health_for(idx):
    return max(48, 80 - (idx // 2) * 2 - (idx % 3))


def action_for(idx, node_id):
    """奖励关无行动值（理论出伤无法计算）；其他节点为战斗，有行动值。"""
    if node_id in REWARD_NODES:
        return None
    return max(2, 6 - idx // 6)


def reward_for(idx):
    return 4 + (idx % 7)


def build_formation(front, back, bench):
    chars = []
    for zone, ids in (("Front", front), ("Back", back), ("Bench", bench)):
        for slot, cid in enumerate(ids):
            _, star, eqs = ROSTER[cid]
            chars.append({
                "CharacterId": cid,
                "Zone": zone,
                "SlotIndex": slot,
                "StarLevel": star,
                "Standing": f"{zone} {slot + 1}",
                "EquipmentIds": eqs,
                "CanDriveDecisions": True,
                "Confidence": 0.9,
                "CandidateCharacterIds": [],
                "FailureReason": None,
                "TemporaryId": None,
                "CardRegion": {"X": 0.1, "Y": 0.2, "Width": 0.08, "Height": 0.12},
                "Evidence": [],
            })
    return chars


def build_battle(node_id, idx, lineup, is_boss):
    dmg = damage_for(idx, node_id)
    rank_ids = lineup[:3]
    chars = []
    for i, cid in enumerate(rank_ids):
        chars.append({
            "Rank": i + 1, "CharacterId": cid, "TemporaryId": None,
            "Damage": dmg // (i + 1), "CanDriveDecisions": True, "RawText": "",
            "AvatarConfidence": 0.9, "DamageConfidence": 0.85,
            "CandidateCharacterIds": [], "FailureReason": None,
            "AvatarRegion": {"X": 0.1, "Y": 0.2, "Width": 0.05, "Height": 0.05},
            "DamageRegion": {"X": 0.15, "Y": 0.2, "Width": 0.1, "Height": 0.05},
            "Evidence": [],
        })
    return {
        "NodeId": node_id,
        "CharacterDamage": chars,
        "TotalDamage": dmg,
        "GoldReward": reward_for(idx),
        "IsComplete": True,
        "IsBossNode": is_boss,
        "IsRewardNode": node_id in REWARD_NODES,
        "IsEncounterNode": node_id in ENCOUNTER_NODES,
        "FinalSettlementTopThree": [
            {"Rank": 1, "CharacterId": rank_ids[0], "TemporaryId": None, "Damage": dmg, "RawText": "", "DamageConfidence": 0.9, "CandidateCharacterIds": [], "FailureReason": None, "Evidence": []},
            {"Rank": 2, "CharacterId": rank_ids[1], "TemporaryId": None, "Damage": dmg // 2, "RawText": "", "DamageConfidence": 0.9, "CandidateCharacterIds": [], "FailureReason": None, "Evidence": []},
            {"Rank": 3, "CharacterId": rank_ids[2], "TemporaryId": None, "Damage": dmg // 3, "RawText": "", "DamageConfidence": 0.9, "CandidateCharacterIds": [], "FailureReason": None, "Evidence": []},
        ],
        "FinalSynergyDamage": [], "FinalUnresolvedDamage": [],
        "FinalDegradedObservations": [], "FinalPartialFields": [],
        "FinalUncertainty": [],
    }


def build_snapshot(node_id, idx, run_id, is_boss):
    t = f"2026-08-02T{6 + idx // 9}:{10 + (idx % 9) * 4:02d}:00.0000000+00:00"
    front, back, bench, lineup = lineup_for(idx)
    bag = [EQ(85), EQ(46), EQ(77), EQ(12), EQ(34), EQ(60), EQ(101), EQ(115), EQ(140), EQ(155)][:5 + idx // 3]
    return {
        "SchemaVersion": "1.0.0",
        "RunId": run_id,
        "AsOf": t,
        "PageId": obs(f"preparation_{node_id}", 0.9, t),
        "Stage": obs(f"preparation_{node_id}", 0.9, t),
        "Economy": obs(gold_for(idx), 0.7, t),
        "CumulativeSpend": obs(1 + idx, 0.65, t),
        "Health": obs(health_for(idx), 0.7, t),
        "ActionPoints": obs(action_for(idx, node_id) or 0, 0.7, t),
        "StoreLevel": obs(store_level_for(idx), 0.8, t),
        "CurrentNodeDamage": obs(0, 0.5, t),
        "BoardCharacterIds": obs(front + back, 0.7, t),
        "BenchCharacterIds": obs(bench, 0.7, t),
        "ShopCharacterIds": unknown_obs(t),
        "LineupIds": obs(lineup, 0.7, t),
        "SynergyIds": obs(["currency_wars_bond_01", "currency_wars_bond_13"], 0.8, t),
        "InvestmentEnvironmentId": obs("investment_environment_031", 0.9, t),
        "InvestmentStrategyIds": obs(strategy_ids_for(idx), 0.8, t),
        "EquipmentIds": obs(bag, 0.7, t),
        "SpecialItemIds": obs(special_items_for(idx), 0.6, t),
        "ExpertAdvisorIds": obs(["expert_advisor_003"], 0.6, t) if idx >= 4 else unknown_obs(t),
        "EnemyIds": obs(["competitor_15", "competitor_12", "competitor_06"], 0.9, t),
        "Nodes": [], "AppliedEventIds": [], "Diagnostics": [],
    }


def strategy_ids_for(idx):
    """固定于 1-3 / 2-2 / 3-2 备战开始之前获得投资策略；
    部分环境/策略会额外给策略（位置不固定），故每节点都要重新识别。"""
    if idx >= 17:  # 3-2
        return ["investment_strategy_001", "investment_strategy_003", "investment_strategy_004"]
    if idx >= 9:   # 2-2
        return ["investment_strategy_001", "investment_strategy_003"]
    if idx >= 2:   # 1-3
        return ["investment_strategy_001"]
    return []


def build_state(node_id, idx, run_id, is_boss):
    t = f"2026-08-02T{6 + idx // 9}:{10 + (idx % 9) * 4:02d}:00.0000000+00:00"
    front, back, bench, lineup = lineup_for(idx)
    return {
        "PageFamily": "Preparation",
        "PageId": f"preparation_{node_id}",
        "NodeId": obs(node_id, 0.9, t),
        "EnemyDifficulty": obs(120 + idx * 12, 0.65, t),
        "Interest": obs(idx * 2, 0.72, t),
        "CumulativeSpend": obs(1 + idx, 0.65, t),
        "PlayerProgress": obs("", 0.7, t),
        "StoreLevel": obs(store_level_for(idx), 0.8, t),
        "Formation": obs(build_formation(front, back, bench), 0.7, t),
        "ActiveSynergies": obs([{"SlotKey": "bond-1", "SynergyId": "currency_wars_bond_01", "ActiveCount": min(3, len(front)), "NextThreshold": 4, "Confidence": 0.8, "Evidence": []}], 0.8, t),
        "DismantleToolCount": obs(1 + idx // 3, 0.72, t),
        "SimpleEquipmentIds": unknown_obs(t),
        "NegativeAffixIds": obs(["enemy_affix_t1_02", "enemy_affix_t2_08", "enemy_affix_t3_24", "enemy_affix_t3_11"], 0.9, t),
        "InvestmentEnvironmentId": obs("investment_environment_031", 0.9, t),
        "InvestmentStrategyIds": obs(strategy_ids_for(idx), 0.8, t),
        "BattleDamage": unknown_obs(t), "BattleSynergyDamage": unknown_obs(t),
        "BattleUnresolvedDamage": unknown_obs(t), "BattleScreenDamageCandidate": unknown_obs(t),
        "SettlementDamage": unknown_obs(t), "SettlementScreenDamageCandidate": unknown_obs(t),
        "SettlementGoldReward": unknown_obs(t), "RemainingActionValue": unknown_obs(t),
        "FinalBattle": unknown_obs(t),
        "NamedContent": [], "PendingIcons": [], "PartialFields": [],
        "RecognitionTrace": [], "Diagnostics": [],
    }


def main():
    run_id = "run-demo-20260802"
    nodes = []
    for idx, nid in enumerate(NODE_IDS):
        is_boss = nid in BOSS_NODES
        t0 = f"2026-08-02T{6 + idx // 9}:{10 + (idx % 9) * 4:02d}:00.0000000+00:00"
        t1 = f"2026-08-02T{6 + idx // 9}:{10 + (idx % 9) * 4 + 3:02d}:00.0000000+00:00"
        front, back, bench, lineup = lineup_for(idx)
        nodes.append({
            "NodeId": nid,
            "StartedAt": t0,
            "EndedAt": t1,
            "IsFinalized": True,
            "IsComplete": True,
            "FinalPreparationSnapshot": build_snapshot(nid, idx, run_id, is_boss),
            "FinalPreparationState": build_state(nid, idx, run_id, is_boss),
            "FinalBattle": build_battle(nid, idx, lineup, is_boss),
            "PreparationAnalysisFile": f"analysis-20260802-{idx:06d}-demo.json",
            "FinalBattleFile": f"nodes/node-{nid}-final.json",
            "AppliedEventIds": [f"event-{idx}"],
            "Diagnostics": [],
        })

    cr = {
        "SchemaVersion": "1.0.0",
        "ArchiveVersion": 1,
        "RunId": run_id,
        "CompletedAt": "2026-08-02T08:20:00.0000000+00:00",
        "IsFinal": True,
        "CompletionPageId": "challenge_success",
        "CompletionNodeId": "3-9",
        "CompletionScreenshotFile": "screenshots/20260802-082000000.png",
        "RatingText": "S",
        "IdentityEvidence": {
            "InvestmentEnvironmentId": "investment_environment_031",
            "InvestmentStrategyIds": ["investment_strategy_001", "investment_strategy_003", "investment_strategy_004"],
            "EnemyAffixIds": ["enemy_affix_t1_02", "enemy_affix_t2_08", "enemy_affix_t3_24", "enemy_affix_t3_11"],
            "EnemyIds": ["competitor_15", "competitor_12", "competitor_06"],
        },
        "Nodes": nodes,
        "SourceAnalysisFiles": [f"analysis-20260802-{i:06d}-demo.json" for i in range(len(nodes))],
        "SourceRevision": "demo",
        "Uncertainty": [],
        "LastSnapshot": None,
        "LastOperationalState": None,
    }

    os.makedirs(OUT_DIR, exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(cr, f, ensure_ascii=False, indent=1)
    print(f"已生成模拟存档: {OUT}（{len(nodes)} 个节点，补给节点已跳过）")


if __name__ == "__main__":
    main()

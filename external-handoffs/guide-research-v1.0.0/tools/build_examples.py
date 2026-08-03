#!/usr/bin/env python3
"""Build the audited examples shipped with the handoff package."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VALID = ROOT / "examples/valid"
INVALID = ROOT / "examples/invalid"


def write(name: str, value: dict, invalid: bool = False) -> None:
    directory = INVALID if invalid else VALID
    (directory / name).write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def refs(**overrides):
    value = {
        "characterIds": [],
        "equipmentIds": [],
        "bondIds": [],
        "investmentEnvironmentIds": [],
        "investmentStrategyIds": [],
        "enemyAffixIds": [],
        "otherRefs": [],
    }
    value.update(overrides)
    return value


def evidence_ref(evidence_set_id: str, claim_id: str):
    return {"evidenceSetId": evidence_set_id, "claimId": claim_id}


def condition(field: str, operator: str, expected, unknown_policy="require_review", evidence=None):
    return {
        "field": field,
        "operator": operator,
        "expected": expected,
        "unknownPolicy": unknown_policy,
        "evidenceRefs": evidence or [],
    }


def number_range(minimum=None, maximum=None, unit="count"):
    return {"min": minimum, "max": maximum, "unit": unit}


def lineup(front=None, back=None, bench=None, stars=None):
    return {
        "frontCharacterIds": front or [],
        "backCharacterIds": back or [],
        "benchCharacterIds": bench or [],
        "minimumStarsByCharacterId": stars or {},
    }


def state(
    *,
    economy=(None, None),
    health=(None, None),
    level=(None, None),
    action_points=(None, None),
    target_lineup=None,
    equipment=None,
    bonds=None,
    environments=None,
    strategies=None,
):
    return {
        "economy": number_range(*economy, unit="coin"),
        "health": number_range(*health, unit="health"),
        "level": number_range(*level, unit="level"),
        "remainingActionPoints": number_range(*action_points, unit="action_point"),
        "lineup": target_lineup or lineup(),
        "equipment": equipment or [],
        "bondIds": bonds or [],
        "investmentEnvironmentIds": environments or [],
        "investmentStrategyIds": strategies or [],
    }


TAPTAP = "evidence-taptap-828363891523190942"
BILI = "evidence-bili-bv1jpny6neta"


def build_evidence() -> None:
    write(
        "01-complete.research-evidence.v1.json",
        {
            "schemaVersion": "research-evidence.v1",
            "evidenceSetId": TAPTAP,
            "title": "V4.4 姬子·启行列车护盾挂机流证据",
            "gameMode": "currency-wars",
            "applicableGameVersions": ["4.4"],
            "createdAt": "2026-07-27T13:40:00Z",
            "updatedAt": "2026-08-01T14:00:00Z",
            "createdBy": {"kind": "mixed", "name": "local audited research migration"},
            "source": {
                "sourceId": "taptap-828363891523190942",
                "title": "【V4.4攻略】货币战争A850攻略｜姬子·启行挂机流",
                "platform": "TapTap",
                "author": "冻梨游研社",
                "url": "https://www.taptap.cn/moment/828363891523190942",
                "publishedAt": "2026-07-20T04:22:21Z",
                "accessedAt": "2026-07-27",
                "contentType": "article",
                "language": "zh-CN",
                "applicableGameVersions": ["4.4"],
                "reuseStatus": "linked_only",
                "reuseNotes": "只保留链接、定位和转述，不捆绑正文。",
            },
            "claims": [
                {
                    "claimId": "metadata-v44-a850",
                    "topic": "source_metadata",
                    "statement": "页面标题明确标注 V4.4 与 A850 攻略。",
                    "assertionType": "source_fact",
                    "supportStatus": "direct",
                    "confidence": 0.99,
                    "locator": {"kind": "page", "value": "article:h1 标题"},
                    "applicableGameVersions": ["4.4"],
                    "subjectRefs": refs(),
                    "conditions": [],
                    "conflicts": [],
                    "notes": [],
                },
                {
                    "claimId": "core-train-shield",
                    "topic": "archetype_core",
                    "statement": "作者将姬子·启行与三月七的列车同行、护盾和反击联动作为路线核心。",
                    "assertionType": "author_recommendation",
                    "supportStatus": "direct",
                    "confidence": 0.93,
                    "locator": {"kind": "section", "value": "article:h2 一、阵容优势"},
                    "applicableGameVersions": ["4.4"],
                    "subjectRefs": refs(
                        characterIds=["currency_wars_character_01", "currency_wars_character_67"],
                        bondIds=["currency_wars_bond_11", "currency_wars_bond_15"],
                    ),
                    "conditions": ["采用姬子·启行列车护盾路线"],
                    "conflicts": [],
                    "notes": ["这是作者攻略建议，不是官方强度保证。"],
                },
                {
                    "claimId": "early-low-cost-transition",
                    "topic": "early_game",
                    "statement": "作者建议第一位面使用低费混合羁绊过渡，并以首个首领前达到 6 级为阶段目标。",
                    "assertionType": "author_recommendation",
                    "supportStatus": "direct",
                    "confidence": 0.9,
                    "locator": {"kind": "section", "value": "article:h2 二、初期过渡"},
                    "applicableGameVersions": ["4.4"],
                    "subjectRefs": refs(
                        bondIds=["currency_wars_bond_14", "currency_wars_bond_17", "currency_wars_bond_12"]
                    ),
                    "conditions": ["第一位面", "核心角色尚未成型"],
                    "conflicts": [],
                    "notes": [],
                },
                {
                    "claimId": "pivot-if-no-himeko",
                    "topic": "transition_branch",
                    "statement": "作者把第二位面仍未获得姬子·启行时转向希儿量子路线列为备选。",
                    "assertionType": "author_recommendation",
                    "supportStatus": "direct",
                    "confidence": 0.88,
                    "locator": {"kind": "section", "value": "article:h2 三、中期运营；article:h2 四、终局"},
                    "applicableGameVersions": ["4.4"],
                    "subjectRefs": refs(
                        characterIds=["currency_wars_character_01", "currency_wars_character_35"],
                        bondIds=["currency_wars_bond_22"],
                    ),
                    "conditions": ["进入第二位面", "未获得姬子·启行"],
                    "conflicts": [],
                    "notes": ["具体转型成本仍取决于当局经济与已持有角色。"],
                },
            ],
            "extractionStatus": "complete",
            "unknowns": [],
            "deduplicationKey": "taptap:828363891523190942",
            "notes": ["由项目内已审核研究结果迁移；未复制原文。"],
        },
    )

    write(
        "02-incomplete-conflicted.research-evidence.v1.json",
        {
            "schemaVersion": "research-evidence.v1",
            "evidenceSetId": BILI,
            "title": "创作体验服姬子护盾反震视频证据（待人工逐段复核）",
            "gameMode": "currency-wars",
            "applicableGameVersions": ["4.4"],
            "createdAt": "2026-07-27T13:40:00Z",
            "updatedAt": "2026-08-01T14:00:00Z",
            "createdBy": {"kind": "ai", "name": "local audited research migration"},
            "source": {
                "sourceId": "bili-bv1jpny6neta",
                "title": "【崩坏：星穹铁道】一动反伤140亿！4.4货币战争姬子护盾反震流",
                "platform": "Bilibili",
                "author": "Sennyuki风早千雪",
                "url": "https://www.bilibili.com/video/BV1jpNY6NEtA/",
                "publishedAt": "2026-07-15T03:17:16Z",
                "accessedAt": "2026-07-27",
                "contentType": "video",
                "language": "zh-CN",
                "applicableGameVersions": ["4.4"],
                "reuseStatus": "linked_only",
                "reuseNotes": "未捆绑视频、字幕或长段文字；时间点是待复核的粗粒度窗口。",
            },
            "claims": [
                {
                    "claimId": "metadata-creative-server",
                    "topic": "source_metadata",
                    "statement": "页面标题标注 4.4、A850 与创作体验服。",
                    "assertionType": "source_fact",
                    "supportStatus": "direct",
                    "confidence": 0.99,
                    "locator": {"kind": "page", "value": "page:title"},
                    "applicableGameVersions": ["4.4"],
                    "subjectRefs": refs(),
                    "conditions": [],
                    "conflicts": [],
                    "notes": ["创作体验服与正式服可能存在差异。"],
                },
                {
                    "claimId": "coarse-train-shield-route",
                    "topic": "archetype_core",
                    "statement": "现有本地研究将视频路线概括为 6 列车同行、2 护盾与姬子反震，但缺少逐段字幕级核验。",
                    "assertionType": "ai_inference",
                    "supportStatus": "unverified",
                    "confidence": 0.55,
                    "locator": {
                        "kind": "timestamp",
                        "value": "video:00:00-17:19 粗粒度复核窗口",
                        "timeStartSeconds": 0,
                        "timeEndSeconds": 1039
                    },
                    "applicableGameVersions": ["4.4"],
                    "subjectRefs": refs(
                        characterIds=["currency_wars_character_01"],
                        bondIds=["currency_wars_bond_11", "currency_wars_bond_15"],
                    ),
                    "conditions": ["创作体验服素材"],
                    "conflicts": [
                        {
                            "withEvidenceSetId": TAPTAP,
                            "withClaimId": "core-train-shield",
                            "summary": "路线方向相近，但视频为创作体验服且缺少逐段核验，不能直接合并为正式服确定规则。",
                            "status": "unresolved",
                            "resolution": None
                        }
                    ],
                    "notes": ["必须由外部研究者重新播放并补精确时间点。"],
                },
            ],
            "extractionStatus": "partial",
            "unknowns": [
                {
                    "field": "逐节点经济、刷新和装备优先级",
                    "reason": "公开字幕和章节不可用，本地资料只有粗粒度回放窗口。",
                    "attemptedSources": ["Bilibili 页面元数据", "公开播放器元数据"]
                }
            ],
            "deduplicationKey": "bilibili:BV1jpNY6NEtA",
            "notes": ["该例专门展示 incomplete、unknown 和 unresolved conflict 的合法表达。"],
        },
    )


def build_playbooks() -> None:
    common_signals = {
        "coreCharacterIds": ["currency_wars_character_01", "currency_wars_character_67"],
        "optionalCharacterIds": ["currency_wars_character_23", "currency_wars_character_55"],
        "equipmentIds": [],
        "bondIds": ["currency_wars_bond_11", "currency_wars_bond_15"],
        "investmentEnvironmentIds": [],
        "investmentStrategyIds": [],
    }
    common_policy = {
        "defaultBehavior": "mark_unknown",
        "highRiskDecisionBehavior": "never_auto_execute",
        "conflictBehavior": "preserve_all_sources",
        "minimumConfidence": 0.7,
    }
    core_ref = evidence_ref(TAPTAP, "core-train-shield")
    early_ref = evidence_ref(TAPTAP, "early-low-cost-transition")
    pivot_ref = evidence_ref(TAPTAP, "pivot-if-no-himeko")

    write(
        "03-complete.guide-playbook.v1.json",
        {
            "schemaVersion": "guide-playbook.v1",
            "guideId": "guide-taptap-himeko-train-shield-v1",
            "title": "V4.4 姬子·启行列车护盾稳定路线",
            "status": "reviewed",
            "applicableGameVersions": ["4.4"],
            "archetypeId": "archetype-himeko-train-shield",
            "archetypeName": "姬子·启行列车护盾",
            "goalIds": ["stable"],
            "signals": common_signals,
            "applicability": {
                "required": [condition("gameVersion", "equals", "4.4", "reject", [evidence_ref(TAPTAP, "metadata-v44-a850")])],
                "prohibited": [],
                "defaultUnknownPolicy": "require_review",
            },
            "phases": [
                {
                    "phaseId": "plane-1-transition",
                    "title": "第一位面低费过渡",
                    "selector": {"planeIds": [1], "nodeIds": [], "nodeTypes": ["preparation", "battle", "boss"]},
                    "recommendedState": state(level=(None, 6), bonds=["currency_wars_bond_14", "currency_wars_bond_17", "currency_wars_bond_12"]),
                    "actionIds": ["hold-flexible-transition"],
                    "evidenceRefs": [early_ref],
                    "notes": ["羁绊只表示可用过渡方向，不要求同时全部激活。"],
                },
                {
                    "phaseId": "plane-2-core",
                    "title": "第二位面向列车护盾收束",
                    "selector": {"planeIds": [2], "nodeIds": [], "nodeTypes": ["preparation", "battle", "boss"]},
                    "recommendedState": state(
                        target_lineup=lineup(
                            front=["currency_wars_character_01", "currency_wars_character_67"],
                            stars={"currency_wars_character_01": 1, "currency_wars_character_67": 1},
                        ),
                        bonds=["currency_wars_bond_11", "currency_wars_bond_15"],
                    ),
                    "actionIds": ["keep-himeko-and-march"],
                    "evidenceRefs": [core_ref],
                    "notes": [],
                },
            ],
            "actions": [
                {
                    "actionId": "hold-flexible-transition",
                    "title": "用现有低费组件稳定过渡",
                    "instruction": "保持可用低费阵容，不为尚未出现的核心角色过度刷新；首个首领前以达到 6 级为阶段目标。",
                    "priority": 100,
                    "conditions": [condition("plane", "equals", 1, "reject", [early_ref])],
                    "benefits": ["减少第一位面的无效经济投入"],
                    "costs": ["当前阵容上限暂时较低"],
                    "risks": ["过渡组件不足时仍需优先保证生存"],
                    "preconditions": ["页面已可靠识别为第一位面备战阶段"],
                    "invalidatesWhen": ["进入第二位面", "当前阵容无法稳定过关"],
                    "fallbackActionIds": [],
                    "evidenceRefs": [early_ref],
                },
                {
                    "actionId": "keep-himeko-and-march",
                    "title": "围绕列车同行与护盾收束",
                    "instruction": "确认获得姬子·启行后保留她，并优先寻找三月七及其他列车同行、生存组件。",
                    "priority": 90,
                    "conditions": [condition("lineupIds", "contains_any", ["currency_wars_character_01"], "reject", [core_ref])],
                    "benefits": ["向来源给出的列车护盾核心收束"],
                    "costs": ["仍需寻找剩余组件"],
                    "risks": ["单有核心角色不代表阵容已经成型"],
                    "preconditions": ["姬子·启行识别置信度可靠"],
                    "invalidatesWhen": ["正式服机制变更", "可靠数据表明当前路线失去生存能力"],
                    "fallbackActionIds": ["hold-flexible-transition"],
                    "evidenceRefs": [core_ref],
                },
            ],
            "branches": [],
            "alternativeRoutes": [],
            "risks": [
                {
                    "riskId": "author-experience-not-official",
                    "severity": "medium",
                    "description": "来源是作者经验，不是官方胜率或强度保证。",
                    "mitigation": "保留来源与版本，并在实际局势不满足时使用安全降级。",
                    "evidenceRefs": [core_ref],
                }
            ],
            "missingInformationPolicy": common_policy,
            "evidenceRefs": [evidence_ref(TAPTAP, "metadata-v44-a850"), core_ref, early_ref],
            "notes": ["本例由项目内已审核 runtime guide 和研究记录迁移。"],
        },
    )

    write(
        "04-branching-transition.guide-playbook.v1.json",
        {
            "schemaVersion": "guide-playbook.v1",
            "guideId": "guide-himeko-or-seele-transition-v1",
            "title": "姬子列车主线与第二位面量子转型分支",
            "status": "reviewed",
            "applicableGameVersions": ["4.4"],
            "archetypeId": "archetype-himeko-with-seele-fallback",
            "archetypeName": "列车护盾／量子转型",
            "goalIds": ["stable", "adaptive"],
            "signals": {
                **common_signals,
                "optionalCharacterIds": ["currency_wars_character_35", "currency_wars_character_23", "currency_wars_character_55"],
                "bondIds": ["currency_wars_bond_11", "currency_wars_bond_15", "currency_wars_bond_22"],
            },
            "applicability": {
                "required": [condition("gameVersion", "equals", "4.4", "reject", [evidence_ref(TAPTAP, "metadata-v44-a850")])],
                "prohibited": [],
                "defaultUnknownPolicy": "require_review",
            },
            "phases": [
                {
                    "phaseId": "evaluate-at-plane-2",
                    "title": "第二位面核心检查",
                    "selector": {"planeIds": [2], "nodeIds": [], "nodeTypes": ["preparation"]},
                    "recommendedState": state(),
                    "actionIds": ["continue-train-route", "pivot-to-seele"],
                    "evidenceRefs": [pivot_ref, core_ref],
                    "notes": ["信息不足时不自动做高风险转型。"],
                }
            ],
            "actions": [
                {
                    "actionId": "continue-train-route",
                    "title": "已获得姬子时继续列车路线",
                    "instruction": "保留姬子·启行并继续补列车同行和护盾组件。",
                    "priority": 100,
                    "conditions": [condition("lineupIds", "contains_any", ["currency_wars_character_01"], "reject", [core_ref])],
                    "benefits": ["避免放弃已经确认的核心"],
                    "costs": [],
                    "risks": ["仍需按实际经济控制刷新"],
                    "preconditions": ["角色识别为 Known"],
                    "invalidatesWhen": ["核心识别被后续高置信度证据否定"],
                    "fallbackActionIds": [],
                    "evidenceRefs": [core_ref],
                },
                {
                    "actionId": "pivot-to-seele",
                    "title": "第二位面仍无姬子时评估量子转型",
                    "instruction": "若第二位面仍确认没有姬子·启行，结合真实经济与已有角色评估转向希儿量子路线。",
                    "priority": 90,
                    "conditions": [
                        condition("plane", "equals", 2, "reject", [pivot_ref]),
                        condition("lineupIds", "contains_none", ["currency_wars_character_01"], "require_review", [pivot_ref]),
                    ],
                    "benefits": ["停止为已经错过的核心持续投入"],
                    "costs": ["可能放弃部分列车过渡投入"],
                    "risks": ["转型成本取决于当前经济和已持有角色"],
                    "preconditions": ["第二位面和缺少核心均经过可靠识别"],
                    "invalidatesWhen": ["获得姬子·启行", "量子组件不足"],
                    "fallbackActionIds": ["continue-train-route"],
                    "evidenceRefs": [pivot_ref],
                },
            ],
            "branches": [
                {
                    "branchId": "plane-2-core-check",
                    "priority": 100,
                    "when": [
                        condition("plane", "equals", 2, "reject", [pivot_ref]),
                        condition("lineupIds", "contains_none", ["currency_wars_character_01"], "require_review", [pivot_ref]),
                    ],
                    "thenActionIds": ["pivot-to-seele"],
                    "otherwiseActionIds": ["continue-train-route"],
                    "transitionToPhaseId": "evaluate-at-plane-2",
                    "evidenceRefs": [pivot_ref],
                }
            ],
            "alternativeRoutes": [
                {
                    "routeId": "seele-quantum-fallback",
                    "title": "希儿量子备选",
                    "triggerConditions": [condition("lineupIds", "contains_none", ["currency_wars_character_01"], "require_review", [pivot_ref])],
                    "actionIds": ["pivot-to-seele"],
                    "tradeoffs": ["降低继续追姬子的机会成本", "可能损失已投入的列车资源"],
                    "evidenceRefs": [pivot_ref],
                }
            ],
            "risks": [
                {
                    "riskId": "insufficient-transition-context",
                    "severity": "high",
                    "description": "单张截图可能无法证明完整经济和已投入资源。",
                    "mitigation": "信息不完整时仅提示评估，不自动执行刷新、出售或换阵。",
                    "evidenceRefs": [pivot_ref],
                }
            ],
            "missingInformationPolicy": common_policy,
            "evidenceRefs": [core_ref, pivot_ref],
            "notes": ["这是复杂条件分支和替代路线的真实资料结构示例。"],
        },
    )

    write(
        "05-incomplete-conflicted.guide-playbook.v1.json",
        {
            "schemaVersion": "guide-playbook.v1",
            "guideId": "guide-creative-server-train-shield-candidate-v1",
            "title": "创作体验服列车护盾候选路线（证据不足）",
            "status": "conflicted",
            "applicableGameVersions": ["4.4"],
            "archetypeId": "archetype-train-shield-unverified",
            "archetypeName": "列车护盾候选",
            "goalIds": ["research_candidate"],
            "signals": {
                "coreCharacterIds": ["currency_wars_character_01"],
                "optionalCharacterIds": ["currency_wars_character_67"],
                "equipmentIds": [],
                "bondIds": ["currency_wars_bond_11", "currency_wars_bond_15"],
                "investmentEnvironmentIds": [],
                "investmentStrategyIds": [],
            },
            "applicability": {
                "required": [condition("gameVersion", "equals", "4.4", "require_review", [evidence_ref(BILI, "metadata-creative-server")])],
                "prohibited": [],
                "defaultUnknownPolicy": "require_review",
            },
            "phases": [
                {
                    "phaseId": "manual-review-only",
                    "title": "待人工逐段复核",
                    "selector": {"planeIds": [], "nodeIds": [], "nodeTypes": ["unknown"]},
                    "recommendedState": state(bonds=["currency_wars_bond_11", "currency_wars_bond_15"]),
                    "actionIds": ["do-not-auto-apply"],
                    "evidenceRefs": [evidence_ref(BILI, "coarse-train-shield-route")],
                    "notes": ["节点、经济、装备和刷新阈值均未可靠取得。"],
                }
            ],
            "actions": [
                {
                    "actionId": "do-not-auto-apply",
                    "title": "保留为研究候选，不驱动高风险决策",
                    "instruction": "在补齐正式服和精确时间点证据前，只展示路线候选及风险，不自动执行购买、出售、刷新或换阵。",
                    "priority": 1,
                    "conditions": [condition("evidenceConfidence", "less_than", 0.7, "require_review", [evidence_ref(BILI, "coarse-train-shield-route")])],
                    "benefits": ["避免把体验服或粗粒度推断误当成正式服事实"],
                    "costs": ["暂时不能生成具体操作建议"],
                    "risks": ["来源之间的适用范围尚未解决"],
                    "preconditions": ["显示证据不足状态"],
                    "invalidatesWhen": ["取得正式服多来源逐段证据并完成复核"],
                    "fallbackActionIds": [],
                    "evidenceRefs": [evidence_ref(BILI, "coarse-train-shield-route")],
                }
            ],
            "branches": [],
            "alternativeRoutes": [],
            "risks": [
                {
                    "riskId": "creative-server-drift",
                    "severity": "high",
                    "description": "创作体验服数值或机制可能与正式服不同。",
                    "mitigation": "保留冲突，标记 unknown，并要求正式服来源交叉验证。",
                    "evidenceRefs": [evidence_ref(BILI, "metadata-creative-server"), evidence_ref(BILI, "coarse-train-shield-route")],
                }
            ],
            "missingInformationPolicy": {
                "defaultBehavior": "require_review",
                "highRiskDecisionBehavior": "never_auto_execute",
                "conflictBehavior": "preserve_all_sources",
                "minimumConfidence": 0.8,
            },
            "evidenceRefs": [
                evidence_ref(BILI, "metadata-creative-server"),
                evidence_ref(BILI, "coarse-train-shield-route"),
                core_ref,
            ],
            "notes": ["该例展示未知信息与跨来源冲突如何保留，不代表路线已被验证。"],
        },
    )


def build_invalid() -> None:
    write(
        "invalid-evidence-confidence.json",
        {
            "schemaVersion": "research-evidence.v1",
            "evidenceSetId": "invalid-confidence",
            "title": "故意非法：置信度超出范围",
            "gameMode": "currency-wars",
            "applicableGameVersions": ["4.4"],
            "createdAt": "2026-08-01T00:00:00Z",
            "updatedAt": "2026-08-01T00:00:00Z",
            "source": {
                "sourceId": "invalid-source",
                "title": "invalid",
                "platform": "test",
                "author": "test",
                "url": "https://example.invalid/source",
                "publishedAt": "unknown",
                "accessedAt": "2026-08-01",
                "contentType": "other",
                "language": "zh-CN",
                "applicableGameVersions": ["4.4"],
                "reuseStatus": "unknown",
            },
            "claims": [
                {
                    "claimId": "bad-confidence",
                    "topic": "test",
                    "statement": "This record must be rejected.",
                    "assertionType": "ai_inference",
                    "supportStatus": "unverified",
                    "confidence": 1.5,
                    "locator": {"kind": "whole_source", "value": "test"},
                    "applicableGameVersions": ["4.4"],
                    "subjectRefs": refs(),
                    "conditions": [],
                    "conflicts": [],
                }
            ],
            "extractionStatus": "partial",
            "unknowns": [],
        },
        invalid=True,
    )
    write(
        "invalid-playbook-executable-code.json",
        {
            "schemaVersion": "guide-playbook.v1",
            "guideId": "invalid-executable-code",
            "title": "故意非法：包含任意代码字段",
            "status": "draft",
            "applicableGameVersions": ["4.4"],
            "archetypeId": "invalid",
            "archetypeName": "invalid",
            "goalIds": ["test"],
            "signals": common_signals if False else {
                "coreCharacterIds": [], "optionalCharacterIds": [], "equipmentIds": [],
                "bondIds": [], "investmentEnvironmentIds": [], "investmentStrategyIds": []
            },
            "applicability": {"required": [], "prohibited": [], "defaultUnknownPolicy": "require_review"},
            "phases": [],
            "actions": [],
            "branches": [],
            "alternativeRoutes": [],
            "risks": [],
            "missingInformationPolicy": {
                "defaultBehavior": "mark_unknown",
                "highRiskDecisionBehavior": "never_auto_execute",
                "conflictBehavior": "preserve_all_sources",
                "minimumConfidence": 0.7,
            },
            "evidenceRefs": [],
            "executeCode": "rm -rf /"
        },
        invalid=True,
    )


if __name__ == "__main__":
    build_evidence()
    build_playbooks()
    build_invalid()
    print("Examples generated.")

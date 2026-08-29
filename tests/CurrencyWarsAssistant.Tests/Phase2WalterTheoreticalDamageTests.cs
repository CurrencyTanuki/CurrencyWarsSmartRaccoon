using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 瓦尔特行动上限 + 理论极限测试（2026-08-06 会话 Z）：
/// 用户规则——阵容（仅前台）识别到瓦尔特 → 假设技能放满：
/// 1/2星 +100、3星 +999（3-7 首领节点基础 160 → 260 / 1159）。
/// 修复：瓦尔特识别放宽到"残缺阵容 value"（formation 整体 Unknown 时
/// 瓦尔特槽位也要识别），否则 3-7 行动值 163 ≥ 160 拒绝计算理论极限。
/// 公式：理论极限 = 总伤害 ÷ 已用行动值 × 总行动值上限（D/U×M）。
/// </summary>
public sealed class Phase2WalterTheoreticalDamageTests
{
    private const string WalterCharacterId = "currency_wars_character_20";

    [Fact]
    public void WalterInFrontRaisesActionCapAndComputesTheoreticalLimit()
    {
        var tracker = new Phase2OperationalStateTracker();
        // 备战帧：formation 整体 Unknown（残缺），但 value 里瓦尔特（前台 1 星）已识别
        ObserveTwice(tracker, PreparationWithWalter("3-7", starLevel: 1));
        // 战斗帧：行动值 163（1 轮 + 63，超过基础上限 160——瓦尔特加成后 260 内）
        ObserveTwice(tracker, Battle("3-7", 163));
        // 结算：总伤害 90.49 亿
        tracker.Observe(Settlement("3-7", 9_049_577_000L));
        var firstExit = tracker.Observe(Preparation("3-8"), Health(69));
        var secondExit = tracker.Observe(Preparation("3-8"), Health(69));
        var update = firstExit.FinalizedBattle is not null
            ? firstExit
            : secondExit;

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        // 3-7 首领节点基础行动上限 160（用户 2026-08-06 实测确认）
        Assert.Equal(160, final.BaseMaximumActionValue);
        // 瓦尔特 1 星：160 + 100 = 260
        Assert.Equal(260, final.EffectiveMaximumActionValue);
        Assert.NotNull(final.TheoreticalDamageLimit);
        // D/U×M = 90.49亿 / (260-163) × 260 = 90.49亿 / 97 × 260 ≈ 242.5亿
        var expected = 9_049_577_000L / 97m * 260m;
        Assert.Equal(
            decimal.ToInt64(decimal.Round(expected, 0, MidpointRounding.AwayFromZero)),
            final.TheoreticalDamageLimit);
        Assert.Contains("Walter", final.TheoreticalDamageRule ?? "");
    }

    [Fact]
    public void WalterOnBenchDoesNotRaiseActionCap()
    {
        // 用户规则：瓦尔特只在（前台）算数；备战席/后台不算。
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, PreparationWithWalter("3-7", starLevel: 3, zone: FormationZone.Bench));
        ObserveTwice(tracker, Battle("3-7", 163));
        tracker.Observe(Settlement("3-7", 9_049_577_000L));
        var firstExit = tracker.Observe(Preparation("3-8"), Health(69));
        var secondExit = tracker.Observe(Preparation("3-8"), Health(69));
        var update = firstExit.FinalizedBattle is not null
            ? firstExit
            : secondExit;

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        // 备战席瓦尔特不生效：上限仍 160 → 行动值 163 < 160？不，163 ≥ 160 → 理论极限不可算
        Assert.Equal(160, final.EffectiveMaximumActionValue);
        Assert.Null(final.TheoreticalDamageLimit);
    }

    [Fact]
    public void WalterDoesNotLeakToNextNodeWithoutWalter()
    {
        // 跨节点污染回归：节点 3-7 前台瓦尔特（finalize 后清空备战阵容），
        // 节点 3-8 无瓦尔特（备战/战斗 formation 全空）不应误标瓦尔特。
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, PreparationWithWalter("3-7", starLevel: 1));
        ObserveTwice(tracker, Battle("3-7", 163));
        tracker.Observe(Settlement("3-7", 9_049_577_000L));
        var exit1 = tracker.Observe(Preparation("3-8"), Health(69));
        var exit2 = tracker.Observe(Preparation("3-8"), Health(69));
        Assert.NotNull(exit1.FinalizedBattle ?? exit2.FinalizedBattle);

        // 节点 3-8：无瓦尔特（formation 空）
        ObserveTwice(tracker, Battle("3-8", 150));
        tracker.Observe(Settlement("3-8", 5_000_000L));
        var f1 = tracker.Observe(Preparation("3-9"), Health(71));
        var f2 = tracker.Observe(Preparation("3-9"), Health(71));
        var final = Assert.IsType<FinalNodeBattleState>(
            f1.FinalizedBattle ?? f2.FinalizedBattle);
        // 3-8 不被 3-7 的瓦尔特污染：上限仍 120（无加成）
        Assert.Equal(120, final.EffectiveMaximumActionValue);
        Assert.Null(final.TheoreticalDamageLimit);
    }

    private static void ObserveTwice(
        Phase2OperationalStateTracker tracker,
        Phase2OperationalState state,
        Observation<int>? playerHealth = null)
    {
        tracker.Observe(state, playerHealth);
        tracker.Observe(state, playerHealth);
    }

    private static Phase2OperationalState PreparationWithWalter(
        string node,
        int starLevel,
        FormationZone zone = FormationZone.Front) => new()
    {
        PageFamily = Phase2PageFamily.Preparation,
        PageId = "preparation_generic",
        NodeId = Observation<string>.Known(node, 0.95),
        Formation = new Observation<IReadOnlyList<FormationCharacterState>>
        {
            // 整体 Unknown（残缺阵容），但瓦尔特槽位识别到了
            Status = ObservationStatus.Unknown,
            Confidence = 0,
            Value =
            [
                new FormationCharacterState(
                    zone,
                    0,
                    WalterCharacterId,
                    starLevel,
                    "前台",
                    [],
                    0.70,
                    new EvidenceReference(
                        "fixture:walter",
                        "vision:formation:walter",
                        "slot",
                        DateTimeOffset.UtcNow,
                        0.70),
                    CanDriveDecisions: true)
            ]
        }
    };

    private static Phase2OperationalState Battle(string node, int action) => new()
    {
        PageFamily = Phase2PageFamily.Battle,
        PageId = "battle_generic",
        NodeId = Observation<string>.Known(node, 0.95),
        RemainingActionValue = Observation<RemainingActionValueState>.Known(
            RemainingActionValueState.Create(1, action - 100),
            0.95),
        BattleScreenDamageCandidate = Observation<long>.Known(
            9_049_577_000L,
            0.9,
            [new EvidenceReference(
                "fixture:battle",
                "vision:battle-total",
                "total",
                DateTimeOffset.UtcNow,
                0.9)],
            DateTimeOffset.UtcNow),
        BattleDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
            [
                new CharacterDamageState(
                    0,
                    "currency_wars_character_20",
                    9_049_577_000L,
                    "90.5亿",
                    0.90,
                    0.90,
                    new RelativeRegion(0.1, 0.2, 0.05, 0.1),
                    new RelativeRegion(0.2, 0.2, 0.1, 0.05),
                    new EvidenceReference(
                        "fixture:battle",
                        "vision:battle-damage",
                        "damage",
                        DateTimeOffset.UtcNow,
                        0.90),
                    CanDriveDecisions: true)
            ],
            0.90)
    };

    private static Phase2OperationalState Settlement(string node, long damage) => new()
    {
        PageFamily = Phase2PageFamily.BattleSettlement,
        PageId = "challenge_success",
        NodeId = Observation<string>.Known(node, 0.95),
        SettlementScreenDamageCandidate = Observation<long>.Known(
            damage,
            0.95,
            [new EvidenceReference(
                "fixture:settlement",
                "vision:settlement-total",
                "total",
                DateTimeOffset.UtcNow,
                0.95)],
            DateTimeOffset.UtcNow),
        SettlementDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
            [
                new CharacterDamageState(
                    0,
                    "currency_wars_character_20",
                    damage,
                    "90.5亿",
                    0.95,
                    0.95,
                    new RelativeRegion(0.1, 0.2, 0.05, 0.1),
                    new RelativeRegion(0.2, 0.2, 0.1, 0.05),
                    new EvidenceReference(
                        "fixture:settlement",
                        "vision:settlement-damage",
                        "damage",
                        DateTimeOffset.UtcNow,
                        0.95),
                    CanDriveDecisions: true)
            ],
            0.95)
    };

    private static Phase2OperationalState Preparation(string node) => new()
    {
        PageFamily = Phase2PageFamily.Preparation,
        PageId = "preparation_generic",
        NodeId = Observation<string>.Known(node, 0.95)
    };

    private static Observation<int> Health(int health) =>
        Observation<int>.Known(health, 0.9);
}

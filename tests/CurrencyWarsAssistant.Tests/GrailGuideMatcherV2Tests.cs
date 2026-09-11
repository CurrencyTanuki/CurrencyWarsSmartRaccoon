using System;
using System.Collections.Generic;
using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAdvisor.GuidePlaybooks;
// 别名消歧：Advisor 命名空间的 record GuidePlaybook（主契约）≠ 文件加载器类（本测试目标）。
using GuidePlaybookFile = CurrencyWarsAdvisor.GuidePlaybooks.GuidePlaybook;
using GuideCondition = CurrencyWarsAdvisor.GuidePlaybooks.Condition;

namespace CurrencyWarsAssistant.Tests;

/// <summary>1.2.131 攻略匹配器接入行为测试（审计 P1/P2 修复的回归守卫）。</summary>
public class GrailGuideMatcherV2Tests
{
    private static RunSnapshot Snapshot(bool known)
    {
        var st = known ? ObservationStatus.Known : ObservationStatus.Unknown;
        return new RunSnapshot
        {
            RunId = "test",
            AsOf = DateTimeOffset.Now,
            BoardCharacterIds = new Observation<IReadOnlyList<string>> { Status = st, Value = new[] { "currency_wars_character_20" } },
            BenchCharacterIds = new Observation<IReadOnlyList<string>> { Status = st, Value = Array.Empty<string>() },
            StoreLevel = new Observation<int> { Status = ObservationStatus.Known, Value = 6 },
            Economy = new Observation<int> { Status = ObservationStatus.Known, Value = 12 },
            Health = new Observation<int> { Status = ObservationStatus.Known, Value = 80 },
            SynergyIds = new Observation<IReadOnlyList<string>> { Status = ObservationStatus.Known, Value = new[] { "currency_wars_bond_31" } },
        };
    }

    private static GuidePlaybookFile Pb(string guideId, params (string CharId, int Priority, string? Note)[] prios)
    {
        var pb = new GuidePlaybookFile
        {
            GuideId = guideId,
            Title = guideId,
            Signals = new Signals { CoreCharacterIds = { prios[0].CharId } },
        };
        var phase = new Phase
        {
            PhaseId = "p1",
            Selector = new Selector { NodeIds = { "3-7" } },
        };
        var rs = new RecommendedState { Lineup = new LineupTarget() };
        rs.Lineup.AcquisitionPriorities = prios.Select(p => new AcquisitionPriority
        {
            CharacterId = p.CharId,
            Priority = p.Priority,
            Note = p.Note,
        }).ToList();
        phase.RecommendedState = rs;
        pb.Phases.Add(phase);
        return pb;
    }

    [Fact]
    public void BestMatch_阵容未观测_返回null不静默返首册()
    {
        var pb = Pb("g1", ("currency_wars_character_04", 1, "核心件"));
        var (result, score) = GuideEvaluator.BestMatch(new[] { pb }, RunContextFactory.FromSnapshot(Snapshot(known: false)));
        Assert.Null(result);
        Assert.Equal(0, score);
    }

    [Fact]
    public void Applicability_羁绊未观测_不阻断只标注()
    {
        var pb = Pb("g1", ("currency_wars_character_04", 1, "核心件"));
        pb.Applicability = new Applicability
        {
            Required =
            {
                new GuideCondition
                {
                    Field = "bonds",
                    Op = "contains_any",
                    Expected = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "currency_wars_bond_08" }),
                },
            },
        };
        var baseCtx = RunContextFactory.FromSnapshot(Snapshot(known: true));
        var ctx = new RunContext
        {
            Level = baseCtx.Level,
            Population = baseCtx.Population,
            Gold = baseCtx.Gold,
            OwnedCharacterIds = baseCtx.OwnedCharacterIds,
            OwnedKnown = baseCtx.OwnedKnown,
            NodeId = baseCtx.NodeId,
            SynergyIds = null, // 未观测
            InvestmentStrategyIds = baseCtx.InvestmentStrategyIds,
        };
        var (gaps, unverified) = GuideEvaluator.ApplicabilityGaps(pb, ctx);
        Assert.Empty(gaps);            // 未观测不得阻断（P1 修复）
        Assert.NotEmpty(unverified);   // 但必须标注
    }

    [Fact]
    public void 分支_when全不满足_then被门控_otherwise可做()
    {
        var pb = Pb("g1", ("currency_wars_character_04", 1, "核心件"));
        pb.Branches.Add(new Branch
        {
            BranchId = "b1",
            Priority = 1,
            When = { new GuideCondition { Field = "gold", Op = "greater_or_equal", Expected = System.Text.Json.JsonSerializer.SerializeToElement(999) } },
            ThenActionIds = { "act-then" },
            OtherwiseActionIds = { "act-else" },
        });
        var ctx = new RunContext { Gold = 5, Level = 4, OwnedKnown = true };
        var thenGate = GuideEvaluator.EvaluateBranchGateForTest(pb, "act-then", ctx);
        var elseGate = GuideEvaluator.EvaluateBranchGateForTest(pb, "act-else", ctx);
        Assert.NotNull(thenGate);   // then 被门控（金不足）
        Assert.Null(elseGate);      // otherwise 可做（else 语义）
    }

    [Fact]
    public void 分支_条件未知_then与otherwise都门控_failClosed()
    {
        var pb = Pb("g1", ("currency_wars_character_04", 1, "核心件"));
        pb.Branches.Add(new Branch
        {
            BranchId = "b1",
            Priority = 1,
            When = { new GuideCondition { Field = "kafka_stars", Op = "greater_or_equal", Expected = System.Text.Json.JsonSerializer.SerializeToElement(1) } },
            ThenActionIds = { "act-then" },
            OtherwiseActionIds = { "act-else" },
        });
        var ctx = new RunContext { Gold = 5, Level = 4, OwnedKnown = true };
        Assert.NotNull(GuideEvaluator.EvaluateBranchGateForTest(pb, "act-then", ctx));
        Assert.NotNull(GuideEvaluator.EvaluateBranchGateForTest(pb, "act-else", ctx)); // 未知条件 fail-closed
    }
}

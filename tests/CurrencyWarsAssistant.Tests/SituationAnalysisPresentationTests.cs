using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.App;

namespace CurrencyWarsAssistant.Tests;

public sealed class SituationAnalysisPresentationTests
{
    [Fact]
    public void MainPageDoesNotExposeInternalClassifierOrOtherPageDiagnostics()
    {
        var capturedAt = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var evidence = new EvidenceReference(
            "fixture:main",
            "partial:unknown-page:battle-node",
            "页面外字段降级证据",
            capturedAt);
        var result = new ScreenshotAnalysisResult
        {
            AnalysisId = "analysis-main",
            Snapshot = new RunSnapshot
            {
                RunId = "run-main",
                AsOf = capturedAt,
                PageId = Observation<string>.Known(
                    "currency_wars_home",
                    0.70,
                    [evidence],
                    capturedAt),
                Stage = Observation<string>.Known(
                    "currency_wars_home",
                    0.70,
                    [evidence],
                    capturedAt)
            },
            Warnings =
            [
                "recognition:classifier-miss currency_wars_home/title=0.698/0.900"
            ],
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Main,
                Diagnostics =
                [
                    "recognition:classifier-miss currency_wars_home/title=0.698/0.900"
                ],
                PartialFields =
                [
                    new Phase2PartialFieldObservation(
                        "battle-node",
                        "unknown-page-battle-node",
                        new RelativeRegion(0.2, 0.1, 0.1, 0.1),
                        new Dictionary<string, string> { ["text"] = "1-6" },
                        ["1-6"],
                        [],
                        0.35,
                        "页面类型尚未确认；该区域文字仅作降级证据，不能驱动操作。",
                        evidence)
                ]
            }
        };

        var presentation = SituationAnalysisPresentation.Build(result);

        Assert.Contains("货币战争主界面", presentation.PageSummary, StringComparison.Ordinal);
        Assert.Contains("当前主页面没有需要写入对局记录的动态字段", presentation.StateSummary,
            StringComparison.Ordinal);
        Assert.Equal("无额外警告。", presentation.WarningsSummary);
        Assert.DoesNotContain("classifier-miss", presentation.WarningsSummary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("battle-node", presentation.WarningsSummary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettlementOperationalFieldsRemainVisibleWhenLegacySnapshotIsUnknown()
    {
        var capturedAt = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var evidence = new EvidenceReference(
            "fixture:settlement",
            "settlement:top-three",
            "结算页前三名",
            capturedAt);
        var damage = new[]
        {
            new CharacterDamageState(
                1,
                "character:the_herta",
                132_498_000,
                "13249.8万",
                0.96,
                0.99,
                new RelativeRegion(0.58, 0.55, 0.05, 0.08),
                new RelativeRegion(0.63, 0.55, 0.12, 0.08),
                evidence),
            new CharacterDamageState(
                2,
                null,
                4_303_000,
                "430.3万",
                0.31,
                0.98,
                new RelativeRegion(0.58, 0.64, 0.05, 0.08),
                new RelativeRegion(0.63, 0.64, 0.12, 0.08),
                evidence,
                TemporaryId: "unknown-character-1",
                FailureReason: "头像未达到唯一匹配阈值",
                CanDriveDecisions: false)
        };
        var result = new ScreenshotAnalysisResult
        {
            AnalysisId = "analysis-settlement",
            Snapshot = new RunSnapshot
            {
                RunId = "run-1",
                AsOf = capturedAt
            },
            UnknownFields = ["shop(Unknown)", "investmentEnvironment(Unknown)"],
            Warnings =
            [
                "以下字段在这张截图中不可可靠确定：shop(Unknown)、investmentEnvironment(Unknown)"
            ],
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.BattleSettlement,
                SettlementDamage = new Observation<IReadOnlyList<CharacterDamageState>>
                {
                    Status = ObservationStatus.Unknown,
                    Value = damage,
                    Confidence = 0.94,
                    Evidence = [evidence],
                    Uncertainty = ["结算前三名伤害完整，但至少一个头像身份未知。"],
                    ObservedAt = capturedAt
                },
                SettlementScreenDamageCandidate = Observation<long>.Known(
                    136_801_000,
                    0.94,
                    [evidence],
                    capturedAt),
                SettlementGoldReward = Observation<int>.Known(
                    9,
                    0.98,
                    [evidence],
                    capturedAt)
            }
        };

        var presentation = SituationAnalysisPresentation.Build(result);

        Assert.Contains("战斗结算", presentation.PageSummary, StringComparison.Ordinal);
        Assert.Contains("奖励金币：9", presentation.StateSummary, StringComparison.Ordinal);
        Assert.Contains("character:the_herta", presentation.StateSummary, StringComparison.Ordinal);
        Assert.Contains("未知角色1", presentation.StateSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-character-1", presentation.StateSummary, StringComparison.Ordinal);
        Assert.Contains("430.3万", presentation.StateSummary, StringComparison.Ordinal);
        Assert.Contains("头像未达到唯一匹配阈值", presentation.WarningsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("shop(Unknown)", presentation.WarningsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("investmentEnvironment(Unknown)", presentation.WarningsSummary, StringComparison.Ordinal);
        Assert.True(presentation.KnownFieldCount >= 2);
        Assert.True(presentation.UnknownFieldCount >= 1);
    }
}

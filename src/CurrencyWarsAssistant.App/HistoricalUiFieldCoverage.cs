using System.Reflection;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.App;

public enum HistoricalUiCoverageDisposition
{
    HistoricalDetail,
    Diagnostics,
    ExplicitlyNotDisplayed
}

public sealed record HistoricalUiFieldCoverage(
    Type ModelType,
    string PropertyName,
    string UiSection,
    HistoricalUiCoverageDisposition Disposition,
    string Rationale)
{
    public string FieldKey => $"{ModelType.Name}.{PropertyName}";
}

/// <summary>
/// Explicit contract between the persisted/reportable run model and the history UI.
/// Property names are intentionally enumerated instead of discovered at runtime: adding
/// a model property without deciding where it is shown must fail the coverage test.
/// </summary>
public static class HistoricalUiFieldCoverageRegistry
{
    private static readonly IReadOnlyList<HistoricalUiFieldCoverage> fields =
        Build();

    public static IReadOnlyList<HistoricalUiFieldCoverage> Fields => fields;

    public static IReadOnlyList<Type> CoveredModelTypes { get; } =
    [
        typeof(CompletedRunRecord),
        typeof(CompletedRunNodeRecord),
        typeof(RunIdentityEvidence),
        typeof(RunSnapshot),
        typeof(NodeRecord),
        typeof(ScreenshotAnalysisResult),
        typeof(Phase2OperationalState),
        typeof(FinalNodeBattleState),
        typeof(FormationCharacterState),
        typeof(CharacterEquipmentSlotState),
        typeof(InventorySlotState),
        typeof(ActiveSynergyState),
        typeof(PlayerProgressState),
        typeof(CharacterDamageState),
        typeof(SynergyDamageState),
        typeof(UnresolvedDamageSourceState),
        typeof(RemainingActionValueState),
        typeof(Phase2NamedContentRecognition),
        typeof(PendingIconObservation),
        typeof(Phase2PartialFieldObservation),
        typeof(Phase2FieldRecognitionTrace),
        typeof(GuideMatch),
        typeof(ScoreComponent),
        typeof(Recommendation),
        typeof(EvidenceReference),
        typeof(Observation<>)
    ];

    private static IReadOnlyList<HistoricalUiFieldCoverage> Build()
    {
        var result = new List<HistoricalUiFieldCoverage>();

        Add<CompletedRunRecord>(result, "对局归档", HistoricalUiCoverageDisposition.HistoricalDetail,
            "对局级元数据在已完成对局页显示。",
            nameof(CompletedRunRecord.SchemaVersion),
            nameof(CompletedRunRecord.ArchiveVersion),
            nameof(CompletedRunRecord.RunId),
            nameof(CompletedRunRecord.CompletedAt),
            nameof(CompletedRunRecord.IsFinal),
            nameof(CompletedRunRecord.CompletionPageId),
            nameof(CompletedRunRecord.CompletionNodeId),
            nameof(CompletedRunRecord.CompletionScreenshotFile),
            nameof(CompletedRunRecord.RatingText),
            nameof(CompletedRunRecord.Nodes),
            nameof(CompletedRunRecord.IdentityEvidence));
        Add<CompletedRunRecord>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "原始末帧、来源文件与不确定项属于可展开诊断证据。",
            nameof(CompletedRunRecord.LastSnapshot),
            nameof(CompletedRunRecord.LastOperationalState),
            nameof(CompletedRunRecord.SourceAnalysisFiles),
            nameof(CompletedRunRecord.SourceRevision),
            nameof(CompletedRunRecord.Uncertainty));

        Add<CompletedRunNodeRecord>(result, "节点概览", HistoricalUiCoverageDisposition.HistoricalDetail,
            "每节点最终快照、状态和战斗结果均进入节点详情。",
            nameof(CompletedRunNodeRecord.NodeId),
            nameof(CompletedRunNodeRecord.FinalPreparationSnapshot),
            nameof(CompletedRunNodeRecord.FinalPreparationState),
            nameof(CompletedRunNodeRecord.FinalBattle));
        Add<CompletedRunNodeRecord>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "原始分析文件路径仅用于证据追溯。",
            nameof(CompletedRunNodeRecord.PreparationAnalysisFile),
            nameof(CompletedRunNodeRecord.FinalBattleFile));

        Add<RunIdentityEvidence>(result, "构筑与敌情", HistoricalUiCoverageDisposition.HistoricalDetail,
            "整局固定身份信息在开局信息及每节点构筑区显示。",
            nameof(RunIdentityEvidence.InvestmentEnvironmentId),
            nameof(RunIdentityEvidence.InvestmentStrategyIds),
            nameof(RunIdentityEvidence.EnemyAffixIds),
            nameof(RunIdentityEvidence.EnemyIds));

        Add<RunSnapshot>(result, "节点概览", HistoricalUiCoverageDisposition.HistoricalDetail,
            "快照身份、时刻、页面、阶段和基础数值均可追溯。",
            nameof(RunSnapshot.SchemaVersion),
            nameof(RunSnapshot.RunId),
            nameof(RunSnapshot.AsOf),
            nameof(RunSnapshot.PageId),
            nameof(RunSnapshot.Stage),
            nameof(RunSnapshot.Health),
            nameof(RunSnapshot.ActionPoints),
            nameof(RunSnapshot.CurrentNodeDamage));
        Add<RunSnapshot>(result, "经济与成长", HistoricalUiCoverageDisposition.HistoricalDetail,
            "经济、累计花费和物品类快照在经济/资源区显示。",
            nameof(RunSnapshot.Economy),
            nameof(RunSnapshot.CumulativeSpend),
            nameof(RunSnapshot.EquipmentIds),
            nameof(RunSnapshot.SpecialItemIds),
            nameof(RunSnapshot.InventorySlots),
            nameof(RunSnapshot.ExpertAdvisorIds));
        Add<RunSnapshot>(result, "阵容与装备", HistoricalUiCoverageDisposition.HistoricalDetail,
            "前台/后台、备战席、商店和出阵集合均显式显示。",
            nameof(RunSnapshot.BoardCharacterIds),
            nameof(RunSnapshot.BenchCharacterIds),
            nameof(RunSnapshot.ShopCharacterIds),
            nameof(RunSnapshot.LineupIds));
        Add<RunSnapshot>(result, "构筑与敌情", HistoricalUiCoverageDisposition.HistoricalDetail,
            "羁绊、投资和敌人集合均显式显示。",
            nameof(RunSnapshot.SynergyIds),
            nameof(RunSnapshot.InvestmentEnvironmentId),
            nameof(RunSnapshot.InvestmentStrategyIds),
            nameof(RunSnapshot.EnemyIds));
        Add<RunSnapshot>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "事件链和诊断文本用于追溯，不混入概览。",
            nameof(RunSnapshot.Nodes),
            nameof(RunSnapshot.AppliedEventIds),
            nameof(RunSnapshot.Diagnostics));

        Add<NodeRecord>(result, "节点事件链", HistoricalUiCoverageDisposition.Diagnostics,
            "嵌套节点记录作为事件链证据展开显示。",
            nameof(NodeRecord.SchemaVersion),
            nameof(NodeRecord.NodeId),
            nameof(NodeRecord.StartedAt),
            nameof(NodeRecord.EndedAt),
            nameof(NodeRecord.Damage),
            nameof(NodeRecord.Economy),
            nameof(NodeRecord.RemainingActionPoints),
            nameof(NodeRecord.LineupIds),
            nameof(NodeRecord.EventIds));

        Add<ScreenshotAnalysisResult>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "分析版本、候选、建议、警告和未知项属于可展开诊断。",
            nameof(ScreenshotAnalysisResult.SchemaVersion),
            nameof(ScreenshotAnalysisResult.ApplicationVersion),
            nameof(ScreenshotAnalysisResult.AnalysisId),
            nameof(ScreenshotAnalysisResult.Snapshot),
            nameof(ScreenshotAnalysisResult.RouteCandidates),
            nameof(ScreenshotAnalysisResult.Recommendations),
            nameof(ScreenshotAnalysisResult.Warnings),
            nameof(ScreenshotAnalysisResult.UnknownFields),
            nameof(ScreenshotAnalysisResult.OperationalState));

        Add<Phase2OperationalState>(result, "节点概览", HistoricalUiCoverageDisposition.HistoricalDetail,
            "页面、节点与基础运营状态在节点详情显示。",
            nameof(Phase2OperationalState.PageFamily),
            nameof(Phase2OperationalState.PageId),
            nameof(Phase2OperationalState.NodeId),
            nameof(Phase2OperationalState.EnemyDifficulty),
            nameof(Phase2OperationalState.Interest),
            nameof(Phase2OperationalState.CumulativeSpend),
            nameof(Phase2OperationalState.PlayerProgress));
        Add<Phase2OperationalState>(result, "阵容与装备", HistoricalUiCoverageDisposition.HistoricalDetail,
            "最终备战阵容及资源明细在构筑区显示。",
            nameof(Phase2OperationalState.Formation),
            nameof(Phase2OperationalState.DismantleToolCount),
            nameof(Phase2OperationalState.SimpleEquipmentIds),
            nameof(Phase2OperationalState.SpecialItemIds),
            nameof(Phase2OperationalState.InventorySlots));
        Add<Phase2OperationalState>(result, "构筑与敌情", HistoricalUiCoverageDisposition.HistoricalDetail,
            "羁绊、词条和投资内容在构筑区显示。",
            nameof(Phase2OperationalState.ActiveSynergies),
            nameof(Phase2OperationalState.NegativeAffixIds),
            nameof(Phase2OperationalState.InvestmentEnvironmentId),
            nameof(Phase2OperationalState.InvestmentStrategyIds));
        Add<Phase2OperationalState>(result, "战斗结果", HistoricalUiCoverageDisposition.HistoricalDetail,
            "两种伤害来源、奖励、行动和最终战斗结果均保留。",
            nameof(Phase2OperationalState.BattleDamage),
            nameof(Phase2OperationalState.BattleSynergyDamage),
            nameof(Phase2OperationalState.BattleUnresolvedDamage),
            nameof(Phase2OperationalState.BattleScreenDamageCandidate),
            nameof(Phase2OperationalState.SettlementDamage),
            nameof(Phase2OperationalState.SettlementScreenDamageCandidate),
            nameof(Phase2OperationalState.SettlementGoldReward),
            nameof(Phase2OperationalState.StoreLevel),
            nameof(Phase2OperationalState.RemainingActionValue),
            nameof(Phase2OperationalState.FinalBattle));
        Add<Phase2OperationalState>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "命名内容、未知图标、残缺字段、轨迹和诊断可展开。",
            nameof(Phase2OperationalState.NamedContent),
            nameof(Phase2OperationalState.PendingIcons),
            nameof(Phase2OperationalState.PartialFields),
            nameof(Phase2OperationalState.RecognitionTrace),
            nameof(Phase2OperationalState.Diagnostics));

        Add<FinalNodeBattleState>(result, "战斗结果", HistoricalUiCoverageDisposition.HistoricalDetail,
            "最终战斗的数值、来源、通关与理论值全部显示。",
            nameof(FinalNodeBattleState.NodeId),
            nameof(FinalNodeBattleState.CharacterDamage),
            nameof(FinalNodeBattleState.TotalDamage),
            nameof(FinalNodeBattleState.RemainingActionValue),
            nameof(FinalNodeBattleState.CapturedAt),
            nameof(FinalNodeBattleState.SynergyDamage),
            nameof(FinalNodeBattleState.IsComplete),
            nameof(FinalNodeBattleState.CanDriveDecisions),
            nameof(FinalNodeBattleState.BattleScreenDamageCandidate),
            nameof(FinalNodeBattleState.SettlementScreenDamageCandidate),
            nameof(FinalNodeBattleState.SelectedDamage),
            nameof(FinalNodeBattleState.SelectedDamageSource),
            nameof(FinalNodeBattleState.SettlementTopThree),
            nameof(FinalNodeBattleState.GoldReward),
            nameof(FinalNodeBattleState.PreBattleHealth),
            nameof(FinalNodeBattleState.PostBattleHealth),
            nameof(FinalNodeBattleState.HealthDelta),
            nameof(FinalNodeBattleState.ClearStatus),
            nameof(FinalNodeBattleState.TheoreticalDamageLimit),
            nameof(FinalNodeBattleState.BaseMaximumActionValue),
            nameof(FinalNodeBattleState.ConfirmedActionIncrease),
            nameof(FinalNodeBattleState.EffectiveMaximumActionValue),
            nameof(FinalNodeBattleState.TheoreticalDamageQuality),
            nameof(FinalNodeBattleState.TheoreticalDamageRule),
            nameof(FinalNodeBattleState.IsRewardNode),
            nameof(FinalNodeBattleState.HealthDepleted),
            nameof(FinalNodeBattleState.FinalSynergyDamage),
            nameof(FinalNodeBattleState.AllRecordedDamage),
            nameof(FinalNodeBattleState.FinalSettlementTopThree));
        Add<FinalNodeBattleState>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "最终战斗证据、残缺来源与不确定原因进入诊断区。",
            nameof(FinalNodeBattleState.Evidence),
            nameof(FinalNodeBattleState.Uncertainty),
            nameof(FinalNodeBattleState.DegradedObservations),
            nameof(FinalNodeBattleState.PartialFields),
            nameof(FinalNodeBattleState.UnresolvedDamage),
            nameof(FinalNodeBattleState.FinalUncertainty),
            nameof(FinalNodeBattleState.FinalDegradedObservations),
            nameof(FinalNodeBattleState.FinalPartialFields),
            nameof(FinalNodeBattleState.FinalUnresolvedDamage));

        Add<FormationCharacterState>(result, "阵容与装备", HistoricalUiCoverageDisposition.HistoricalDetail,
            "每个角色的区域、槽位、身份、星级、站位和装备均逐项显示。",
            nameof(FormationCharacterState.Zone),
            nameof(FormationCharacterState.SlotIndex),
            nameof(FormationCharacterState.CharacterId),
            nameof(FormationCharacterState.StarLevel),
            nameof(FormationCharacterState.Standing),
            nameof(FormationCharacterState.EquipmentIds),
            nameof(FormationCharacterState.Confidence),
            nameof(FormationCharacterState.TemporaryId),
            nameof(FormationCharacterState.CandidateCharacterIds),
            nameof(FormationCharacterState.FailureReason),
            nameof(FormationCharacterState.CanDriveDecisions),
            nameof(FormationCharacterState.EquipmentSlots),
            nameof(FormationCharacterState.FinalEquipmentSlots));
        Add<FormationCharacterState>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "头像证据与裁剪区域用于复查。",
            nameof(FormationCharacterState.Evidence),
            nameof(FormationCharacterState.CardRegion));

        Add<CharacterEquipmentSlotState>(result, "阵容与装备",
            HistoricalUiCoverageDisposition.HistoricalDetail,
            "每个装备槽的占用状态、装备身份、候选和可靠性均逐槽显示。",
            nameof(CharacterEquipmentSlotState.SlotIndex),
            nameof(CharacterEquipmentSlotState.Occupancy),
            nameof(CharacterEquipmentSlotState.EquipmentId),
            nameof(CharacterEquipmentSlotState.CandidateEquipmentIds),
            nameof(CharacterEquipmentSlotState.Confidence),
            nameof(CharacterEquipmentSlotState.FailureReason),
            nameof(CharacterEquipmentSlotState.CanDriveDecisions));
        Add<CharacterEquipmentSlotState>(result, "识别诊断",
            HistoricalUiCoverageDisposition.Diagnostics,
            "装备槽裁剪区域和来源帧用于复查。",
            nameof(CharacterEquipmentSlotState.Region),
            nameof(CharacterEquipmentSlotState.Evidence));

        Add<InventorySlotState>(result, "背包与物品",
            HistoricalUiCoverageDisposition.HistoricalDetail,
            "每个背包槽的空槽、已知物品、未知物品和候选均逐槽显示。",
            nameof(InventorySlotState.SlotIndex),
            nameof(InventorySlotState.Occupancy),
            nameof(InventorySlotState.ItemKind),
            nameof(InventorySlotState.ItemId),
            nameof(InventorySlotState.CandidateItemIds),
            nameof(InventorySlotState.Confidence),
            nameof(InventorySlotState.FailureReason),
            nameof(InventorySlotState.CanDriveDecisions));
        Add<InventorySlotState>(result, "识别诊断",
            HistoricalUiCoverageDisposition.Diagnostics,
            "背包槽裁剪区域和来源帧用于复查及后续重识别。",
            nameof(InventorySlotState.Region),
            nameof(InventorySlotState.Evidence));

        Add<ActiveSynergyState>(result, "构筑与敌情", HistoricalUiCoverageDisposition.HistoricalDetail,
            "羁绊身份、激活层数和下一阈值均显示。",
            nameof(ActiveSynergyState.SynergyId),
            nameof(ActiveSynergyState.ActiveCount),
            nameof(ActiveSynergyState.NextThreshold),
            nameof(ActiveSynergyState.SlotKey),
            nameof(ActiveSynergyState.Confidence));
        Add<ActiveSynergyState>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "羁绊图标证据用于复查。", nameof(ActiveSynergyState.Evidence));

        Add<PlayerProgressState>(result, "经济与成长", HistoricalUiCoverageDisposition.HistoricalDetail,
            "等级和经验进度均显示。",
            nameof(PlayerProgressState.Level),
            nameof(PlayerProgressState.Experience),
            nameof(PlayerProgressState.ExperienceToNextLevel));

        Add<CharacterDamageState>(result, "伤害明细", HistoricalUiCoverageDisposition.HistoricalDetail,
            "角色排名、身份、伤害和原始文本均显示。",
            nameof(CharacterDamageState.Rank),
            nameof(CharacterDamageState.CharacterId),
            nameof(CharacterDamageState.Damage),
            nameof(CharacterDamageState.RawText),
            nameof(CharacterDamageState.AvatarConfidence),
            nameof(CharacterDamageState.DamageConfidence),
            nameof(CharacterDamageState.TemporaryId),
            nameof(CharacterDamageState.CandidateCharacterIds),
            nameof(CharacterDamageState.FailureReason),
            nameof(CharacterDamageState.CanDriveDecisions));
        Add<CharacterDamageState>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "头像/数值区域和证据用于复查。",
            nameof(CharacterDamageState.AvatarRegion),
            nameof(CharacterDamageState.DamageRegion),
            nameof(CharacterDamageState.Evidence));

        Add<SynergyDamageState>(result, "伤害明细", HistoricalUiCoverageDisposition.HistoricalDetail,
            "羁绊排名、身份、伤害和原始文本均显示。",
            nameof(SynergyDamageState.Rank),
            nameof(SynergyDamageState.SynergyId),
            nameof(SynergyDamageState.Damage),
            nameof(SynergyDamageState.RawText),
            nameof(SynergyDamageState.IconConfidence),
            nameof(SynergyDamageState.DamageConfidence),
            nameof(SynergyDamageState.TemporaryId),
            nameof(SynergyDamageState.CandidateSynergyIds),
            nameof(SynergyDamageState.FailureReason),
            nameof(SynergyDamageState.CanDriveDecisions));
        Add<SynergyDamageState>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "图标/数值区域和证据用于复查。",
            nameof(SynergyDamageState.IconRegion),
            nameof(SynergyDamageState.DamageRegion),
            nameof(SynergyDamageState.Evidence));

        Add<UnresolvedDamageSourceState>(result, "伤害明细", HistoricalUiCoverageDisposition.HistoricalDetail,
            "未知来源仍保留临时身份、类型、数值、候选和失败原因。",
            nameof(UnresolvedDamageSourceState.Rank),
            nameof(UnresolvedDamageSourceState.TemporaryId),
            nameof(UnresolvedDamageSourceState.SourceKind),
            nameof(UnresolvedDamageSourceState.SourceId),
            nameof(UnresolvedDamageSourceState.Damage),
            nameof(UnresolvedDamageSourceState.RawText),
            nameof(UnresolvedDamageSourceState.IconConfidence),
            nameof(UnresolvedDamageSourceState.DamageConfidence),
            nameof(UnresolvedDamageSourceState.CandidateIds),
            nameof(UnresolvedDamageSourceState.FailureReason),
            nameof(UnresolvedDamageSourceState.CanDriveDecisions));
        Add<UnresolvedDamageSourceState>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "图标/数值区域和证据用于复查。",
            nameof(UnresolvedDamageSourceState.IconRegion),
            nameof(UnresolvedDamageSourceState.DamageRegion),
            nameof(UnresolvedDamageSourceState.Evidence));

        Add<RemainingActionValueState>(result, "战斗结果", HistoricalUiCoverageDisposition.HistoricalDetail,
            "轮数、当前轮行动值和合计行动值均显示。",
            nameof(RemainingActionValueState.RemainingRounds),
            nameof(RemainingActionValueState.CurrentRoundActionValue),
            nameof(RemainingActionValueState.TotalActionValue));

        Add<Phase2NamedContentRecognition>(result, "构筑与敌情", HistoricalUiCoverageDisposition.HistoricalDetail,
            "对象类型、槽位、身份、名称、状态、置信度与候选均显示。",
            nameof(Phase2NamedContentRecognition.Kind),
            nameof(Phase2NamedContentRecognition.SlotKey),
            nameof(Phase2NamedContentRecognition.Status),
            nameof(Phase2NamedContentRecognition.ObjectId),
            nameof(Phase2NamedContentRecognition.StandardName),
            nameof(Phase2NamedContentRecognition.RawOcrTexts),
            nameof(Phase2NamedContentRecognition.Confidence),
            nameof(Phase2NamedContentRecognition.EvidenceKind),
            nameof(Phase2NamedContentRecognition.CandidateIds),
            nameof(Phase2NamedContentRecognition.Conflicts));
        Add<Phase2NamedContentRecognition>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "识别区域和来源帧用于复查。",
            nameof(Phase2NamedContentRecognition.Region),
            nameof(Phase2NamedContentRecognition.Evidence));

        Add<PendingIconObservation>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "未识别图标的类别、槽位、候选、裁剪和证据完整保留。",
            nameof(PendingIconObservation.Category),
            nameof(PendingIconObservation.SlotKey),
            nameof(PendingIconObservation.Region),
            nameof(PendingIconObservation.TemplateId),
            nameof(PendingIconObservation.Confidence),
            nameof(PendingIconObservation.Evidence),
            nameof(PendingIconObservation.Status),
            nameof(PendingIconObservation.CandidateTemplateIds),
            nameof(PendingIconObservation.TemporaryId),
            nameof(PendingIconObservation.RecognizedFields),
            nameof(PendingIconObservation.CanDriveDecisions),
            nameof(PendingIconObservation.CropFile));

        Add<Phase2PartialFieldObservation>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "残缺字段的已读内容、候选、原因、区域和证据完整保留。",
            nameof(Phase2PartialFieldObservation.Field),
            nameof(Phase2PartialFieldObservation.TemporaryId),
            nameof(Phase2PartialFieldObservation.Region),
            nameof(Phase2PartialFieldObservation.RecognizedFields),
            nameof(Phase2PartialFieldObservation.RawTexts),
            nameof(Phase2PartialFieldObservation.CandidateIds),
            nameof(Phase2PartialFieldObservation.Confidence),
            nameof(Phase2PartialFieldObservation.FailureReason),
            nameof(Phase2PartialFieldObservation.Evidence),
            nameof(Phase2PartialFieldObservation.CanDriveDecisions));

        Add<Phase2FieldRecognitionTrace>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "字段、页面、OCR、标准值、状态、区域、时间和降级原因完整保留。",
            nameof(Phase2FieldRecognitionTrace.Field),
            nameof(Phase2FieldRecognitionTrace.NodeId),
            nameof(Phase2FieldRecognitionTrace.SourcePageId),
            nameof(Phase2FieldRecognitionTrace.RawOcr),
            nameof(Phase2FieldRecognitionTrace.NormalizedValue),
            nameof(Phase2FieldRecognitionTrace.Status),
            nameof(Phase2FieldRecognitionTrace.Confidence),
            nameof(Phase2FieldRecognitionTrace.Attempt),
            nameof(Phase2FieldRecognitionTrace.DegradationReason),
            nameof(Phase2FieldRecognitionTrace.Region),
            nameof(Phase2FieldRecognitionTrace.CapturedAt),
            nameof(Phase2FieldRecognitionTrace.CropFile));

        Add<GuideMatch>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "流派候选属于解释性诊断，不冒充确定对局事实。",
            nameof(GuideMatch.GuideId), nameof(GuideMatch.ArchetypeId),
            nameof(GuideMatch.ArchetypeName), nameof(GuideMatch.Eligible),
            nameof(GuideMatch.Score), nameof(GuideMatch.Confidence),
            nameof(GuideMatch.Components), nameof(GuideMatch.Warnings),
            nameof(GuideMatch.MissingInformation));
        Add<ScoreComponent>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "候选评分分解可展开。",
            nameof(ScoreComponent.Name), nameof(ScoreComponent.Score),
            nameof(ScoreComponent.Weight), nameof(ScoreComponent.Explanation));
        Add<Recommendation>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "建议和依据仅作为历史诊断，不覆盖采集事实。",
            nameof(Recommendation.RecommendationId), nameof(Recommendation.GuideId),
            nameof(Recommendation.Priority), nameof(Recommendation.Action),
            nameof(Recommendation.IsNoAction), nameof(Recommendation.Confidence),
            nameof(Recommendation.Reasons), nameof(Recommendation.Benefits),
            nameof(Recommendation.Costs), nameof(Recommendation.Risks),
            nameof(Recommendation.Preconditions), nameof(Recommendation.InvalidatesWhen),
            nameof(Recommendation.MissingInformation), nameof(Recommendation.Sources));

        Add<EvidenceReference>(result, "识别诊断", HistoricalUiCoverageDisposition.Diagnostics,
            "证据来源、定位、摘要、时间和置信度均可展开。",
            nameof(EvidenceReference.SourceId), nameof(EvidenceReference.Locator),
            nameof(EvidenceReference.Summary), nameof(EvidenceReference.CapturedAt),
            nameof(EvidenceReference.Confidence));
        Add(typeof(Observation<>), result, "识别诊断",
            HistoricalUiCoverageDisposition.Diagnostics,
            "所有观察值统一展示状态、值、置信度、证据、未知原因和时间。",
            nameof(Observation<int>.Status), nameof(Observation<int>.Value),
            nameof(Observation<int>.Confidence), nameof(Observation<int>.Evidence),
            nameof(Observation<int>.Uncertainty), nameof(Observation<int>.ObservedAt));

        return result;
    }

    private static void Add<T>(
        ICollection<HistoricalUiFieldCoverage> result,
        string uiSection,
        HistoricalUiCoverageDisposition disposition,
        string rationale,
        params string[] properties) =>
        Add(typeof(T), result, uiSection, disposition, rationale, properties);

    private static void Add(
        Type modelType,
        ICollection<HistoricalUiFieldCoverage> result,
        string uiSection,
        HistoricalUiCoverageDisposition disposition,
        string rationale,
        params string[] properties)
    {
        foreach (var property in properties)
        {
            if (modelType.GetProperty(property, BindingFlags.Instance | BindingFlags.Public) is null)
            {
                throw new InvalidOperationException(
                    $"Historical UI coverage references missing property {modelType.Name}.{property}.");
            }

            result.Add(new HistoricalUiFieldCoverage(
                modelType,
                property,
                uiSection,
                disposition,
                rationale));
        }
    }
}

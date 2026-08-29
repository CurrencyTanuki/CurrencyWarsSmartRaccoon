using System.Text.Json.Serialization;

namespace CurrencyWarsAssistant.Advisor;

public sealed record RelativeRegion(
    double X,
    double Y,
    double Width,
    double Height);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Phase2PageFamily
{
    Unknown,
    Transition,
    Main,
    Preparation,
    Supply,
    Battle,
    BattleSettlement
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormationZone
{
    Front,
    Back,
    Bench,
    Special
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EquipmentSlotOccupancy
{
    Empty,
    Equipped,
    Unknown,
    Occluded
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryItemKind
{
    Unknown,
    SimpleEquipment,
    AdvancedEquipment,
    DismantleTool,
    SpecialItem
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PendingIconCategory
{
    CharacterAvatar,
    NegativeAffix,
    InvestmentEnvironment,
    InvestmentStrategy,
    Synergy,
    SimpleEquipment,
    AdvancedEquipment,
    SpecialItem,
    InventoryItem
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Phase2NamedContentKind
{
    NegativeAffix,
    InvestmentEnvironment,
    InvestmentStrategy,
    Synergy
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Phase2RecognitionEvidenceKind
{
    Ocr,
    Icon,
    OcrAndIcon,
    IconOnlyWithoutText
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BattleDamageSourceKind
{
    Character,
    Synergy,
    SpecialUnit,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinalDamageSelectionSource
{
    Unavailable,
    BattleLastFrame,
    SettlementTopThree
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NodeClearStatus
{
    Unknown,
    Perfect,
    NotPerfect
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TheoreticalDamageQuality
{
    Unknown,
    Exact,
    WalterObserved,
    WalterEstimated,
    ActionExhausted
}

public sealed record Phase2NamedContentRecognition(
    Phase2NamedContentKind Kind,
    string SlotKey,
    ObservationStatus Status,
    string? ObjectId,
    string? StandardName,
    IReadOnlyList<string> RawOcrTexts,
    double Confidence,
    RelativeRegion Region,
    Phase2RecognitionEvidenceKind EvidenceKind,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> Conflicts,
    EvidenceReference Evidence);

public sealed record PendingIconObservation(
    PendingIconCategory Category,
    string SlotKey,
    RelativeRegion Region,
    string? TemplateId,
    double Confidence,
    EvidenceReference Evidence,
    string Status,
    IReadOnlyList<string>? CandidateTemplateIds = null,
    string? TemporaryId = null,
    IReadOnlyDictionary<string, string>? RecognizedFields = null,
    bool CanDriveDecisions = false,
    string? CropFile = null);

public sealed record Phase2PartialFieldObservation(
    string Field,
    string TemporaryId,
    RelativeRegion Region,
    IReadOnlyDictionary<string, string> RecognizedFields,
    IReadOnlyList<string> RawTexts,
    IReadOnlyList<string> CandidateIds,
    double Confidence,
    string FailureReason,
    EvidenceReference Evidence,
    bool CanDriveDecisions = false);

public sealed record Phase2FieldRecognitionTrace(
    string Field,
    string? NodeId,
    string? SourcePageId,
    IReadOnlyList<string> RawOcr,
    string? NormalizedValue,
    ObservationStatus Status,
    double Confidence,
    int Attempt,
    string? DegradationReason,
    RelativeRegion Region,
    DateTimeOffset CapturedAt,
    string? CropFile = null);

public sealed record FormationCharacterState(
    FormationZone Zone,
    int SlotIndex,
    string CharacterId,
    int? StarLevel,
    string Standing,
    IReadOnlyList<string> EquipmentIds,
    double Confidence,
    EvidenceReference Evidence,
    string? TemporaryId = null,
    IReadOnlyList<string>? CandidateCharacterIds = null,
    string? FailureReason = null,
    bool CanDriveDecisions = true,
    RelativeRegion? CardRegion = null,
    IReadOnlyList<CharacterEquipmentSlotState>? EquipmentSlots = null,
    // 被打call特效加持（开拓者•欢愉在后台时给任意角色应援）：卡牌左右
    // 两侧出现应援棒（左冰蓝+右暖黄，动态摇晃）。仅备战页检测
    //（用户 2026-08-07：战斗页无此特效不检测）。识别层填值，报告渲染"应援"标记。
    bool IsCheered = false,
    // 猎星人标记（星核猎手羁绊：装备最多的星核猎手成为猎星人）：卡牌
    // 左上角出现金色徽章（米黄十字形凸起+中心四角星）。独立状态标记，
    // 非装备（用户 2026-08-07 确认）。识别层填值，报告渲染"猎星人"标记。
    bool IsHunterStar = false,
    // 特殊装备（不占普通装备槽位，显示在卡牌左上角）。欢愉卡带仅允许
    // 银狼/银狼 LV999 携带；识别层填值，报告渲染特殊装备图标。
    IReadOnlyList<string>? SpecialEquipmentIds = null,
    CharacterSpecialEquipmentState? SpecialEquipment = null,
    // 银狼 LV.999 等变费角色当前卡面费用。仅在命中的角色模板明确带费用
    // 语义时填写；普通角色和无法唯一判断的卡面保持 null。
    int? CurrentCost = null)
{
    public IReadOnlyList<CharacterEquipmentSlotState> FinalEquipmentSlots =>
        EquipmentSlots ?? [];
}

public enum Phase2FormationSlotOccupancy
{
    Recognized,
    Empty,
    Uncertain
}

/// <summary>
/// Result for a fixed formation slot that was actually scanned in this frame.
/// A missing key means unobserved; Empty is the only value allowed to clear an
/// existing slot; Uncertain keeps the committed value and schedules a retry.
/// </summary>
public sealed record Phase2FormationSlotObservation(
    FormationZone Zone,
    int SlotIndex,
    Phase2FormationSlotOccupancy Occupancy);

public sealed record CharacterSpecialEquipmentState(
    EquipmentSlotOccupancy Occupancy,
    string? EquipmentId,
    IReadOnlyList<string> CandidateEquipmentIds,
    IReadOnlyList<string> CandidateDisplayNames,
    double Confidence,
    RelativeRegion Region,
    EvidenceReference Evidence,
    string? FailureReason = null,
    bool CanDriveDecisions = true);

public sealed record CharacterEquipmentSlotState(
    int SlotIndex,
    EquipmentSlotOccupancy Occupancy,
    string? EquipmentId,
    IReadOnlyList<string> CandidateEquipmentIds,
    double Confidence,
    RelativeRegion Region,
    EvidenceReference Evidence,
    string? FailureReason = null,
    bool CanDriveDecisions = true,
    bool? IsPrivileged = null);

public sealed record InventorySlotState(
    int SlotIndex,
    EquipmentSlotOccupancy Occupancy,
    InventoryItemKind ItemKind,
    string? ItemId,
    IReadOnlyList<string> CandidateItemIds,
    double Confidence,
    RelativeRegion Region,
    EvidenceReference Evidence,
    string? FailureReason = null,
    bool CanDriveDecisions = true,
    int? Quantity = null);

public sealed record ActiveSynergyState(
    string? SynergyId,
    int? ActiveCount,
    int? NextThreshold,
    string SlotKey,
    double Confidence,
    EvidenceReference Evidence);

public sealed record PlayerProgressState(
    int Level,
    int Experience,
    int ExperienceToNextLevel);

public sealed record CharacterDamageState(
    int Rank,
    string? CharacterId,
    long Damage,
    string RawText,
    double AvatarConfidence,
    double DamageConfidence,
    RelativeRegion AvatarRegion,
    RelativeRegion DamageRegion,
    EvidenceReference Evidence,
    string? TemporaryId = null,
    IReadOnlyList<string>? CandidateCharacterIds = null,
    string? FailureReason = null,
    bool CanDriveDecisions = true);

public sealed record SynergyDamageState(
    int Rank,
    string? SynergyId,
    long Damage,
    string RawText,
    double IconConfidence,
    double DamageConfidence,
    RelativeRegion IconRegion,
    RelativeRegion DamageRegion,
    EvidenceReference Evidence,
    string? TemporaryId = null,
    IReadOnlyList<string>? CandidateSynergyIds = null,
    string? FailureReason = null,
    bool CanDriveDecisions = true);

public sealed record UnresolvedDamageSourceState(
    int Rank,
    string TemporaryId,
    BattleDamageSourceKind SourceKind,
    string? SourceId,
    long Damage,
    string RawText,
    double IconConfidence,
    double DamageConfidence,
    RelativeRegion IconRegion,
    RelativeRegion DamageRegion,
    IReadOnlyList<string> CandidateIds,
    string FailureReason,
    EvidenceReference Evidence,
    bool CanDriveDecisions = false);

public sealed record RemainingActionValueState(
    int RemainingRounds,
    int CurrentRoundActionValue,
    int TotalActionValue)
{
    public static RemainingActionValueState Create(
        int remainingRounds,
        int currentRoundActionValue)
    {
        if (remainingRounds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingRounds));
        }

        if (currentRoundActionValue is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentRoundActionValue));
        }

        return new RemainingActionValueState(
            remainingRounds,
            currentRoundActionValue,
            checked(remainingRounds * 100 + currentRoundActionValue));
    }
}

public sealed record FinalNodeBattleState(
    string NodeId,
    IReadOnlyList<CharacterDamageState> CharacterDamage,
    long? TotalDamage,
    RemainingActionValueState? RemainingActionValue,
    DateTimeOffset CapturedAt,
    EvidenceReference Evidence,
    IReadOnlyList<SynergyDamageState>? SynergyDamage = null,
    bool IsComplete = true,
    bool CanDriveDecisions = true,
    IReadOnlyList<string>? Uncertainty = null,
    IReadOnlyList<PendingIconObservation>? DegradedObservations = null,
    IReadOnlyList<Phase2PartialFieldObservation>? PartialFields = null,
    IReadOnlyList<UnresolvedDamageSourceState>? UnresolvedDamage = null,
    long? BattleScreenDamageCandidate = null,
    long? SettlementScreenDamageCandidate = null,
    long? SelectedDamage = null,
    FinalDamageSelectionSource SelectedDamageSource =
        FinalDamageSelectionSource.Unavailable,
    IReadOnlyList<CharacterDamageState>? SettlementTopThree = null,
    int? GoldReward = null,
    int? PreBattleHealth = null,
    int? PostBattleHealth = null,
    int? HealthDelta = null,
    NodeClearStatus ClearStatus = NodeClearStatus.Unknown,
    long? TheoreticalDamageLimit = null,
    int? BaseMaximumActionValue = null,
    int? ConfirmedActionIncrease = null,
    int? EffectiveMaximumActionValue = null,
    TheoreticalDamageQuality TheoreticalDamageQuality =
        TheoreticalDamageQuality.Unknown,
    string? TheoreticalDamageRule = null,
    bool IsRewardNode = false,
    bool HealthDepleted = false)
{
    public IReadOnlyList<SynergyDamageState> FinalSynergyDamage =>
        SynergyDamage ?? [];
    public IReadOnlyList<string> FinalUncertainty => Uncertainty ?? [];
    public IReadOnlyList<PendingIconObservation> FinalDegradedObservations =>
        DegradedObservations ?? [];
    public IReadOnlyList<Phase2PartialFieldObservation> FinalPartialFields =>
        PartialFields ?? [];
    public IReadOnlyList<UnresolvedDamageSourceState> FinalUnresolvedDamage =>
        UnresolvedDamage ?? [];
    public long AllRecordedDamage => checked(
        CharacterDamage.Sum(item => item.Damage) +
        FinalSynergyDamage.Sum(item => item.Damage) +
        FinalUnresolvedDamage.Sum(item => item.Damage));
    public IReadOnlyList<CharacterDamageState> FinalSettlementTopThree =>
        SettlementTopThree ?? [];
}

public sealed record Phase2OperationalState
{
    public Phase2PageFamily PageFamily { get; init; }
    public string? PageId { get; init; }
    public Observation<string> NodeId { get; init; } =
        Observation<string>.Unknown("not observed");
    /// <summary>备战页玩家血量（顶部中央偏右，2026-08-07 接入识别）。</summary>
    public Observation<int> Health { get; init; } =
        Observation<int>.Unknown("health 尚未识别", []);

    public Observation<int> EnemyDifficulty { get; init; } =
        Observation<int>.Unknown("not observed");
    public Observation<int> StoreLevel { get; init; } =
        Observation<int>.Unknown("not observed");
    public Observation<int> Interest { get; init; } =
        Observation<int>.Unknown("not observed");
    public Observation<int> CumulativeSpend { get; init; } =
        Observation<int>.Unknown("not observed");
    public Observation<PlayerProgressState> PlayerProgress { get; init; } =
        Observation<PlayerProgressState>.Unknown("not observed");
    /// <summary>人口上限（备战页中上区域 "N/N" 分母，如 8/8 → 8）。
    /// 2026-08-08 新增独立字段：旧实现只把容量并进 PlayerProgress，
    /// 等级 OCR 失败（progress Unknown）时人口一起丢失。</summary>
    public Observation<int> Population { get; init; } =
        Observation<int>.Unknown("not observed");
    public Observation<IReadOnlyList<FormationCharacterState>> Formation { get; init; } =
        Observation<IReadOnlyList<FormationCharacterState>>.Unknown("not observed");
    public IReadOnlyList<Phase2FormationSlotObservation> FormationSlotObservations
        { get; init; } = [];
    public bool FormationSlotObservationsArePartial { get; init; }
    public bool FormationSlotObservationsAreAtomic { get; init; }
    public long? FormationSlotTransactionId { get; init; }
    public Observation<IReadOnlyList<ActiveSynergyState>> ActiveSynergies { get; init; } =
        Observation<IReadOnlyList<ActiveSynergyState>>.Unknown("not observed");
    public Observation<int> DismantleToolCount { get; init; } =
        Observation<int>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> SimpleEquipmentIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> SpecialItemIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<InventorySlotState>> InventorySlots { get; init; } =
        Observation<IReadOnlyList<InventorySlotState>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> NegativeAffixIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("template library pending");
    public Observation<string> InvestmentEnvironmentId { get; init; } =
        Observation<string>.Unknown("template library pending");
    public Observation<IReadOnlyList<string>> InvestmentStrategyIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("template library pending");
    public Observation<IReadOnlyList<CharacterDamageState>> BattleDamage { get; init; } =
        Observation<IReadOnlyList<CharacterDamageState>>.Unknown("not observed");
    public Observation<IReadOnlyList<SynergyDamageState>> BattleSynergyDamage { get; init; } =
        Observation<IReadOnlyList<SynergyDamageState>>.Unknown("not observed");
    public Observation<IReadOnlyList<UnresolvedDamageSourceState>>
        BattleUnresolvedDamage { get; init; } =
            Observation<IReadOnlyList<UnresolvedDamageSourceState>>.Unknown("not observed");
    public Observation<long> BattleScreenDamageCandidate { get; init; } =
        Observation<long>.Unknown("not observed");
    public Observation<IReadOnlyList<CharacterDamageState>> SettlementDamage { get; init; } =
        Observation<IReadOnlyList<CharacterDamageState>>.Unknown("not observed");
    public Observation<long> SettlementScreenDamageCandidate { get; init; } =
        Observation<long>.Unknown("not observed");
    public Observation<int> SettlementGoldReward { get; init; } =
        Observation<int>.Unknown("not observed");
    public Observation<RemainingActionValueState> RemainingActionValue { get; init; } =
        Observation<RemainingActionValueState>.Unknown("not observed");
    public Observation<FinalNodeBattleState> FinalBattle { get; init; } =
        Observation<FinalNodeBattleState>.Unknown("battle has not been finalized");
    public IReadOnlyList<Phase2NamedContentRecognition> NamedContent { get; init; } = [];
    public IReadOnlyList<PendingIconObservation> PendingIcons { get; init; } = [];
    public IReadOnlyList<Phase2PartialFieldObservation> PartialFields { get; init; } = [];
    public IReadOnlyList<Phase2FieldRecognitionTrace> RecognitionTrace { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed record CompletedRunNodeRecord(
    string NodeId,
    RunSnapshot? FinalPreparationSnapshot,
    Phase2OperationalState? FinalPreparationState,
    FinalNodeBattleState? FinalBattle,
    string? PreparationAnalysisFile,
    string? FinalBattleFile);

public sealed record CompletedRunRecord
{
    public string SchemaVersion { get; init; } = AdvisorContractVersions.Current;
    public int ArchiveVersion { get; init; } = 1;
    public int ArchiveAlgorithmRevision { get; init; }
    public string? SourceRevision { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public bool IsFinal { get; init; } = true;
    public required string CompletionPageId { get; init; }
    public required string CompletionNodeId { get; init; }
    public string? CompletionScreenshotFile { get; init; }
    public string? RatingText { get; init; }
    public RunSnapshot? LastSnapshot { get; init; }
    public Phase2OperationalState? LastOperationalState { get; init; }
    public IReadOnlyList<CompletedRunNodeRecord> Nodes { get; init; } = [];
    public IReadOnlyList<string> SourceAnalysisFiles { get; init; } = [];
    public RunIdentityEvidence IdentityEvidence { get; init; } = new();
    public IReadOnlyList<string> Uncertainty { get; init; } = [];
}

using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Tasks;

public enum Phase2RecognitionMethod
{
    Ocr,
    CharacterAvatarTemplate,
    IconTemplate,
    PixelAndOcr,
    Derived
}

public enum Phase2SavePolicy
{
    MultiFrameConfirmed,
    OnChange,
    LatestCompleteBattleFrame,
    DerivedFromConfirmedFields,
    PendingTemplateLibrary
}

public sealed record Phase2RecognitionRegionDefinition(
    string Field,
    Phase2PageFamily Page,
    string Area,
    NormalizedRect Region,
    string DataType,
    Phase2RecognitionMethod Method,
    Phase2SavePolicy SavePolicy);

public static class Phase2RecognitionRegions
{
    public static IReadOnlyList<PixelRect> PreparationCharacterSlots1920 { get; } =
    [
        new(681, 329, 128, 140),
        new(827, 329, 122, 140),
        new(972, 329, 120, 140),
        new(1114, 329, 120, 140),
        new(535, 600, 140, 145),
        new(687, 600, 130, 145),
        new(829, 600, 130, 145),
        new(966, 600, 130, 145),
        new(1108, 600, 130, 145),
        new(1258, 600, 130, 145)
    ];

    // 后台格子等距对称模型（用户 2026-08-07 确认）：以屏幕对称轴 x=960
    // 为中心，等距 145px，格子大小固定，数量 6-9（基础 6 + 财富宝钻/
    // 特殊单位最多 +3）。预置 4 套坐标（用户要求：不现场生成，先数格子
    // 数再匹配对应套）。格子中心 = 960 ± i*145（6格:±362/217/72，
    // 7格:±435/290/145/0，8格:±507/362/217/72，9格:±580/435/290/145/0）。
    // 槽位尺寸沿用标准后台（宽~130 高~145，y=600）。
    private static readonly int[][] BackCenterOffsets1920 =
    {
        // 6 格
        new[] { -362, -217, -72, 72, 217, 362 },
        // 7 格
        new[] { -435, -290, -145, 0, 145, 290, 435 },
        // 8 格
        new[] { -507, -362, -217, -72, 72, 217, 362, 507 },
        // 9 格
        new[] { -580, -435, -290, -145, 0, 145, 290, 435, 580 }
    };

    /// <summary>后台格子坐标（按格数 6-9 选对应套，中心对称 960、等距 145）。</summary>
    public static IReadOnlyList<PixelRect> BackCharacterSlots1920(int count)
    {
        var index = count - 6;
        if (index < 0 || index >= BackCenterOffsets1920.Length)
        {
            // 超出范围回退 6 格
            index = 0;
        }

        return BackCenterOffsets1920[index]
            .Select(offset => new PixelRect(
                960 + offset - 65, // 中心-半宽(130/2)
                600,
                130,
                145))
            .ToArray();
    }

    // Opening the reward shop keeps the formation visible, but the game uses a
    // compact layout: front/back cards are smaller and shifted down. These
    // reference-space bounds were verified against 1920x1080, 2048x1152 and
    // 2560x1440 captures and continue to use the normal 16:9 scaling path.
    public static IReadOnlyList<PixelRect> RewardShopCharacterSlots1920 { get; } =
    [
        new(730, 414, 116, 142),
        new(845, 414, 116, 142),
        new(960, 414, 116, 142),
        new(1075, 414, 116, 142),
        new(580, 620, 150, 160),
        new(712, 620, 150, 160),
        new(844, 620, 150, 160),
        new(976, 620, 150, 160),
        new(1108, 620, 150, 160),
        new(1240, 620, 150, 160)
    ];

    public static IReadOnlyList<PixelRect> BenchCharacterSlots1920 { get; } =
    [
        new(383, 844, 114, 137),
        new(506, 844, 119, 137),
        new(633, 844, 117, 137),
        new(759, 844, 114, 137),
        new(883, 844, 116, 137),
        new(1005, 844, 116, 137),
        new(1128, 844, 116, 137),
        new(1250, 844, 116, 137),
        new(1374, 844, 116, 137)
    ];

    public static IReadOnlyList<NormalizedRect> NegativeAffixSlots { get; } =
        EvenSlots(0.113, 0.060, 0.014, 0.029, 4, 0.015);
    public static IReadOnlyList<NormalizedRect> NegativeAffixTextSlots { get; } =
        EvenSlots(0.103, 0.025, 0.052, 0.070, 4, 0.015);
    public static IReadOnlyList<NormalizedRect> InvestmentIconSlots { get; } =
        // The four HUD slots are spaced more tightly than the search button to
        // their right. A 0.028 pitch placed slot four over that search button,
        // turning an empty strategy slot into a persistent unknown icon.
        EvenSlots(0.447, 0.109, 0.020, 0.041, 4, 0.024);
    public static IReadOnlyList<NormalizedRect> InvestmentTextSlots { get; } =
        EvenSlots(0.426, 0.095, 0.064, 0.090, 4, 0.024);
    public static IReadOnlyList<NormalizedRect> SynergyIconSlots { get; } =
        EvenSlots(0.024, 0.130, 0.025, 0.038, 8, 0, 0.075);
    public static IReadOnlyList<NormalizedRect> SynergyTextSlots { get; } =
        EvenSlots(0.050, 0.120, 0.095, 0.058, 8, 0, 0.075);
    public static IReadOnlyList<NormalizedRect> InventoryIconSlots { get; } =
        // The artwork fills almost the entire 0.075 vertical pitch. The old
        // 0.060 crop cut off the lower 15-20% of every icon, which caused the
        // inventory matcher to compare partial silhouettes and prefer
        // unrelated advanced equipment.
        EvenSlots(0.940, 0.115, 0.040, 0.071, 7, 0, 0.075);
    public static readonly NormalizedRect PreparationDifficulty =
        new(0.052, 0.025, 0.115, 0.075);
    public static readonly NormalizedRect MainTitle =
        new(0.015, 0.045, 0.250, 0.155);
    public static readonly NormalizedRect MainStartAction =
        new(0.675, 0.800, 0.315, 0.175);
    public static readonly NormalizedRect PreparationAffixes =
        new(0.105, 0.025, 0.065, 0.070);
    public static readonly NormalizedRect PreparationNode =
        new(0.205, 0.015, 0.095, 0.090);
    public static readonly NormalizedRect PreparationNodeValue =
        new(0.225, 0.040, 0.055, 0.050);
    public static readonly NormalizedRect PreparationDifficultyValue =
        new(0.050, 0.025, 0.075, 0.065);
    public static readonly NormalizedRect PreparationDifficultyDigits =
        new(0.055, 0.052, 0.032, 0.040);
    public static readonly NormalizedRect PreparationHealth =
        new(0.730, 0.010, 0.055, 0.085);
    // 备战页右上角玩家血量数字区（2026-08-08 用 000032 像素标定：
    // 心形图标在上、数字 98 在 x=1399-1646/y=70-149 @1920。旧区域
    // (0.797,0.064,0.035,0.050) 只覆盖 "98" 右侧边缘 → 恒读 0）。
    public static readonly NormalizedRect PreparationHealthValue =
        new(0.720, 0.058, 0.140, 0.085);
    public static readonly NormalizedRect InvestmentSlots =
        new(0.440, 0.100, 0.125, 0.065);
    public static readonly NormalizedRect SynergyList =
        new(0.020, 0.115, 0.125, 0.610);
    public static readonly NormalizedRect PreparationFront =
        new(0.350, 0.280, 0.310, 0.190);
    public static readonly NormalizedRect PreparationBack =
        new(0.230, 0.535, 0.535, 0.185);
    public static readonly NormalizedRect SimpleEquipmentInventory =
        new(0.932, 0.105, 0.060, 0.335);
    public static readonly NormalizedRect DismantleToolCountValue =
        new(0.940, 0.115, 0.050, 0.090);
    public static readonly NormalizedRect DismantleToolCountDigits =
        new(0.970, 0.135, 0.020, 0.045);
    public static readonly NormalizedRect LevelAndExperience =
        new(0.115, 0.765, 0.080, 0.195);
    public static readonly NormalizedRect PlayerLevelDigits =
        new(0.151, 0.820, 0.030, 0.047);
    public static readonly NormalizedRect PreparationFrontCapacity =
        new(0.430, 0.180, 0.130, 0.090);
    // 备战页中上区域"人口 N/N"（图标中上数字，如 8/8）：2026-08-08 用
    // 000032 像素标定，数字在 x=826-1172/y=214-328 @1920（人形图标 + N/N）。
    // 旧区域 PreparationFrontCapacity 只覆盖左半。读出的 N/N 用作人口字段。
    public static readonly NormalizedRect PreparationPopulation =
        new(0.430, 0.198, 0.180, 0.120);
    public static readonly NormalizedRect Bench =
        new(0.190, 0.775, 0.575, 0.155);
    public static readonly NormalizedRect CumulativeSpend =
        new(0.775, 0.765, 0.035, 0.175);
    public static readonly NormalizedRect CumulativeSpendValue =
        new(0.785, 0.800, 0.028, 0.038);
    public static readonly NormalizedRect Interest =
        new(0.820, 0.765, 0.060, 0.075);
    public static readonly NormalizedRect InterestValue =
        new(0.846, 0.765, 0.020, 0.065);
    /// <summary>
    /// 备战页左下角"购买经验"区域（实测校准：2560×1440 截图
    /// 内容为"购买经验 Lv.N / N/N"与 4 金币图标）。
    /// </summary>
    public static readonly NormalizedRect StoreLevel =
        new(0.046, 0.532, 0.030, 0.121);
    // 商店 Lv（'购买经验 Lv.X'按钮，左下角）：2026-08-08 用 000035 标定——
    // Lv 数字在左下角 '购买经验' 标签右侧（1920 参考系 x≈0.17-0.22,
    // y≈0.84-0.87，用户参考图实测 Lv.7）。原区域 (0.048,0.548) 错位。
    // 商店 Lv（'购买经验 Lv.X'按钮，左下角）：2026-08-08 用 000032 像素
    // 标定——'购买经验'(上) → 'Lv.X'(中, 数字大) → '2/52'经验(下)。
    // 区域含 "Lv." 前缀与数字本体：反相+对比度拉伸后数字模板把 "l"→1、
    // "v"→7 误识别进前缀（如 17），读取时取最右侧 1-2 个 glyph 还原真值
    //（"Lv." 在左、数字在右）。纯数字窄裁剪投影分割失败（2026-08-08 实测）。
    // 2026-08-15 实测：商店等级 UI 有两版布局——
    // 旧版（000032, 8-08 参考图）：'Lv.' 小字在上、数字高约 56 在下方，
    //   全部位于 x336-460 窄带；旧区域 (0.148,0.790) 已验证对旧版 OK。
    // 新版（00023/01433, 8-12 帧）：'Lv.' 在数字左下、大数字 139×162
    //   位于 x516-655/y1135-1296；旧区域切掉数字右 24%/下 37% 致失败。
    // 单区域无法兼容两版：StoreLevelBigDigit 只裁新版纯大数字（无前缀/
    // 进度小字干扰），旧版帧该区域无黑字可干净回退 StoreLevelValue。
    public static readonly NormalizedRect StoreLevelValue =
        new(0.148, 0.790, 0.095, 0.070);
    // 新版 UI 商店等级纯大数字区（2026-08-15 两帧 2560×1440 像素投影
    // 标定：帧 x516-655, y1135-1296，留 margin 后归一化）。
    public static readonly NormalizedRect StoreLevelBigDigit =
        new(0.195, 0.780, 0.070, 0.125);
    public static readonly NormalizedRect Economy =
        new(0.820, 0.835, 0.060, 0.100);
    public static readonly NormalizedRect EconomyValue =
        // Keep the full leading digit while remaining to the right of the coin
        // icon. Starting at 0.855 clipped the 2 in a visible value of 23; the
        // bounded 0.849 start still excludes the icon that once produced 73.
        new(0.849, 0.835, 0.031, 0.045);
    public static readonly NormalizedRect EconomyValueNarrow =
        // Independent overlap used to reject a clipped leading digit or a
        // false component introduced at the left edge of the wider crop.
        new(0.855, 0.835, 0.025, 0.045);

    public static readonly NormalizedRect BattleNodeHealthEconomy =
        new(0.335, 0.000, 0.260, 0.070);
    public static readonly NormalizedRect BattleNodeValue =
        // Keep the OCR crop between the battle icon and the adjacent progress
        // percentage. The previous wider crop read "1-3 37%" as "1-337",
        // which made otherwise stable battle frames lose their node identity.
        new(0.365, 0.005, 0.035, 0.045);
    public static readonly NormalizedRect BattleNodeIdentity =
        // Page-family recognition needs enough context for OCR to retain the
        // separator in "1-3". This wider crop is read-only and is not used by
        // mouse input, so it may include the battle icon and adjacent percent.
        new(0.345, 0.000, 0.075, 0.075);
    public static readonly NormalizedRect BattleActionTimeline =
        new(0.005, 0.035, 0.100, 0.720);
    public static readonly NormalizedRect BattleDamagePanel =
        new(0.840, 0.165, 0.150, 0.520);
    public static readonly NormalizedRect BattleDamageHeader =
        new(0.800, 0.055, 0.195, 0.130);
    public static readonly NormalizedRect BattleActionIndicator =
        new(0.830, 0.820, 0.165, 0.170);

    public static readonly NormalizedRect SettlementTitle =
        new(0.390, 0.150, 0.220, 0.145);
    public static readonly NormalizedRect SettlementSemanticBody =
        new(0.245, 0.235, 0.510, 0.520);
    public static readonly NormalizedRect SettlementAction =
        new(0.355, 0.775, 0.290, 0.145);
    public static readonly NormalizedRect SettlementNodeValue =
        new(0.400, 0.225, 0.105, 0.070);
    public static readonly NormalizedRect SettlementHealth =
        new(590d / 1920d, 445d / 1080d, 330d / 1920d, 105d / 1080d);
    public static readonly NormalizedRect SettlementGoldReward =
        new(0.510, 0.485, 0.065, 0.075);
    public static readonly NormalizedRect SettlementGoldRewardLabeledRow =
        new(0.260, 0.485, 0.315, 0.075);
    public static readonly NormalizedRect SettlementGoldRewardIcon =
        new(0.520, 0.493, 0.030, 0.055);
    public static readonly NormalizedRect SettlementGoldRewardDigit =
        new(0.535, 0.500, 0.040, 0.060);
    public static readonly NormalizedRect SettlementDamagePanel =
        new(0.570, 0.480, 0.180, 0.310);

    public static NormalizedRect SettlementDamageAvatar(int row) =>
        RowRegion(row, 0.582, 0.562, 0.040, 0.062, 0.075);

    public static NormalizedRect SettlementDamageValue(int row) =>
        // 结算伤害数值区域（B 修复，用户 2026-08-06 深夜确认）：
        // 原宽 0.105（≈269px@2560）能读到数字但常截不到右侧"万"单位
        //（3-5 实测 rawText="4038260.9 (settlement unit inferred as 万)"
        // → ambiguousScale=true 判 partial 弃用）。加宽到 0.126 把单位
        // 框进裁剪，消除单位推断。右界 0.622+0.126=0.748 不超面板 0.750
        //（review 2026-08-06 指出 0.135 会越界 7px，已收紧）。
        RowRegion(row, 0.622, 0.575, 0.126, 0.052, 0.075);

    public static NormalizedRect SettlementDamageBar(int row) =>
        RowRegion(row, 0.615, 0.565, 0.115, 0.018, 0.071);

    public static NormalizedRect BattleDamageAvatar(int row) =>
        // 战斗伤害榜头像区域（C1 修正，2026-08-18 用户确认后校准）：
        // 旧区域 (0.852,0.232,0.030,0.050) 偏左上 → 曾把 rank1 银狼误判成
        // 大黑塔(conf 0.477<0.48)。先按圆心(0.8701,0.2621)置为 1.2x 圆框
        // (0.851,0.243,0.038,0.038)，但用户肉眼复核：该框"宽太宽(截到非
        // 头像)、高太矮(截掉部分头像)"。最终改为以圆心为中心的正方形
        // ~90px 头像框：x∈[2182,2272] y∈[332,422] @2560x1440
        // → 归一 (0.8523,0.2306,0.0352,0.0625)，宽/高等比收窄/加高贴合框。
        RowRegion(row, 0.8523, 0.2306, 0.0352, 0.0625);

    public static NormalizedRect BattleDamageValue(int row) =>
        // 伤害数值区域（A 修复，用户 2026-08-06 深夜确认）：
        // 原宽 0.060（≈154px@2560）装不下大数字（如 1424.76亿 → 只读出
        // 142.47亿 少一位）。加宽到 0.100（≈256px）容纳完整数值。
        // End above the colored bar at y=0.266. The old 0.038 height reached
        // y=0.270 and included about six bar pixels at 2560x1440, which could
        // turn an empty row decoration into OCR text such as "1" or "117".
        RowRegion(row, 0.883, 0.232, 0.100, 0.034);

    public static NormalizedRect BattleDamageBar(int row) =>
        RowRegion(row, 0.883, 0.266, 0.082, 0.012);

    public static IReadOnlyList<NormalizedRect> CharacterEquipmentSlots(
        PixelRect referenceCharacterBounds,
        bool compactFrontLayout = false) => CharacterEquipmentSlots(
            referenceCharacterBounds,
            3,
            compactFrontLayout);

    public static IReadOnlyList<NormalizedRect> CharacterEquipmentSlots(
        PixelRect referenceCharacterBounds,
        int occupiedSlotCount,
        bool compactFrontLayout = false)
    {
        if (occupiedSlotCount is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(occupiedSlotCount));
        }

        const double slotWidthRatio = 0.34;
        const double slotPitchRatio = 0.33;
        var groupWidthRatio = occupiedSlotCount == 0
            ? 0
            : slotWidthRatio + (occupiedSlotCount - 1) * slotPitchRatio;
        var groupStartRatio = (1 - groupWidthRatio) / 2;
        return Enumerable.Range(0, occupiedSlotCount)
            .Select(index => new NormalizedRect(
                (referenceCharacterBounds.X +
                 referenceCharacterBounds.Width *
                 (groupStartRatio + index * slotPitchRatio)) / 1920d,
                 // Standard preparation icons sit just below the card at 0.96H.
                 // Reward-shop Front cards use a distinct compact layout: native
                 // 1080p/1440p captures place the square icon body at 0.83-1.09H.
                 // The former 0.90H/0.30H crop cut off the icon top and selected a
                 // different shared template group on the real 1-4 shop frame.
                 (referenceCharacterBounds.Y +
                 referenceCharacterBounds.Height *
                 (compactFrontLayout ? 0.83 : 0.96)) / 1080d,
                 referenceCharacterBounds.Width * 0.34 / 1920d,
                 referenceCharacterBounds.Height *
                 (compactFrontLayout ? 0.26 : 0.32) / 1080d))
            .ToArray();
    }

    public static NormalizedRect CharacterEquipmentIconBand(
        PixelRect referenceCharacterBounds) => new(
            // 2026-08-10 用户实机确认（034916 图）：装备带紧贴卡面底——
            // y 起点 = 卡y + 0.95 卡高、高 0.26 卡高。x 起点 = 卡x + 0.03 卡宽。
            (referenceCharacterBounds.X +
             referenceCharacterBounds.Width * 0.03) / 1920d,
            (referenceCharacterBounds.Y +
             referenceCharacterBounds.Height * 0.95) / 1080d,
            referenceCharacterBounds.Width * 1.10 / 1920d,
            referenceCharacterBounds.Height * 0.26 / 1080d);

    /// <summary>
    /// 特殊装备区：卡牌左上角显示一件不占普通装备槽的特殊装备。
    /// 当猎星人标签同时存在时，两者竖直排列，特殊装备在上、猎星人在下；
    /// 因此这里只截上方图标，不把下方猎星人标签混入装备识别。
    /// </summary>
    public static NormalizedRect CharacterSpecialEquipmentRegion(
        PixelRect referenceCharacterBounds) => new(
            (referenceCharacterBounds.X +
             referenceCharacterBounds.Width * 0.02) / 1920d,
            (referenceCharacterBounds.Y +
             referenceCharacterBounds.Height * 0.02) / 1080d,
            referenceCharacterBounds.Width * 0.24 / 1920d,
            referenceCharacterBounds.Height * 0.23 / 1080d);

    public static IReadOnlyList<Phase2RecognitionRegionDefinition> All { get; } =
    [
        new("enemyDifficulty", Phase2PageFamily.Preparation, "左上角评分", PreparationDifficulty, "integer", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("negativeAffixes", Phase2PageFamily.Preparation, "左上角附属图标组", PreparationAffixes, "named-content[]", Phase2RecognitionMethod.PixelAndOcr, Phase2SavePolicy.MultiFrameConfirmed),
        new("nodeId", Phase2PageFamily.Preparation, "顶部中央阶段", PreparationNode, "plane-node", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("health", Phase2PageFamily.Preparation, "顶部中央偏右生命", PreparationHealth, "integer", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("investmentEnvironmentAndStrategies", Phase2PageFamily.Preparation, "中间偏上四槽", InvestmentSlots, "named-content[4]", Phase2RecognitionMethod.PixelAndOcr, Phase2SavePolicy.MultiFrameConfirmed),
        new("activeSynergies", Phase2PageFamily.Preparation, "左侧纵向羁绊列表", SynergyList, "named-synergy-tier[]", Phase2RecognitionMethod.PixelAndOcr, Phase2SavePolicy.MultiFrameConfirmed),
        new("frontFormation", Phase2PageFamily.Preparation, "前台区域", PreparationFront, "character-slot[]", Phase2RecognitionMethod.CharacterAvatarTemplate, Phase2SavePolicy.MultiFrameConfirmed),
        new("backFormation", Phase2PageFamily.Preparation, "后台区域", PreparationBack, "character-slot[]", Phase2RecognitionMethod.CharacterAvatarTemplate, Phase2SavePolicy.MultiFrameConfirmed),
        new("advancedEquipment", Phase2PageFamily.Preparation, "角色卡下方装备", new(0.230, 0.420, 0.535, 0.315), "equipment-by-character", Phase2RecognitionMethod.IconTemplate, Phase2SavePolicy.MultiFrameConfirmed),
        new("simpleEquipmentAndTools", Phase2PageFamily.Preparation, "右侧物品栏", SimpleEquipmentInventory, "inventory", Phase2RecognitionMethod.IconTemplate, Phase2SavePolicy.MultiFrameConfirmed),
        new("playerProgress", Phase2PageFamily.Preparation, "左下购买经验", LevelAndExperience, "level-and-experience", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("benchFormation", Phase2PageFamily.Preparation, "底部候补角色区", Bench, "character-slot[]", Phase2RecognitionMethod.CharacterAvatarTemplate, Phase2SavePolicy.MultiFrameConfirmed),
        new("cumulativeSpend", Phase2PageFamily.Preparation, "右下火焰连胜数", CumulativeSpend, "integer", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("interest", Phase2PageFamily.Preparation, "右下循环箭头", Interest, "integer", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("economy", Phase2PageFamily.Preparation, "右下金币", Economy, "integer", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("nodeHealthEconomy", Phase2PageFamily.Battle, "顶部中央状态条", BattleNodeHealthEconomy, "node-health-gold", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("remainingActionValue", Phase2PageFamily.Battle, "左侧行动条彩色倒计时行", BattleActionTimeline, "rounds-and-action-value", Phase2RecognitionMethod.PixelAndOcr, Phase2SavePolicy.LatestCompleteBattleFrame),
        new("characterDamage", Phase2PageFamily.Battle, "右侧伤害榜", BattleDamagePanel, "character-damage[]", Phase2RecognitionMethod.CharacterAvatarTemplate, Phase2SavePolicy.LatestCompleteBattleFrame),
        new("synergyDamage", Phase2PageFamily.Battle, "右侧伤害榜中的无文字羁绊行", BattleDamagePanel, "synergy-damage[]", Phase2RecognitionMethod.IconTemplate, Phase2SavePolicy.LatestCompleteBattleFrame),
        new("settlementTopDamage", Phase2PageFamily.BattleSettlement, "结算页右侧数据统计", SettlementDamagePanel, "top-three-character-damage[]", Phase2RecognitionMethod.CharacterAvatarTemplate, Phase2SavePolicy.MultiFrameConfirmed),
        new("settlementGoldReward", Phase2PageFamily.BattleSettlement, "结算页左侧获得金币总览", SettlementGoldReward, "integer", Phase2RecognitionMethod.Ocr, Phase2SavePolicy.MultiFrameConfirmed),
        new("nodeTotalDamage", Phase2PageFamily.BattleSettlement, "战斗最后一帧与结算前三名候选值取较大值", SettlementDamagePanel, "integer", Phase2RecognitionMethod.Derived, Phase2SavePolicy.DerivedFromConfirmedFields)
    ];

    private static NormalizedRect RowRegion(
        int row,
        double x,
        double y,
        double width,
        double height,
        double stepY = 0.064)
    {
        if (row is < 0 or >= 8)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        return new NormalizedRect(x, y + row * stepY, width, height);
    }

    private static IReadOnlyList<NormalizedRect> EvenSlots(
        double x,
        double y,
        double width,
        double height,
        int count,
        double stepX,
        double stepY = 0) => Enumerable.Range(0, count)
        .Select(index => new NormalizedRect(
            x + index * stepX,
            y + index * stepY,
            width,
            height))
        .ToArray();
}

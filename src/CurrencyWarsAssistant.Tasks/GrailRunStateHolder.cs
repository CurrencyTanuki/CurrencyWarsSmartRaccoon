using System;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」编排层运行状态持有器（每局一个实例，跨帧持久）。
/// <para>
/// 承载两类事实：
/// ① 事件态——只能由编排/执行层回填、识别层看不见的进度（祈愿响应计数、聘用书获得/打开数、
///    奇迹代偿与无限之釜旗标及选择时血量、5 档放弃闩锁、第 5 成员出现标记）；
/// ② last-known 缓存——弹框/过渡期识别为 Unknown 的血量/人口/金币，按最近一次确认值+时间戳缓存，
///    组装器按陈旧度窗口决定是否继续可信（超龄按未识别，F13 防御口径）。
/// </para>
/// <para>
/// 线程模型：识别事件与循环任务可能来自不同线程，全部经 <see cref="_gate"/> 串行化
/// （与既有 FateGrailLiveSnapshotSource 的锁模式一致）。每局新实例，跨轮状态天然清零。
/// </para>
/// </summary>
public sealed class GrailRunStateHolder
{
    private readonly object _gate = new();

    private int _wishesResponded;
    private int _lettersObtained;
    private int _lettersOpened;
    private bool _miracleCompensationSelected;
    private int? _miracleCompensationSelectedAtHealth;
    private bool _infiniteCauldronSelected;
    private bool _fiveBondGivenUp;
    private bool _newBondMemberAvailable;
    private bool _refreshSurcharge; // 行为限制/行为禁锢：刷新价格+1
    private bool _xpSurcharge;      // 回路过载/回路超频：购买经验价格+1
    private bool _openingFormationApplied; // N13/N12/N2 开局动作闩锁（每局一次）

    /// <summary>本局已在商店买到的角色名（同名只买一次；买完立即记录，跨帧持久，供去重）。</summary>
    private readonly HashSet<string> _purchasedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本局星徽携带者账本（2026-09-03 用户拍板：星徽一旦装备到角色身上，本局内恒绑定该角色，
    /// 不卸下/卖掉不转移——角色装备识别漏读由本账本兜底，识别读数与账本取并集，识别永不推翻账本）。
    /// 记账两级：A4 只传位置语义，先落 <see cref="_badgePositionsPending"/>（槽位→角色名未定）；
    /// 组装器首次在该槽位识别到角色时提升为按名记账（角色换槽后仍随名携带）。
    /// </summary>
    private readonly HashSet<string> _badgeCarrierNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _badgePositionsPending = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>挂起槽位连续缺席计数（坑38 批次修复：动画期单帧可能整体丢槽，
    /// 单帧缺席不消化——连续 ≥2 帧 Known 缺席才认定真空槽）。</summary>
    private readonly Dictionary<string, int> _badgeAbsenceStrikes = new(StringComparer.OrdinalIgnoreCase);

    private int? _lastHealth;
    private DateTimeOffset? _healthCapturedAt;
    private int? _lastPopulation;
    private DateTimeOffset? _populationCapturedAt;
    private int? _lastGold;
    private DateTimeOffset? _goldCapturedAt;

    /// <summary>
    /// 跨局复位（审计#6：同一刷取会话可能连续多局共用本持有器，事件态/缓存若不复位，
    /// 第 2 局会继承第 1 局的祈愿计数/聘用书/旗标 → G1③ 提前判死或假成功）。
    /// 调用点=对局边界：生产 GrailRunLoop 每轮 new 持有器天然清零；指令测试台路径由
    /// M8（GrailMacroCommands.RunOpeningAsync）显式调用（1.2.32 起）。
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _wishesResponded = 0;
            _lettersObtained = 0;
            _lettersOpened = 0;
            _miracleCompensationSelected = false;
            _miracleCompensationSelectedAtHealth = null;
            _infiniteCauldronSelected = false;
            _fiveBondGivenUp = false;
            _newBondMemberAvailable = false;
            _refreshSurcharge = false;
            _xpSurcharge = false;
            _openingFormationApplied = false;
            _purchasedNames.Clear();
            _badgeCarrierNames.Clear();
            _badgePositionsPending.Clear();
            _badgeAbsenceStrikes.Clear();
            _lastFormationSlotCount = -1;
            _lastHealth = null;
            _healthCapturedAt = null;
            _lastPopulation = null;
            _populationCapturedAt = null;
            _lastGold = null;
            _goldCapturedAt = null;
        }
    }

    // ---------- 识别值捕获（仅 Known 值落缓存；null 表示本帧 Unknown，不清缓存） ----------

    public void CaptureHealth(int? value, DateTimeOffset capturedAt)
    {
        if (value is < 1 or > 100)
        {
            return; // tracker 口径外的噪声值不落缓存（0..100）
        }

        lock (_gate)
        {
            if (value.HasValue)
            {
                _lastHealth = value;
                _healthCapturedAt = capturedAt;
            }
        }
    }

    public void CapturePopulation(int? value, DateTimeOffset capturedAt)
    {
        lock (_gate)
        {
            if (value is > 0)
            {
                _lastPopulation = value;
                _populationCapturedAt = capturedAt;
            }
        }
    }

    public void CaptureGold(int? value, DateTimeOffset capturedAt)
    {
        lock (_gate)
        {
            if (value is >= 0)
            {
                _lastGold = value;
                _goldCapturedAt = capturedAt;
            }
        }
    }

    // ---------- 事件态回填 ----------

    /// <summary>
    /// 把一次弹框响应落地（语义与 <see cref="GrailTrialResponseDecider.ApplyTo"/> 一致）。
    /// healthAtSelection 必须传点击前识别到的血量（确认后血已扣 88）。
    /// </summary>
    public void ApplyTrialResponse(GrailTrialResponse response, int? healthAtSelection)
    {
        ArgumentNullException.ThrowIfNull(response);
        var selected = response.SelectedTrialName ?? string.Empty;
        var isMiracle = GrailFuzzyText.ContainsFuzzy(selected, GrailTrialResponseDecider.MiracleKeyword);
        var isCauldron = GrailFuzzyText.ContainsFuzzy(selected, GrailTrialResponseDecider.CauldronKeyword);

        lock (_gate)
        {
            _wishesResponded++;
            if (isMiracle)
            {
                _miracleCompensationSelected = true;
                _miracleCompensationSelectedAtHealth = healthAtSelection;
            }

            if (isCauldron)
            {
                _infiniteCauldronSelected = true;
            }

            if (isMiracle)
            {
                // 奇迹代偿代价 = 扣 88 血 + 全部金币：选中即把金币缓存清 0（决策层不得再高估）
                _lastGold = 0;
                _goldCapturedAt = DateTimeOffset.Now;
            }

            // 诅咒代价按数据文档分开判定：行为限制(禁锢)=刷新价格+1；回路过载(超频)=购买经验价格+1
            if (GrailFuzzyText.ContainsFuzzy(selected, "行为限制")
                || GrailFuzzyText.ContainsFuzzy(selected, "行为禁锢"))
            {
                _refreshSurcharge = true;
            }

            if (GrailFuzzyText.ContainsFuzzy(selected, "回路过载")
                || GrailFuzzyText.ContainsFuzzy(selected, "回路超频"))
            {
                _xpSurcharge = true;
            }

            if (response.OpenLettersAfter)
            {
                _lettersObtained += 2;
            }
        }
    }

    /// <summary>F11a：聘用书逐本打开成功后 +1（不得一次跳到 obtained，防止虚报绕过 L4“全部打开”校验）。</summary>
    public void MarkLetterOpened()
    {
        lock (_gate)
        {
            _lettersOpened++;
        }
    }

    /// <summary>编排层旁路获得聘用书时使用（常规路径走 <see cref="ApplyTrialResponse"/> 的 OpenLettersAfter）。</summary>
    public void MarkLettersObtained(int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _lettersObtained += count;
        }
    }

    /// <summary>第 5 个羁绊成员已出现（商店刷出未拥有命杯成员 / 第二枚星徽到手）。</summary>
    public void MarkNewBondMemberAvailable()
    {
        lock (_gate)
        {
            _newBondMemberAvailable = true;
        }
    }

    /// <summary>N17b：5 档激活机会永久放弃（闩锁）。</summary>
    public void GiveUpFiveBond()
    {
        lock (_gate)
        {
            _fiveBondGivenUp = true;
        }
    }

    /// <summary>N13/N12/N2 开局动作是否已执行（闩锁，每局一次；Reset 清空）。</summary>
    public bool PeekOpeningFormationApplied()
    {
        lock (_gate)
        {
            return _openingFormationApplied;
        }
    }

    /// <summary>标记 N13/N12/N2 开局动作已执行（闩锁置位，防止本局重复拖拽）。</summary>
    public void MarkOpeningFormationApplied()
    {
        lock (_gate)
        {
            _openingFormationApplied = true;
        }
    }

    /// <summary>令咒决议诅咒是否已使商店刷新价格 +1（行为限制系选中后为真，保守全程生效）。</summary>
    public bool PeekRefreshSurcharge()
    {
        lock (_gate)
        {
            return _refreshSurcharge;
        }
    }

    /// <summary>令咒决议诅咒是否已使购买经验价格 +1（回路过载系选中后为真；买经验两次共+2金）。</summary>
    public bool PeekXpSurcharge()
    {
        lock (_gate)
        {
            return _xpSurcharge;
        }
    }

    /// <summary>记录一名已在商店买到的角色（同名只买一次的核心：买完立即持久化，跨帧去重）。</summary>
    public void RecordPurchased(string name)
    {
        lock (_gate)
        {
            _purchasedNames.Add(name);
        }
    }

    private int _lastFormationSlotCount = -1;
    private DateTimeOffset _lastFormationSlotCountAt;

    /// <summary>阵容识别格数突变守卫（1.2.31）：6 秒内总格数变化 &gt;2 = 疑似过渡/坏帧，
    /// 读数不可信（合法操作每 4 秒最多 ±1~2；晶矿连开爆发允许一次重试）。记录即更新基线。</summary>
    public bool IsFormationCountPlausible(int count, DateTimeOffset now, out string note)
    {
        lock (_gate)
        {
            var last = _lastFormationSlotCount;
            var lastAt = _lastFormationSlotCountAt;
            _lastFormationSlotCount = count;
            _lastFormationSlotCountAt = now;
            note = $"阵容格数 {count}（前值 {last}，间隔 {(now - lastAt).TotalSeconds:F0}s）";
            if (last < 0)
            {
                return true;
            }

            return Math.Abs(count - last) <= 2 || (now - lastAt).TotalSeconds >= 6;
        }
    }

    /// <summary>本局商店已买到的角色名集合（与识别快照 OwnedCharacterNames 并集做去重）。</summary>
    public IReadOnlySet<string> PurchasedNames()
    {
        lock (_gate)
        {
            return new HashSet<string>(_purchasedNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ---------- 星徽携带者账本（识别漏读兜底，2026-09-03 用户拍板） ----------

    /// <summary>A4 装配成功即调用（位置语义：如 "F2"/"B3"，1 基槽位号拼进键）。
    /// 此时角色名未知，先挂起；组装器在该槽位首次识别到角色时提升为按名记账。</summary>
    public void RecordBadgeEquippedAtSlot(string slotKey)
    {
        if (string.IsNullOrWhiteSpace(slotKey))
        {
            return;
        }

        lock (_gate)
        {
            _badgePositionsPending[slotKey] = null;
        }
    }

    /// <summary>已知角色名的装配路径直接按名记账（预留 API：当前生产路径 A4 只知槽位，
    /// 经组装器提升；名字直记供未来"装配时已知角色名"的流程使用）。</summary>
    public void RecordBadgeCarrierName(string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return;
        }

        lock (_gate)
        {
            _badgeCarrierNames.Add(characterName);
        }
    }

    /// <summary>
    /// 挂起槽位纠账（审查 P2 修复+坑38 批次加固）：阵容 Known 的帧里不在
    /// <paramref name="occupiedSlotKeys"/> 内的挂起槽位累计缺席一次，**连续缺席 ≥2 帧**
    /// 才认定真空槽并消化（动画期单帧可能整体丢槽，单帧缺席绝不消化——防把 A4 刚记的账吃掉）。
    /// 槽位重新出现即清零计数。已提升为名字键的携带者不受影响（徽绑定角色）。
    /// </summary>
    public void ConsumeBadgePendingSlotsExcept(IReadOnlySet<string> occupiedSlotKeys)
    {
        lock (_gate)
        {
            foreach (var key in _badgePositionsPending.Keys.ToList())
            {
                if (occupiedSlotKeys.Contains(key))
                {
                    _badgeAbsenceStrikes.Remove(key);
                    continue;
                }

                var strikes = _badgeAbsenceStrikes.GetValueOrDefault(key) + 1;
                if (strikes >= 2)
                {
                    _badgePositionsPending.Remove(key);
                    _badgeAbsenceStrikes.Remove(key);
                }
                else
                {
                    _badgeAbsenceStrikes[key] = strikes;
                }
            }
        }
    }

    /// <summary>
    /// 组装器在挂起槽位识别到角色时调用：槽位记账提升为名字记账（并清除挂起项，
    /// 防同一枚徽在角色换槽后被旧槽位重复计数）。槽位上没有角色则不动。
    /// </summary>
    public void PromoteBadgeCarrier(string slotKey, string characterName)
    {
        if (string.IsNullOrWhiteSpace(slotKey) || string.IsNullOrWhiteSpace(characterName))
        {
            return;
        }

        lock (_gate)
        {
            if (_badgePositionsPending.ContainsKey(slotKey))
            {
                _badgePositionsPending.Remove(slotKey);
                _badgeCarrierNames.Add(characterName);
            }
        }
    }

    /// <summary>当前星徽账本快照：已按名记账的携带者 + 尚未解析角色的挂起槽位。</summary>
    public (IReadOnlySet<string> CarrierNames, IReadOnlyDictionary<string, string?> PendingSlots) PeekBadgeLedger()
    {
        lock (_gate)
        {
            return (
                new HashSet<string>(_badgeCarrierNames, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string?>(_badgePositionsPending, StringComparer.OrdinalIgnoreCase));
        }
    }

    // ---------- 读取（供组装器） ----------

    public (int? Value, DateTimeOffset? CapturedAt) PeekHealth()
    {
        lock (_gate)
        {
            return (_lastHealth, _healthCapturedAt);
        }
    }

    public (int? Value, DateTimeOffset? CapturedAt) PeekPopulation()
    {
        lock (_gate)
        {
            return (_lastPopulation, _populationCapturedAt);
        }
    }

    public (int? Value, DateTimeOffset? CapturedAt) PeekGold()
    {
        lock (_gate)
        {
            return (_lastGold, _goldCapturedAt);
        }
    }

    public (int WishesResponded, int LettersObtained, int LettersOpened, bool MiracleSelected, int? MiracleAtHealth,
        bool CauldronSelected, bool FiveBondGivenUp, bool NewBondMemberAvailable) PeekEventState()
    {
        lock (_gate)
        {
            return (_wishesResponded, _lettersObtained, _lettersOpened, _miracleCompensationSelected,
                _miracleCompensationSelectedAtHealth, _infiniteCauldronSelected, _fiveBondGivenUp,
                _newBondMemberAvailable);
        }
    }
}

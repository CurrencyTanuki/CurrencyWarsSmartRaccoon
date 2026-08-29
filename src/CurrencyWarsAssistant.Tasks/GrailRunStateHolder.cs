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
    private bool _refreshSurcharge;

    private int? _lastHealth;
    private DateTimeOffset? _healthCapturedAt;
    private int? _lastPopulation;
    private DateTimeOffset? _populationCapturedAt;
    private int? _lastGold;
    private DateTimeOffset? _goldCapturedAt;

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

            // 令咒决议·回路过载/行为限制的诅咒代价 = 商店刷新价格 +1 金（用户 2026-08-29 确认）
            if (GrailFuzzyText.ContainsFuzzy(selected, GrailTrialResponseDecider.OverloadKeyword)
                || GrailFuzzyText.ContainsFuzzy(selected, GrailTrialResponseDecider.RestrictKeyword))
            {
                _refreshSurcharge = true;
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

    /// <summary>令咒决议诅咒是否已使商店刷新价格 +1（回路过载/行为限制选中后为真，保守全程生效）。</summary>
    public bool PeekRefreshSurcharge()
    {
        lock (_gate)
        {
            return _refreshSurcharge;
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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace CurrencyWarsAssistant.App;

public enum AppCombinationItemKind
{
    InvestmentEnvironment,
    Competitor,
    EnemyAffix
}

public sealed record CombinationRuleOption(
    AppCombinationItemKind Kind,
    string Id,
    string Name)
{
    public string CategoryName => Kind switch
    {
        AppCombinationItemKind.InvestmentEnvironment => "投资环境",
        AppCombinationItemKind.Competitor => "敌人阵营",
        AppCombinationItemKind.EnemyAffix => "敌人负面词条",
        _ => ""
    };

    public string Label => $"{CategoryName} · {Name}";
}

public enum AppFilterSelectionMode
{
    Unrestricted,
    Required,
    Forbidden
}

public sealed record FilterModeOption(
    AppFilterSelectionMode Value,
    string DisplayName);

public static class FilterModeOptions
{
    public static IReadOnlyList<FilterModeOption> All { get; } =
    [
        new(AppFilterSelectionMode.Unrestricted, "不限制"),
        new(AppFilterSelectionMode.Required, "必须出现"),
        new(AppFilterSelectionMode.Forbidden, "必须不出现")
    ];

    public static IReadOnlyList<FilterModeOption> CombinationConditions => All;
}

public sealed class FilterItemViewModel : ObservableObject
{
    private AppFilterSelectionMode _selectionMode;

    public FilterItemViewModel(
        string id,
        string name,
        string description,
        bool supportsForbidden,
        bool isSelectable = true,
        string availabilityLabel = "")
    {
        Id = id;
        Name = name;
        Description = description;
        SupportsForbidden = supportsForbidden;
        IsSelectable = isSelectable;
        AvailabilityLabel = availabilityLabel;
        CycleSelectionCommand = new RelayCommand(
            CycleSelection,
            () => IsSelectable);
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool SupportsForbidden { get; }
    public bool IsSelectable { get; }
    public string AvailabilityLabel { get; }
    public bool HasAvailabilityLabel =>
        !string.IsNullOrWhiteSpace(AvailabilityLabel);
    public RelayCommand CycleSelectionCommand { get; }

    public AppFilterSelectionMode SelectionMode
    {
        get => _selectionMode;
        set
        {
            if (value == AppFilterSelectionMode.Forbidden &&
                !SupportsForbidden)
            {
                value = AppFilterSelectionMode.Unrestricted;
            }

            if (SetProperty(ref _selectionMode, value))
            {
                OnPropertyChanged(nameof(SelectionLabel));
            }
        }
    }

    public string SelectionLabel => SelectionMode switch
    {
        AppFilterSelectionMode.Required =>
            SupportsForbidden ? "必出" : "想要",
        AppFilterSelectionMode.Forbidden => "刷掉",
        _ => ""
    };

    private void CycleSelection()
    {
        SelectionMode = SelectionMode switch
        {
            AppFilterSelectionMode.Unrestricted =>
                AppFilterSelectionMode.Required,
            AppFilterSelectionMode.Required when SupportsForbidden =>
                AppFilterSelectionMode.Forbidden,
            _ => AppFilterSelectionMode.Unrestricted
        };
    }
}

public sealed class InvestmentStrategyRarityViewModel : ObservableObject
{
    private string _searchText = "";

    public InvestmentStrategyRarityViewModel(
        string rarity,
        IEnumerable<FilterItemViewModel> selectableItems,
        IEnumerable<FilterItemViewModel> unavailableItems)
    {
        Rarity = rarity;
        SelectableItems =
            new ObservableCollection<FilterItemViewModel>(selectableItems);
        UnavailableItems =
            new ObservableCollection<FilterItemViewModel>(unavailableItems);
        SelectableView =
            CollectionViewSource.GetDefaultView(SelectableItems);
        UnavailableView =
            CollectionViewSource.GetDefaultView(UnavailableItems);
        SelectableView.Filter = MatchesSearch;
        UnavailableView.Filter = MatchesSearch;
    }

    public string Rarity { get; }
    public ObservableCollection<FilterItemViewModel> SelectableItems { get; }
    public ObservableCollection<FilterItemViewModel> UnavailableItems { get; }
    public ICollectionView SelectableView { get; }
    public ICollectionView UnavailableView { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                SelectableView.Refresh();
                UnavailableView.Refresh();
            }
        }
    }

    private bool MatchesSearch(object value)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var item = (FilterItemViewModel)value;
        return item.Name.Contains(
                   SearchText,
                   StringComparison.OrdinalIgnoreCase) ||
               item.Description.Contains(
                   SearchText,
                   StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FilterGroupViewModel : ObservableObject
{
    private string _searchText = "";

    public FilterGroupViewModel(IEnumerable<FilterItemViewModel> items)
    {
        Items = new ObservableCollection<FilterItemViewModel>(items);
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = MatchesSearch;
    }

    public ObservableCollection<FilterItemViewModel> Items { get; }
    public ICollectionView View { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                View.Refresh();
            }
        }
    }

    private bool MatchesSearch(object value)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var item = (FilterItemViewModel)value;
        return item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               item.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CharacterAutomationItemViewModel : ObservableObject
{
    private bool _isRetained;
    private bool _isAutoPurchased;

    public CharacterAutomationItemViewModel(
        string id,
        string name,
        string position,
        IReadOnlyList<int> costs,
        IReadOnlyList<string> bonds,
        bool isRetained)
    {
        Id = id;
        Name = name;
        Position = position;
        Costs = costs;
        Bonds = bonds;
        _isRetained = isRetained;
    }

    public string Id { get; }
    public string Name { get; }
    public string Position { get; }
    public IReadOnlyList<int> Costs { get; }
    public IReadOnlyList<string> Bonds { get; }
    public string CostLabel => string.Join("/", Costs.Order());
    public string BondsLabel => string.Join(" · ", Bonds);
    public string SearchText =>
        $"{Name} {Position} {CostLabel} {BondsLabel}";

    public bool IsRetained
    {
        get => _isRetained;
        set => SetProperty(ref _isRetained, value);
    }

    public bool IsAutoPurchased
    {
        get => _isAutoPurchased;
        set => SetProperty(ref _isAutoPurchased, value);
    }
}

public sealed class CharacterAutomationGroupViewModel : ObservableObject
{
    private string _searchText = "";

    public CharacterAutomationGroupViewModel(
        IEnumerable<CharacterAutomationItemViewModel> items)
    {
        Items = new ObservableCollection<CharacterAutomationItemViewModel>(
            items);
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = MatchesSearch;
    }

    public ObservableCollection<CharacterAutomationItemViewModel> Items
    {
        get;
    }

    public ICollectionView View { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                View.Refresh();
            }
        }
    }

    private bool MatchesSearch(object value)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var item = (CharacterAutomationItemViewModel)value;
        return item.SearchText.Contains(
            SearchText,
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RerollProfileViewModel : ObservableObject
{
    private bool _isEnabled;

    public RerollProfileViewModel(
        string id,
        string name,
        bool isEnabled,
        IReadOnlyList<string> acceptedInvestmentEnvironmentIds,
        IReadOnlyList<string> acceptedInvestmentEnvironmentNames,
        IReadOnlyList<string> preferredInvestmentStrategyIds,
        IReadOnlyList<string> preferredInvestmentStrategyNames,
        IReadOnlyList<string> requiredCompetitorIds,
        IReadOnlyList<string> requiredCompetitorNames,
        IReadOnlyList<string> rejectedCompetitorIds,
        IReadOnlyList<string> rejectedCompetitorNames,
        IReadOnlyList<string> requiredEnemyAffixIds,
        IReadOnlyList<string> requiredEnemyAffixNames,
        IReadOnlyList<string> rejectedEnemyAffixIds,
        IReadOnlyList<string> rejectedEnemyAffixNames)
    {
        Id = id;
        Name = name;
        _isEnabled = isEnabled;
        AcceptedInvestmentEnvironmentIds =
            acceptedInvestmentEnvironmentIds;
        AcceptedInvestmentEnvironmentNames =
            acceptedInvestmentEnvironmentNames;
        PreferredInvestmentStrategyIds =
            preferredInvestmentStrategyIds;
        PreferredInvestmentStrategyNames =
            preferredInvestmentStrategyNames;
        RequiredCompetitorIds = requiredCompetitorIds;
        RequiredCompetitorNames = requiredCompetitorNames;
        RejectedCompetitorIds = rejectedCompetitorIds;
        RejectedCompetitorNames = rejectedCompetitorNames;
        RequiredEnemyAffixIds = requiredEnemyAffixIds;
        RequiredEnemyAffixNames = requiredEnemyAffixNames;
        RejectedEnemyAffixIds = rejectedEnemyAffixIds;
        RejectedEnemyAffixNames = rejectedEnemyAffixNames;
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> AcceptedInvestmentEnvironmentIds { get; }
    public IReadOnlyList<string> AcceptedInvestmentEnvironmentNames { get; }
    public IReadOnlyList<string> PreferredInvestmentStrategyIds { get; }
    public IReadOnlyList<string> PreferredInvestmentStrategyNames { get; }
    public IReadOnlyList<string> RequiredCompetitorIds { get; }
    public IReadOnlyList<string> RequiredCompetitorNames { get; }
    public IReadOnlyList<string> RejectedCompetitorIds { get; }
    public IReadOnlyList<string> RejectedCompetitorNames { get; }
    public IReadOnlyList<string> RequiredEnemyAffixIds { get; }
    public IReadOnlyList<string> RequiredEnemyAffixNames { get; }
    public IReadOnlyList<string> RejectedEnemyAffixIds { get; }
    public IReadOnlyList<string> RejectedEnemyAffixNames { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string InvestmentEnvironmentSummary =>
        Summary(AcceptedInvestmentEnvironmentNames, "任意投资环境");
    public string InvestmentStrategySummary =>
        Summary(PreferredInvestmentStrategyNames, "任意投资策略");
    public string RequiredSummary =>
        Summary(
            [.. RequiredCompetitorNames, .. RequiredEnemyAffixNames],
            "无额外必出项");
    public string RejectedSummary =>
        Summary(
            [.. RejectedCompetitorNames, .. RejectedEnemyAffixNames],
            "无额外排除项");

    private static string Summary(
        IReadOnlyList<string> values,
        string emptyText) =>
        values.Count == 0
            ? emptyText
            : string.Join(" 或 ", values);
}

public sealed class RerollProfileEditorViewModel
{
    private readonly bool _existingIsEnabled;

    public RerollProfileEditorViewModel(
        CurrencyWarsAssistant.Game.GameDataCatalog gameData,
        RerollProfileViewModel? existing = null)
    {
        ExistingId = existing?.Id;
        _existingIsEnabled = existing?.IsEnabled ?? true;
        Name = existing?.Name ?? $"刷取方案 {DateTime.Now:HHmm}";
        InvestmentEnvironments = new FilterGroupViewModel(
            gameData.InvestmentEnvironments.Select(item =>
                new FilterItemViewModel(
                    item.Id,
                    item.Name,
                    item.Effect,
                    supportsForbidden: false)));
        InvestmentStrategies = new FilterGroupViewModel(
            gameData.InvestmentStrategies
                .Where(item => item.AvailablePlanes.Contains(1))
                .OrderByDescending(item =>
                    CurrencyWarsAssistant.Game
                        .InvestmentStrategyVersionCatalog
                        .GetNewestFirstRank(item.Id))
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(item => new FilterItemViewModel(
                    item.Id,
                    $"{item.Name} · {item.Rarity}",
                    $"{item.Rarity} · {item.Effect}",
                    supportsForbidden: false)));
        Competitors = new FilterGroupViewModel(
            gameData.Competitors.Select(item =>
                new FilterItemViewModel(
                    item.Id,
                    item.Name,
                    "竞争对手阵营",
                    supportsForbidden: true)));
        EnemyAffixes = new FilterGroupViewModel(
            gameData.EnemyAffixes.Select(item =>
                new FilterItemViewModel(
                    item.Id,
                    item.Name,
                    $"T{item.Tier} · {item.Effect}",
                    supportsForbidden: true)));
        if (existing is not null)
        {
            Apply(existing.AcceptedInvestmentEnvironmentIds,
                InvestmentEnvironments.Items,
                AppFilterSelectionMode.Required);
            Apply(existing.PreferredInvestmentStrategyIds,
                InvestmentStrategies.Items,
                AppFilterSelectionMode.Required);
            Apply(existing.RequiredCompetitorIds,
                Competitors.Items,
                AppFilterSelectionMode.Required);
            Apply(existing.RejectedCompetitorIds,
                Competitors.Items,
                AppFilterSelectionMode.Forbidden);
            Apply(existing.RequiredEnemyAffixIds,
                EnemyAffixes.Items,
                AppFilterSelectionMode.Required);
            Apply(existing.RejectedEnemyAffixIds,
                EnemyAffixes.Items,
                AppFilterSelectionMode.Forbidden);
        }
    }

    public string? ExistingId { get; }
    public string Name { get; set; }
    public FilterGroupViewModel InvestmentEnvironments { get; }
    public FilterGroupViewModel InvestmentStrategies { get; }
    public FilterGroupViewModel Competitors { get; }
    public FilterGroupViewModel EnemyAffixes { get; }

    public RerollProfileViewModel Build()
    {
        var name = Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("方案名称不能为空。");
        }

        var environmentRequired = Required(InvestmentEnvironments.Items);
        var strategyRequired = Required(InvestmentStrategies.Items);
        var competitorRequired = Required(Competitors.Items);
        var competitorRejected = Rejected(Competitors.Items);
        var affixRequired = Required(EnemyAffixes.Items);
        var affixRejected = Rejected(EnemyAffixes.Items);
        if (environmentRequired.Count == 0 &&
            strategyRequired.Count == 0 &&
            competitorRequired.Count == 0 &&
            competitorRejected.Count == 0 &&
            affixRequired.Count == 0 &&
            affixRejected.Count == 0)
        {
            throw new InvalidOperationException(
                "刷取方案至少需要选择一个条件。");
        }

        return new RerollProfileViewModel(
            ExistingId ?? $"profile_{Guid.NewGuid():N}",
            name,
            _existingIsEnabled,
            Ids(environmentRequired),
            Names(environmentRequired),
            Ids(strategyRequired),
            Names(strategyRequired),
            Ids(competitorRequired),
            Names(competitorRequired),
            Ids(competitorRejected),
            Names(competitorRejected),
            Ids(affixRequired),
            Names(affixRequired),
            Ids(affixRejected),
            Names(affixRejected));
    }

    private static void Apply(
        IReadOnlyList<string> ids,
        IEnumerable<FilterItemViewModel> items,
        AppFilterSelectionMode mode)
    {
        var selected = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => selected.Contains(item.Id)))
        {
            item.SelectionMode = mode;
        }
    }

    private static IReadOnlyList<FilterItemViewModel> Required(
        IEnumerable<FilterItemViewModel> items) =>
        items.Where(item =>
            item.SelectionMode == AppFilterSelectionMode.Required).ToArray();

    private static IReadOnlyList<FilterItemViewModel> Rejected(
        IEnumerable<FilterItemViewModel> items) =>
        items.Where(item =>
            item.SelectionMode == AppFilterSelectionMode.Forbidden).ToArray();

    private static IReadOnlyList<string> Ids(
        IEnumerable<FilterItemViewModel> items) =>
        items.Select(item => item.Id).ToArray();

    private static IReadOnlyList<string> Names(
        IEnumerable<FilterItemViewModel> items) =>
        items.Select(item => item.Name).ToArray();
}

public sealed class SpecialCombinationViewModel : ObservableObject
{
    private AppFilterSelectionMode _condition;
    private AppFilterSelectionMode _lastEnabledCondition;

    public SpecialCombinationViewModel(
        string name,
        AppFilterSelectionMode condition,
        string investmentEnvironments,
        string competitors,
        string enemyAffixes,
        bool isBuiltIn,
        string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? isBuiltIn
                ? "death_dragon_plus_extra_strike"
                : $"custom_{Guid.NewGuid():N}"
            : id;
        Name = name;
        _condition = condition;
        _lastEnabledCondition = condition == AppFilterSelectionMode.Unrestricted
            ? AppFilterSelectionMode.Forbidden
            : condition;
        InvestmentEnvironments = investmentEnvironments;
        Competitors = competitors;
        EnemyAffixes = enemyAffixes;
        IsBuiltIn = isBuiltIn;
        CycleConditionCommand = new RelayCommand(CycleCondition);
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<FilterModeOption> ModeOptions => FilterModeOptions.All;
    public AppFilterSelectionMode Condition
    {
        get => _condition;
        set
        {
            if (SetProperty(ref _condition, value))
            {
                if (value != AppFilterSelectionMode.Unrestricted)
                {
                    _lastEnabledCondition = value;
                }
                OnPropertyChanged(nameof(ConditionLabel));
                OnPropertyChanged(nameof(IsRuleEnabled));
                OnPropertyChanged(nameof(RuleStateLabel));
            }
        }
    }
    public bool IsRuleEnabled
    {
        get => Condition != AppFilterSelectionMode.Unrestricted;
        set => Condition = value
            ? _lastEnabledCondition
            : AppFilterSelectionMode.Unrestricted;
    }
    public RelayCommand CycleConditionCommand { get; }
    public string ConditionLabel => Condition switch
    {
        AppFilterSelectionMode.Required => "命中则保留",
        AppFilterSelectionMode.Forbidden => "命中则排除",
        _ => "未启用"
    };
    public string RuleStateLabel => Condition switch
    {
        AppFilterSelectionMode.Required => "已启用 · 同时出现则保留",
        AppFilterSelectionMode.Forbidden => "已启用 · 同时出现则刷掉",
        _ => "已关闭"
    };
    public string InvestmentEnvironments { get; }
    public string Competitors { get; }
    public string EnemyAffixes { get; }
    public bool IsBuiltIn { get; }
    public string SourceDisplayName => IsBuiltIn ? "内置规则" : "自定义规则";

    private void CycleCondition()
    {
        IsRuleEnabled = !IsRuleEnabled;
    }
}

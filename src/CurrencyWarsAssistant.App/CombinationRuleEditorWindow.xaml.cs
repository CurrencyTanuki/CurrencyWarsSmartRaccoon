using System.Windows;

namespace CurrencyWarsAssistant.App;

public partial class CombinationRuleEditorWindow : Window
{
    private readonly MainViewModel _viewModel;

    public CombinationRuleEditorWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        FirstCondition.ItemsSource = viewModel.CombinationRuleOptions;
        SecondCondition.ItemsSource = viewModel.CombinationRuleOptions;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (FirstCondition.SelectedItem is not CombinationRuleOption first ||
            SecondCondition.SelectedItem is not CombinationRuleOption second)
        {
            ValidationMessage.Text = "请选择条件 A 和条件 B。";
            return;
        }

        if (!_viewModel.AddProhibitedCombination(first, second))
        {
            ValidationMessage.Text = "两个条件不能选择同一项。";
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

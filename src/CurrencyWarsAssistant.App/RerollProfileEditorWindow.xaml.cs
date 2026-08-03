using System.Windows;

namespace CurrencyWarsAssistant.App;

public partial class RerollProfileEditorWindow : Window
{
    private readonly RerollProfileEditorViewModel _viewModel;

    public RerollProfileEditorWindow(
        RerollProfileEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public RerollProfileViewModel? Result { get; private set; }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = _viewModel.Build();
            DialogResult = true;
        }
        catch (InvalidOperationException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

using System.Windows;

namespace CurrencyWarsAssistant.App;

public partial class StartupNoticeWindow : Window
{
    public StartupNoticeWindow(CommunityContactOptions contacts)
    {
        InitializeComponent();
        DataContext = contacts;
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        // 声明窗口当前以非模态方式显示（App.xaml.cs 使用 Show()）。
        // 对非模态窗口设置 DialogResult 会抛 InvalidOperationException，
        // 因此统一用 Close() 关闭（对 ShowDialog 模态窗口同样生效）。
        Close();
    }
}

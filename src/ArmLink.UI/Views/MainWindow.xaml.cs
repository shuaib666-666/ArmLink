using ArmLink.UI.ViewModels;
using System.Windows;

namespace ArmLink.UI.Views;

/// <summary>
/// 主視窗。
/// <para>Code-behind 刻意維持在最小狀態：只有 InitializeComponent 與 DataContext 指派。
/// 所有邏輯都在 <see cref="MainViewModel"/>，所有樣式與互動都在 XAML，這裡一行商業邏輯都沒有。</para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>建立主視窗。</summary>
    /// <param name="viewModel">由 App 注入的 ViewModel。</param>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

using System.Windows;
using ArmLink.Core.Abstractions;
using ArmLink.Core.Communication;
using ArmLink.Core.Simulation;
using ArmLink.UI.Services;
using ArmLink.UI.ViewModels;
using ArmLink.UI.Views;

namespace ArmLink.UI;

/// <summary>
/// 應用程式的進入點。這裡負責「組裝」所有物件（Composition Root）：
/// View 不知道 Model，Model 不知道 View，兩邊只在中間這一層被黏起來。
/// </summary>
public partial class App : Application
{
    private MainViewModel? _viewModel;

    /// <summary>建立並顯示主視窗。</summary>
    /// <param name="e">啟動參數。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. 基礎設施：UI 緒列排程器
        var dispatcher = new WpfDispatcher(Current.Dispatcher);

        // 2. 服務層：虛擬機台（也可以換成真實機台的 IP）
        IRobotServer mockServer = new MockRobotServer(MockRobotServerOptions.LocalDefault);

        // 3. 客戶端工廠：ViewModel 在 IP/Port 變更時可以重建連線物件，卻不必認識 TcpRobotClient
        IRobotClient ClientFactory(RobotClientOptions options) => new TcpRobotClient(options);

        // 4. ViewModel + View
        _viewModel = new MainViewModel(ClientFactory, mockServer, dispatcher);

        var window = new MainWindow(_viewModel);
        MainWindow = window;

        window.Loaded += async (_, _) =>
        {
            if (_viewModel is not null)
            {
                await _viewModel.InitializeAsync();
            }
        };

        window.Closed += async (_, _) =>
        {
            if (_viewModel is not null)
            {
                await _viewModel.DisposeAsync().ConfigureAwait(false);
            }
        };

        window.Show();
    }
}

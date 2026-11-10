using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ArmLink.Core.Abstractions;
using ArmLink.Core.Communication;
using ArmLink.Core.Exceptions;
using ArmLink.Core.Models;
using ArmLink.Core.Protocol;
using ArmLink.Core.Simulation;
using ArmLink.UI.Services;

namespace ArmLink.UI.ViewModels;

/// <summary>
/// 主控台的主要 ViewModel。
/// <para>設計原則：</para>
/// <list type="bullet">
///   <item><description>所有對 UI 的寫入都透過 <see cref="IUiDispatcher"/> 回到 UI 緒列，因為事件是從背景緒列來的。</description></item>
///   <item><description>商業邏輯（協定、連線、模擬）全部在 Core，這裡只做「把狀態翻成人看的樣子」。</description></item>
///   <item><description>不直接相依 <see cref="System.Windows.Window"/>，因此邏輯層可以單元測試。</description></item>
/// </list>
/// </summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogLines = 400;

    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x43, 0xA0, 0x47));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xFF, 0xB3, 0x00));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xE5, 0x39, 0x35));

    private readonly Func<RobotClientOptions, IRobotClient> _clientFactory;
    private readonly IRobotServer _mockServer;
    private readonly IUiDispatcher _dispatcher;

    private IRobotClient _client;
    private RobotClientOptions _clientOptions = RobotClientOptions.LocalDefault;
    private ClientConnectionState _connectionState = ClientConnectionState.Disconnected;
    private MachineState _machineState = MachineState.Unknown;

    /// <summary>建立 ViewModel。</summary>
    /// <param name="clientFactory">客戶端工廠（讓 IP/Port 變更後可以重建連線物件）。</param>
    /// <param name="mockServer">內建的虛擬機台服務。</param>
    /// <param name="dispatcher">UI 緒列排程器。</param>
    public MainViewModel(Func<RobotClientOptions, IRobotClient> clientFactory, IRobotServer mockServer, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(mockServer);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _clientFactory = clientFactory;
        _mockServer = mockServer;
        _dispatcher = dispatcher;

        _client = clientFactory(_clientOptions);
        Subscribe(_client);
    }

    // ---------------- 繫結用屬性 ----------------

    /// <summary>日誌來源。</summary>
    public ObservableCollection<LogLine> Logs { get; } = new();

    /// <summary>Jog 步長選項（mm）。</summary>
    public double[] StepOptions { get; } = { 1, 10, 50 };

    /// <summary>目前選擇的步長（mm）。</summary>
    [ObservableProperty]
    private double _selectedStep = 10;

    /// <summary>目標 IP。</summary>
    [ObservableProperty]
    private string _host = "127.0.0.1";

    /// <summary>目標埠號。</summary>
    [ObservableProperty]
    private int _port = 9000;

    /// <summary>是否已連線（影響所有操作類命令的 CanExecute）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(JogCommand))]
    [NotifyCanExecuteChangedFor(nameof(HomeCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmergencyStopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool _isConnected;

    /// <summary>連線區的狀態文字。</summary>
    [ObservableProperty]
    private string _connectionText = "未連線";

    /// <summary>狀態指示燈的顏色。</summary>
    [ObservableProperty]
    private Brush _connectionBrush = RedBrush;

    /// <summary>機台狀態的中文描述。</summary>
    [ObservableProperty]
    private string _machineStateText = "未知";

    /// <summary>目前 X 座標。</summary>
    [ObservableProperty]
    private double _currentX;

    /// <summary>目前 Y 座標。</summary>
    [ObservableProperty]
    private double _currentY;

    /// <summary>目前 Z 座標。</summary>
    [ObservableProperty]
    private double _currentZ;

    /// <summary>目前設定速度（mm/s）。</summary>
    [ObservableProperty]
    private double _currentSpeed;

    /// <summary>作業模式。</summary>
    [ObservableProperty]
    private string _modeText = "-";

    /// <summary>最後一次被機台拒絕的原因。</summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    /// <summary>內建模擬器的狀態說明。</summary>
    [ObservableProperty]
    private string _mockServerText = "虛擬機台尚未啟動";

    /// <summary>已送出的封包數。</summary>
    [ObservableProperty]
    private int _transmitCount;

    /// <summary>已收到的封包數。</summary>
    [ObservableProperty]
    private int _receiveCount;

    /// <summary>收到的壞封包數（這條對展示「穩健性」特別有用）。</summary>
    [ObservableProperty]
    private int _malformedCount;

    /// <summary>是否自動捲動日誌。</summary>
    [ObservableProperty]
    private bool _isAutoScroll = true;

    // ---------------- 啟用流程 ----------------

    /// <summary>啟動內建模擬器並自動連線（由 App 在視窗載入後呼叫）。</summary>
    public async Task InitializeAsync()
    {
        await _mockServer.StartAsync().ConfigureAwait(false);
        var port = _mockServer.Port;

        await _dispatcher.InvokeAsync(() =>
        {
            Port = port;
            MockServerText = $"內建模擬器已啟動：{Host}:{port}";
        }).ConfigureAwait(false);

        AppendLog("SYS", $"虛擬機台監聽於 {Host}:{port}");

        // 自動連線一次；若失敗也只是寫日誌，之後會由自動重連機制持續嘗試。
        await TryConnectAsync().ConfigureAwait(false);
    }

    // ---------------- 命令 ----------------

    private bool CanConnect => !IsConnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync() => await TryConnectAsync();

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        await _client.DisconnectAsync().ConfigureAwait(false);
        AppendLog("SYS", "已中斷連線");
    }

    /// <summary>X/Y/Z 軸點動，參數格式為「X+」「X-」「Y+」…。</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task JogAsync(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter) || parameter.Length != 2)
        {
            return;
        }

        var axis = char.ToUpperInvariant(parameter[0]) switch
        {
            'X' => Axis.X,
            'Y' => Axis.Y,
            'Z' => Axis.Z,
            _ => (Axis?)null,
        };

        if (axis is null)
        {
            return;
        }

        var step = parameter[1] == '+' ? SelectedStep : -SelectedStep;
        await ExecuteAsync(RobotCommand.JogAxis(axis.Value, step), $"Jog {axis} {step:+#;-#;0} mm");
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task HomeAsync() => await ExecuteAsync(RobotCommand.Home(), "回歸原點");

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task EmergencyStopAsync()
    {
        // 急停講求時效：不等 ACK、不排隊等待，直接丟出去。
        try
        {
            await _client.SendCommandAsync(RobotCommand.EmergencyStop()).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => ErrorText = "已送出急停").ConfigureAwait(false);
        }
        catch (RobotException ex)
        {
            ReportError(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ResetAsync() => await ExecuteAsync(RobotCommand.Reset(), "急停復歸");

    [RelayCommand]
    private Task ClearLogsAsync() => _dispatcher.InvokeAsync(() =>
    {
        Logs.Clear();
        AppendLog("SYS", "日誌已清除");
    });

    // ---------------- Core 事件訂閱 ----------------

    private void Subscribe(IRobotClient client)
    {
        client.StatusReceived += OnStatusReceived;
        client.ConnectionStateChanged += OnConnectionStateChanged;
        client.LogEmitted += OnLogEmitted;
        client.MalformedPacketReceived += OnMalformedPacketReceived;
    }

    private void Unsubscribe(IRobotClient client)
    {
        client.StatusReceived -= OnStatusReceived;
        client.ConnectionStateChanged -= OnConnectionStateChanged;
        client.LogEmitted -= OnLogEmitted;
        client.MalformedPacketReceived -= OnMalformedPacketReceived;
    }

    private void OnStatusReceived(object? sender, StatusReceivedEventArgs e)
    {
        var status = e.Status;

        // 背景緒列 → UI 緒列，這是「跨緒列更新 UI」唯一的合法途徑。
        _dispatcher.Post(() =>
        {
            _machineState = status.State;
            CurrentX = status.Pose.X;
            CurrentY = status.Pose.Y;
            CurrentZ = status.Pose.Z;
            CurrentSpeed = status.Speed;
            ModeText = Translate(status.Mode);
            MachineStateText = Translate(status.State);
            ReceiveCount++;

            if (status.Error != ErrorCode.None)
            {
                ErrorText = $"{RobotProtocol.ToToken(status.Error)}：{Translate(status.Error)}";
            }

            UpdateLamp();
        });
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            _connectionState = e.State;
            var connected = e.State == ClientConnectionState.Connected;

            IsConnected = connected;
            ConnectionText = e.State switch
            {
                ClientConnectionState.Connected => "已連線",
                ClientConnectionState.Connecting => "連線中…",
                ClientConnectionState.Reconnecting => "重新連線中…",
                _ => "未連線",
            };

            if (!connected)
            {
                _machineState = MachineState.Unknown;
                AppendLog("SYS", string.IsNullOrWhiteSpace(e.Reason)
                    ? "連線已中斷"
                    : $"連線中斷：{e.Reason}");
            }

            UpdateLamp();
        });
    }

    private void OnLogEmitted(object? sender, RobotLogEventArgs e)
    {
        if (e.Direction == LogDirection.Transmit)
        {
            _dispatcher.Post(() => TransmitCount++);
        }

        AppendLog(e.Prefix, e.Line);
    }

    private void OnMalformedPacketReceived(object? sender, MalformedPacketEventArgs e)
    {
        _dispatcher.Post(() => MalformedCount++);
        AppendLog("BAD", $"{e.Reason}：{e.RawPacket}");
    }

    // ---------------- 內部邏輯 ----------------

    private async Task<bool> TryConnectAsync()
    {
        try
        {
            await EnsureClientMatchesEndpointAsync().ConfigureAwait(false);
            await _client.ConnectAsync().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => ErrorText = string.Empty).ConfigureAwait(false);
            return true;
        }
        catch (RobotException ex)
        {
            ReportError(ex);
            return false;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return false;
        }
    }

    private async Task ExecuteAsync(RobotCommand command, string description)
    {
        try
        {
            await _client.ExecuteAsync(command).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => ErrorText = string.Empty).ConfigureAwait(false);
        }
        catch (CommandRejectedException ex)
        {
            await _dispatcher.InvokeAsync(() =>
                ErrorText = $"{description} 被拒絕：{RobotProtocol.ToToken(ex.ErrorCode)} — {ex.Message}").ConfigureAwait(false);
        }
        catch (RobotException ex)
        {
            ReportError(ex);
        }
    }

    private void ReportError(Exception exception)
    {
        var message = exception.Message;
        _dispatcher.Post(() =>
        {
            ErrorText = message;
            AppendLog("ERR", message);
        });
    }

    /// <summary>若使用者修改了 IP / Port，就地重建一個新的連線物件（連線中則先斷線）。</summary>
    private async Task EnsureClientMatchesEndpointAsync()
    {
        // Host / Port 只會在 UI 緒列被寫入，這裡只是讀值，直接讀即可（最差是晚一幀生效）。
        var host = Host;
        var port = Port;

        if (string.Equals(_clientOptions.Host, host, StringComparison.Ordinal) && _clientOptions.Port == port)
        {
            return;
        }

        await _client.DisconnectAsync().ConfigureAwait(false);
        Unsubscribe(_client);
        await _client.DisposeAsync().ConfigureAwait(false);

        _clientOptions = RobotClientOptions.Create(host, port);
        _client = _clientFactory(_clientOptions);
        Subscribe(_client);
    }

    private void UpdateLamp()
    {
        ConnectionBrush = _connectionState switch
        {
            ClientConnectionState.Connected => _machineState switch
            {
                MachineState.Moving => AmberBrush,      // 黃色：移動中
                MachineState.Error => RedBrush,         // 紅色：錯誤
                MachineState.EmergencyStop => RedBrush, // 紅色：急停
                MachineState.Booting => AmberBrush,
                _ => GreenBrush,                        // 綠色：正常待命
            },
            ClientConnectionState.Disconnected => RedBrush, // 紅色：斷線
            _ => AmberBrush,                                // 黃色：連線 / 重連中
        };
    }

    private void AppendLog(string direction, string content)
    {
        var line = new LogLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), direction, content);

        _dispatcher.Post(() =>
        {
            Logs.Add(line);
            while (Logs.Count > MaxLogLines)
            {
                Logs.RemoveAt(0);
            }
        });
    }

    private static string Translate(RobotMode mode) => mode switch
    {
        RobotMode.Manual => "手動 Manual",
        RobotMode.Auto => "自動 Auto",
        _ => "未知",
    };

    private static string Translate(MachineState state) => state switch
    {
        MachineState.Ready => "就緒 Ready",
        MachineState.Moving => "移動中 Moving",
        MachineState.Error => "錯誤 Error",
        MachineState.EmergencyStop => "急停 E-Stop",
        MachineState.Booting => "開機中 Booting",
        _ => "未知 Unknown",
    };

    private static string Translate(ErrorCode code) => code switch
    {
        ErrorCode.None => "無",
        ErrorCode.UnknownCommand => "無法識別的指令",
        ErrorCode.MalformedPacket => "封包格式錯誤",
        ErrorCode.InvalidArgument => "參數錯誤",
        ErrorCode.CoordinateOutOfRange => "座標超出軟體極限",
        ErrorCode.SpeedOutOfRange => "速度超出允許範圍",
        ErrorCode.EmergencyStopActive => "急停尚未復歸",
        ErrorCode.MachineBusy => "機台忙碌中",
        ErrorCode.MachineNotReady => "機台尚未就緒",
        ErrorCode.InternalError => "內部錯誤",
        _ => "未知錯誤",
    };

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Unsubscribe(_client);
        await _client.DisposeAsync().ConfigureAwait(false);
        await _mockServer.StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

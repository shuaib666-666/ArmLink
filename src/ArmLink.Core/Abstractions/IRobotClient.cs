using ArmLink.Core.Models;
using ArmLink.Core.Protocol;

namespace ArmLink.Core.Abstractions;

/// <summary>
/// 機械手臂客戶端的抽象介面（TCP 連線 + 協定 + 生命週期管理）。
/// UI 與測試都只依賴這個介面，因此可以隨意抽換成模擬器、真實機台，甚至是錄製回放的假連線。
/// </summary>
public interface IRobotClient : IAsyncDisposable
{
    /// <summary>目前是否在連線狀態。</summary>
    bool IsConnected { get; }

    /// <summary>目前連線狀態。</summary>
    ClientConnectionState State { get; }

    /// <summary>最近一次的狀態快照；尚未收到任何廣播時為 <c>null</c>。</summary>
    RobotStatusSnapshot? LatestStatus { get; }

    /// <summary>收到一筆合法的狀態廣播（Server 每 100ms 送一次）。</summary>
    event EventHandler<StatusReceivedEventArgs>? StatusReceived;

    /// <summary>連線狀態變化（含自動重連的過程）。</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>TX / RX / 系統日誌。</summary>
    event EventHandler<RobotLogEventArgs>? LogEmitted;

    /// <summary>收到壞封包（解析失敗、長度超標、含控制字元等）。</summary>
    event EventHandler<MalformedPacketEventArgs>? MalformedPacketReceived;

    /// <summary>
    /// 建立連線。若失敗會拋出 <see cref="Exceptions.RobotCommunicationException"/>；
    /// 成功後會自動在背景啟動讀寫與自動重連機制。
    /// </summary>
    /// <param name="cancellationToken">取消權杖。</param>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>中斷連線並停止自動重連。</summary>
    Task DisconnectAsync();

    /// <summary>
    /// 送出指令但不等待 ACK（適用於 E-Stop 這種講求時效的指令）。
    /// </summary>
    /// <param name="command">指令。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    Task SendCommandAsync(RobotCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// 送出指令並等待機台的 OK / ERR 回應（使用預設逾時）。
    /// </summary>
    /// <param name="command">指令。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>機台的回應。</returns>
    Task<RobotResponse> ExecuteAsync(RobotCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// 送出指令並等待機台的 OK / ERR 回應。
    /// </summary>
    /// <param name="command">指令。</param>
    /// <param name="timeout">等待 ACK 的逾時時間。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>機台的回應。</returns>
    Task<RobotResponse> ExecuteAsync(RobotCommand command, TimeSpan timeout, CancellationToken cancellationToken = default);
}

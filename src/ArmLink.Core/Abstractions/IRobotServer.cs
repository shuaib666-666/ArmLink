namespace ArmLink.Core.Abstractions;

/// <summary>
/// 虛擬機台服務的抽象介面。
/// 抽這個介面的目的：讓「跑真的 TCP Server」與「程序內直接對接」可以互換，測試時就不必開真 Port。
/// </summary>
public interface IRobotServer : IAsyncDisposable
{
    /// <summary>是否在監聽中。</summary>
    bool IsRunning { get; }

    /// <summary>實際監聽的埠號（若使用 Port 0 由系統分配，這裡會回傳真實埠號）。</summary>
    int Port { get; }

    /// <summary>目前連線數。</summary>
    int ClientCount { get; }

    /// <summary>啟動監聽與狀態廣播。</summary>
    /// <param name="cancellationToken">取消權杖。</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>停止監聽並中斷所有客戶端。</summary>
    /// <param name="cancellationToken">取消權杖，僅用於「等待」階段的逾時控制。</param>
    Task StopAsync(CancellationToken cancellationToken = default);
}

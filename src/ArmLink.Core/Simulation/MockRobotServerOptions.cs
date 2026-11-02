using ArmLink.Core.Models;

namespace ArmLink.Core.Simulation;

/// <summary>
/// 虛擬機台服務的設定值。
/// </summary>
public sealed class MockRobotServerOptions
{
    /// <summary>監聽的 IP。<c>127.0.0.1</c> 或 <c>0.0.0.0</c>。</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>監聽埠號；設為 0 會由作業系統自動分配（單元測試很愛用）。</summary>
    public int Port { get; init; } = 9000;

    /// <summary>狀態廣播週期，規格要求 100ms。</summary>
    public TimeSpan BroadcastInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>運動插值的運算週期，比廣播更細，讓軌跡平滑。</summary>
    public TimeSpan MotionInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>模擬真實機台收到指令後的處理延遲（排程 + 加減速運算）。</summary>
    public TimeSpan CommandProcessDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>初始速度（mm/s）。</summary>
    public double DefaultSpeed { get; init; } = 120;

    /// <summary>最大允許速度（mm/s）。</summary>
    public double MaxSpeed { get; init; } = 600;

    /// <summary>軟體行程極限。</summary>
    public RobotLimits Limits { get; init; } = RobotLimits.Default;

    /// <summary>
    /// 故障注入：每處理一筆客戶端封包，有機率（0~1）先丟一條「壞封包」出去，
    /// 用來驗證客戶端在髒線路上也不會崩潰。預設 0（關閉）。
    /// </summary>
    public double MalformedPacketRate { get; init; }

    /// <summary>故障注入：每筆指令有機率（0~1）直接回 ERR，用來驗證逾時與重試邏輯。預設 0（關閉）。</summary>
    public double AckFailureRate { get; init; }

    /// <summary>本機預設設定（Port 9000）。</summary>
    public static MockRobotServerOptions LocalDefault { get; } = new();
}

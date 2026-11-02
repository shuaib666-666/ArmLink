using ArmLink.Core.Models;

namespace ArmLink.Core.Communication;

/// <summary>
/// TCP 客戶端的設定值（不可變，方便在建構時一次傳入）。
/// </summary>
public sealed class RobotClientOptions
{
    /// <summary>機台 IP 或主機名稱。</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>機台埠號。</summary>
    public int Port { get; init; } = 9000;

    /// <summary>單次連線建立的逾時時間。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>等待 ACK 的預設逾時時間。</summary>
    public TimeSpan DefaultCommandTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>是否啟用自動重連。</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>重連策略。</summary>
    public ReconnectPolicy Reconnect { get; init; } = ReconnectPolicy.Default;

    /// <summary>接收緩衝區大小（位元組）。</summary>
    public int ReceiveBufferSize { get; init; } = 1024;

    /// <summary>連到本地虛擬機台的預設設定。</summary>
    public static RobotClientOptions LocalDefault { get; } = new();

    /// <summary>快速建立一組設定。</summary>
    /// <param name="host">IP 或主機名稱。</param>
    /// <param name="port">埠號。</param>
    public static RobotClientOptions Create(string host, int port) => new() { Host = host, Port = port };
}

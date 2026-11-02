using ArmLink.Core.Models;

namespace ArmLink.Core.Abstractions;

/// <summary>通訊日誌的方向。</summary>
public enum LogDirection
{
    /// <summary>TX：客戶端送出去。</summary>
    Transmit = 0,

    /// <summary>RX：客戶端收到。</summary>
    Receive = 1,

    /// <summary>系統訊息（連線狀態變化等）。</summary>
    System = 2,
}

/// <summary>
/// 收到一筆合法的狀態廣播。
/// </summary>
public sealed class StatusReceivedEventArgs : EventArgs
{
    /// <summary>建立事件參數。</summary>
    /// <param name="status">狀態快照。</param>
    public StatusReceivedEventArgs(RobotStatusSnapshot status) => Status = status;

    /// <summary>狀態快照。</summary>
    public RobotStatusSnapshot Status { get; }
}

/// <summary>
/// 連線狀態發生變化（含自動重連的每一次狀態切換）。
/// </summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>建立事件參數。</summary>
    /// <param name="state">新的連線狀態。</param>
    /// <param name="reason">補充說明，例如例外訊息。</param>
    public ConnectionStateChangedEventArgs(ClientConnectionState state, string? reason)
    {
        State = state;
        Reason = reason;
    }

    /// <summary>新的連線狀態。</summary>
    public ClientConnectionState State { get; }

    /// <summary>補充說明。</summary>
    public string? Reason { get; }
}

/// <summary>
/// 一筆 TX / RX / 系統日誌。
/// </summary>
public sealed class RobotLogEventArgs : EventArgs
{
    /// <summary>建立事件參數（時間戳自動取現在）。</summary>
    /// <param name="direction">方向。</param>
    /// <param name="line">封包或訊息內容。</param>
    public RobotLogEventArgs(LogDirection direction, string line)
        : this(direction, line, DateTimeOffset.Now)
    {
    }

    /// <summary>建立事件參數。</summary>
    /// <param name="direction">方向。</param>
    /// <param name="line">封包或訊息內容。</param>
    /// <param name="timestamp">時間戳。</param>
    public RobotLogEventArgs(LogDirection direction, string line, DateTimeOffset timestamp)
    {
        Direction = direction;
        Line = line;
        Timestamp = timestamp;
    }

    /// <summary>方向。</summary>
    public LogDirection Direction { get; }

    /// <summary>內容。</summary>
    public string Line { get; }

    /// <summary>時間戳。</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>格式化後的單行日誌。</summary>
    public string Prefix => Direction switch
    {
        LogDirection.Transmit => "TX",
        LogDirection.Receive => "RX",
        _ => "SYS",
    };

    /// <inheritdoc/>
    public override string ToString() => $"[{Timestamp:HH:mm:ss.fff}] {Prefix} {Line}";
}

/// <summary>
/// 收到一條無法解析的壞封包。這個事件的存在是刻意的：
/// 「髒資料」應該被看見（寫進日誌），而不是被悄悄吞掉，更不能讓程式崩潰。
/// </summary>
public sealed class MalformedPacketEventArgs : EventArgs
{
    /// <summary>建立事件參數。</summary>
    /// <param name="rawPacket">原始封包內容。</param>
    /// <param name="reason">無法解析的原因。</param>
    public MalformedPacketEventArgs(string rawPacket, string reason)
    {
        RawPacket = rawPacket;
        Reason = reason;
    }

    /// <summary>原始封包內容。</summary>
    public string RawPacket { get; }

    /// <summary>無法解析的原因。</summary>
    public string Reason { get; }
}

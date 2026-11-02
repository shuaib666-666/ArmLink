namespace ArmLink.Core.Abstractions;

/// <summary>
/// 客戶端的連線生命週期狀態。與機台本身的 <see cref="Models.MachineState"/> 是不同的東西：
/// 這邊描述的是「通訊通道」，那邊描述的是「手臂」。
/// </summary>
public enum ClientConnectionState
{
    /// <summary>未連線。</summary>
    Disconnected = 0,

    /// <summary>正在建立第一次連線。</summary>
    Connecting = 1,

    /// <summary>連線正常。</summary>
    Connected = 2,

    /// <summary>斷線後正在依照退避策略重新連線。</summary>
    Reconnecting = 3,
}

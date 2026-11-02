namespace ArmLink.Core.Protocol;

/// <summary>
/// 指令類型，對應協定裡的關鍵字。
/// </summary>
public enum CommandType
{
    /// <summary>移動到絕對座標：<c>MOVE:X,Y,Z</c>。</summary>
    Move = 0,

    /// <summary>相對點動：<c>JOG:dX,dY,dZ</c>。</summary>
    Jog = 1,

    /// <summary>回歸原點：<c>HOME</c>。</summary>
    Home = 2,

    /// <summary>緊急停止：<c>ESTOP</c>。</summary>
    EmergencyStop = 3,

    /// <summary>錯誤復歸：<c>RESET</c>。</summary>
    Reset = 4,

    /// <summary>設定速度：<c>SPEED:mm/s</c>。</summary>
    SetSpeed = 5,

    /// <summary>設定模式：<c>MODE:AUTO|MANUAL</c>。</summary>
    SetMode = 6,

    /// <summary>心跳測試：<c>PING</c>。</summary>
    Ping = 7,

    /// <summary>
    /// 協定層專用：代表「歸不了類的指令」。
    /// 當 Server 收到連關鍵字都無法識別的壞封包時，沒辦法把 ERR 對應到任何真實指令，就用這個值。
    /// 客戶端不會主動送出它（<see cref="RobotProtocol.EncodeCommand"/> 會拒絕它）。
    /// </summary>
    Unknown = 99,
}

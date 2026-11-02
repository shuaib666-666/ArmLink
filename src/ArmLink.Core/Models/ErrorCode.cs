namespace ArmLink.Core.Models;

/// <summary>
/// 機台回傳的錯誤代碼。<c>0</c> 代表沒有錯誤。
/// 設計原則：所有錯誤都是「可恢復」的，客戶端收到後不會因為例外而崩潰。
/// </summary>
public enum ErrorCode
{
    /// <summary>沒有錯誤。</summary>
    None = 0,

    /// <summary>無法識別的指令。</summary>
    UnknownCommand = 100,

    /// <summary>封包格式錯誤。</summary>
    MalformedPacket = 101,

    /// <summary>參數數量或格式錯誤。</summary>
    InvalidArgument = 102,

    /// <summary>目標座標超出軟體極限。</summary>
    CoordinateOutOfRange = 200,

    /// <summary>速度設定超出允許範圍。</summary>
    SpeedOutOfRange = 201,

    /// <summary>急停尚未解除，拒絕運動指令。</summary>
    EmergencyStopActive = 300,

    /// <summary>機台忙碌中。</summary>
    MachineBusy = 301,

    /// <summary>機台尚未就緒。</summary>
    MachineNotReady = 302,

    /// <summary>未預期的內部錯誤。</summary>
    InternalError = 900,
}

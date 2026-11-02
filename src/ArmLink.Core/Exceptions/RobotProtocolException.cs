namespace ArmLink.Core.Exceptions;

/// <summary>
/// 封包格式錯誤（無法解析的字串、長度超限、包含控制字元等）。
/// 這是「壞資料」的第一道防線，永遠不應該讓應用崩潰。
/// </summary>
public sealed class RobotProtocolException : RobotException
{
    /// <summary>建立例外。</summary>
    /// <param name="message">錯誤訊息。</param>
    public RobotProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>建立例外並攜帶原始封包內容，方便寫入日誌。</summary>
    /// <param name="message">錯誤訊息。</param>
    /// <param name="rawPacket">造成錯誤的原始字串。</param>
    public RobotProtocolException(string message, string rawPacket)
        : base(message)
    {
        RawPacket = rawPacket;
    }

    /// <summary>造成錯誤的原始封包。</summary>
    public string? RawPacket { get; }
}

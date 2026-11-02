namespace ArmLink.Core.Exceptions;

/// <summary>
/// 傳輸層錯誤：連線失敗、連線中斷、Socket 讀寫失敗。
/// </summary>
public sealed class RobotCommunicationException : RobotException
{
    /// <summary>建立例外。</summary>
    /// <param name="message">錯誤訊息。</param>
    public RobotCommunicationException(string message)
        : base(message)
    {
    }

    /// <summary>建立例外並攜帶原始例外。</summary>
    /// <param name="message">錯誤訊息。</param>
    /// <param name="innerException">原始 Socket 例外。</param>
    public RobotCommunicationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

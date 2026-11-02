namespace ArmLink.Core.Exceptions;

/// <summary>
/// 等待機台回應逾時。
/// </summary>
public sealed class RobotTimeoutException : RobotException
{
    /// <summary>建立例外。</summary>
    /// <param name="message">錯誤訊息。</param>
    public RobotTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>建立例外並記錄逾時長度。</summary>
    /// <param name="message">錯誤訊息。</param>
    /// <param name="timeout">當初設定的逾時時間。</param>
    public RobotTimeoutException(string message, TimeSpan timeout)
        : base(message)
    {
        Timeout = timeout;
    }

    /// <summary>當初設定的逾時時間。</summary>
    public TimeSpan Timeout { get; }
}

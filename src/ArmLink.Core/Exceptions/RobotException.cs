namespace ArmLink.Core.Exceptions;

/// <summary>
/// ArmLink 所有例外型別的根類別。
/// 目的：UI 層只要 <c>catch (RobotException)</c> 就能處理所有已知通訊問題，不會漏接也不會誤吞系統例外。
/// </summary>
public class RobotException : Exception
{
    /// <summary>建立例外。</summary>
    public RobotException()
        : base("機器人通訊發生未預期的錯誤。")
    {
    }

    /// <summary>以指定訊息建立例外。</summary>
    /// <param name="message">錯誤訊息。</param>
    public RobotException(string message)
        : base(message)
    {
    }

    /// <summary>以指定訊息與內部例外建立例外。</summary>
    /// <param name="message">錯誤訊息。</param>
    /// <param name="innerException">原始例外。</param>
    public RobotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

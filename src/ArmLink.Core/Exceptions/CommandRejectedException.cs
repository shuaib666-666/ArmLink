using ArmLink.Core.Models;
using ArmLink.Core.Protocol;

namespace ArmLink.Core.Exceptions;

/// <summary>
/// 機台明確拒絕了指令（回傳 ERR 封包）。
/// </summary>
public sealed class CommandRejectedException : RobotException
{
    /// <summary>建立例外。</summary>
    /// <param name="command">被拒絕的指令類型。</param>
    /// <param name="errorCode">機台回傳的錯誤代碼。</param>
    /// <param name="message">機台回傳的錯誤訊息。</param>
    public CommandRejectedException(CommandType command, ErrorCode errorCode, string message)
        : base(message)
    {
        Command = command;
        ErrorCode = errorCode;
    }

    /// <summary>被拒絕的指令類型。</summary>
    public CommandType Command { get; }

    /// <summary>機台回傳的錯誤代碼。</summary>
    public ErrorCode ErrorCode { get; }
}

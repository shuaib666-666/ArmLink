using ArmLink.Core.Models;

namespace ArmLink.Core.Protocol;

/// <summary>
/// 機台對單一指令的回應（OK / ERR）。
/// </summary>
public sealed record RobotResponse
{
    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; init; }

    /// <summary>對應的指令類型。</summary>
    public CommandType Command { get; init; }

    /// <summary>失敗時的錯誤代碼。</summary>
    public ErrorCode Error { get; init; } = ErrorCode.None;

    /// <summary>附加訊息。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>建立成功回應。</summary>
    public static RobotResponse Ok(CommandType command, string message = "") => new()
    {
        IsSuccess = true,
        Command = command,
        Error = ErrorCode.None,
        Message = message,
    };

    /// <summary>建立失敗回應。</summary>
    public static RobotResponse Fail(CommandType command, ErrorCode error, string message) => new()
    {
        IsSuccess = false,
        Command = command,
        Error = error,
        Message = message,
    };

    /// <inheritdoc/>
    public override string ToString() => IsSuccess
        ? $"OK:{Command}|{Message}"
        : $"ERR:{Command}|{Error}|{Message}";
}

using ArmLink.Core.Models;

namespace ArmLink.Core.Protocol;

/// <summary>
/// 一條送給機台的指令（不可變）。
/// 刻意採用「靜態工廠方法」而非公開建構子：每種指令該帶哪些參數由工廠保證，呼叫端不可能組出矛盾的指令。
/// </summary>
public sealed class RobotCommand
{
    private RobotCommand(CommandType type, RobotPose pose, double? speed, RobotMode? mode)
    {
        Type = type;
        Pose = pose;
        Speed = speed;
        Mode = mode;
    }

    /// <summary>指令類型。</summary>
    public CommandType Type { get; }

    /// <summary>
    /// 座標參數。<see cref="CommandType.Move"/> 代表絕對目標；<see cref="CommandType.Jog"/> 代表相對位移；其餘指令無意義。
    /// </summary>
    public RobotPose Pose { get; }

    /// <summary>速度參數，僅 <see cref="CommandType.SetSpeed"/> 使用。</summary>
    public double? Speed { get; }

    /// <summary>模式參數，僅 <see cref="CommandType.SetMode"/> 使用。</summary>
    public RobotMode? Mode { get; }

    /// <summary>移動到絕對座標。</summary>
    public static RobotCommand Move(RobotPose target) => new(CommandType.Move, target, null, null);

    /// <summary>以相對量點動（同時也能一次三軸）。</summary>
    public static RobotCommand Jog(RobotPose delta) => new(CommandType.Jog, delta, null, null);

    /// <summary>單軸點動的便利方法，UI 的 +X / -Z 按鈕就是用這個。</summary>
    public static RobotCommand JogAxis(Axis axis, double distance)
    {
        var delta = axis switch
        {
            Axis.X => new RobotPose(distance, 0, 0),
            Axis.Y => new RobotPose(0, distance, 0),
            Axis.Z => new RobotPose(0, 0, distance),
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "未知的軸別"),
        };

        return Jog(delta);
    }

    /// <summary>回歸原點。</summary>
    public static RobotCommand Home() => new(CommandType.Home, RobotPose.Origin, null, null);

    /// <summary>緊急停止。</summary>
    public static RobotCommand EmergencyStop() => new(CommandType.EmergencyStop, RobotPose.Origin, null, null);

    /// <summary>錯誤復歸。</summary>
    public static RobotCommand Reset() => new(CommandType.Reset, RobotPose.Origin, null, null);

    /// <summary>設定移動速度（mm/s）。</summary>
    public static RobotCommand SetSpeed(double millimetersPerSecond) => new(CommandType.SetSpeed, RobotPose.Origin, millimetersPerSecond, null);

    /// <summary>切換作業模式。</summary>
    public static RobotCommand SetMode(RobotMode mode) => new(CommandType.SetMode, RobotPose.Origin, null, mode);

    /// <summary>心跳測試。</summary>
    public static RobotCommand Ping() => new(CommandType.Ping, RobotPose.Origin, null, null);

    /// <summary>序列化成協定字串（不含換行符）。</summary>
    public string ToWireLine() => RobotProtocol.EncodeCommand(this);

    /// <inheritdoc/>
    public override string ToString() => ToWireLine();
}

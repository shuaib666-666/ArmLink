using ArmLink.Core.Models;
using ArmLink.Core.Protocol;

namespace ArmLink.Core.Simulation;

/// <summary>
/// 虛擬手臂的「狀態機 + 運動學」，完全不含任何 Socket 程式碼。
/// <para>刻意這樣切的好處：</para>
/// <list type="bullet">
///   <item><description>可以在單元測試裡直接 new 起來跑，不需要開 Port、不需要等非同步。</description></item>
///   <item><description>運動邏輯與網路層解耦，Mock 的 Server 只負責搬封包。</description></item>
/// </list>
/// 所有公開成員都以 lock 保護，因此可以安全地從多個連緒（per-client 讀取緒 + 廣播緒）同時呼叫。
/// </summary>
public sealed class VirtualRobotMachine
{
    private readonly object _sync = new();
    private readonly RobotLimits _limits;
    private readonly double _maxSpeed;

    private RobotPose _pose = RobotPose.Origin;
    private RobotPose _target = RobotPose.Origin;
    private double _speed;
    private RobotMode _mode = RobotMode.Manual;
    private MachineState _state = MachineState.Ready;
    private ErrorCode _error = ErrorCode.None;
    private long _sequence;

    /// <summary>建立虛擬手臂。</summary>
    /// <param name="limits">軟體行程極限。</param>
    /// <param name="defaultSpeed">初始速度（mm/s）。</param>
    /// <param name="maxSpeed">允許的最大速度（mm/s）。</param>
    public VirtualRobotMachine(RobotLimits? limits = null, double defaultSpeed = 120, double maxSpeed = 600)
    {
        _limits = limits ?? RobotLimits.Default;
        _maxSpeed = maxSpeed > 0 ? maxSpeed : 600;
        _speed = Math.Clamp(defaultSpeed, 0.1, _maxSpeed);
    }

    /// <summary>目前座標。</summary>
    public RobotPose CurrentPose
    {
        get
        {
            lock (_sync)
            {
                return _pose;
            }
        }
    }

    /// <summary>目前的作業模式。</summary>
    public RobotMode Mode
    {
        get
        {
            lock (_sync)
            {
                return _mode;
            }
        }
    }

    /// <summary>目前的機台狀態。</summary>
    public MachineState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>執行一條指令並回傳 ACK / ERR。純邏輯、無 IO、無延遲。</summary>
    /// <param name="command">要執行的指令。</param>
    public RobotResponse Execute(RobotCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_sync)
        {
            switch (command.Type)
            {
                case CommandType.Move:
                    return CommitMove(command.Pose, CommandType.Move);

                case CommandType.Jog:
                    if (_state == MachineState.EmergencyStop)
                    {
                        return Reject(CommandType.Jog, ErrorCode.EmergencyStopActive, "急停尚未復歸，請先解除。");
                    }

                    if (_mode == RobotMode.Auto)
                    {
                        return Reject(CommandType.Jog, ErrorCode.MachineNotReady, "自動模式下禁止手動點動。");
                    }

                    // Jog 是相對量：目標 = 現在的位置 + 位移
                    return CommitMove(_pose.Translate(command.Pose), CommandType.Jog);

                case CommandType.Home:
                    if (_state == MachineState.EmergencyStop)
                    {
                        return Reject(CommandType.Home, ErrorCode.EmergencyStopActive, "急停尚未復歸，請先解除。");
                    }

                    return CommitMove(_limits.Clamp(RobotPose.Origin), CommandType.Home);

                case CommandType.EmergencyStop:
                    _target = _pose;                            // 運動目標原地凍結
                    _state = MachineState.EmergencyStop;
                    _error = ErrorCode.EmergencyStopActive;
                    return RobotResponse.Ok(CommandType.EmergencyStop, "已觸發急停，所有運動凍結");

                case CommandType.Reset:
                    _error = ErrorCode.None;
                    _state = MachineState.Ready;
                    return RobotResponse.Ok(CommandType.Reset, "已復歸，機台就緒");

                case CommandType.SetSpeed:
                    var requested = command.Speed ?? 0;
                    if (!double.IsFinite(requested) || requested <= 0 || requested > _maxSpeed)
                    {
                        return Reject(CommandType.SetSpeed, ErrorCode.SpeedOutOfRange, $"速度必須介於 0 與 {_maxSpeed:F0} mm/s 之間。");
                    }

                    _speed = requested;
                    return RobotResponse.Ok(CommandType.SetSpeed, $"速度設為 {_speed:F1} mm/s");

                case CommandType.SetMode:
                    _mode = command.Mode ?? RobotMode.Manual;
                    return RobotResponse.Ok(CommandType.SetMode, $"模式切換為 {_mode}");

                case CommandType.Ping:
                    return RobotResponse.Ok(CommandType.Ping, "pong");

                default:
                    return Reject(CommandType.Unknown, ErrorCode.UnknownCommand, "不支援的指令。");
            }
        }
    }

    /// <summary>推進一段時間的運動插值。呼叫頻率越高，軌跡越平滑。</summary>
    /// <param name="elapsed">距離上次推進所經過的時間。</param>
    public void Advance(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        lock (_sync)
        {
            if (_state != MachineState.Moving)
            {
                return;
            }

            var step = _speed * elapsed.TotalSeconds;
            _pose = _pose.MoveTowards(_target, step);

            if (_pose.DistanceTo(_target) <= 1e-6)
            {
                _pose = _target;
                _state = MachineState.Ready;
            }
        }
    }

    /// <summary>取得目前狀態快照（每次呼叫都會遞增封包序號）。</summary>
    public RobotStatusSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new RobotStatusSnapshot(_pose, _mode, _state, _error, _speed, ++_sequence, DateTimeOffset.UtcNow);
        }
    }

    private RobotResponse CommitMove(RobotPose target, CommandType command)
    {
        if (_state == MachineState.EmergencyStop)
        {
            return Reject(command, ErrorCode.EmergencyStopActive, "急停尚未復歸，請先解除。");
        }

        if (!target.IsFinite())
        {
            return Reject(command, ErrorCode.InvalidArgument, "座標包含 NaN 或無限值。");
        }

        if (!_limits.TryValidate(target, out var violatedAxis))
        {
            var detail = violatedAxis.HasValue
                ? $"{violatedAxis} 軸超出軟體極限 {_limits.GetRange(violatedAxis.Value)}"
                : "座標超出軟體極限";
            return Reject(command, ErrorCode.CoordinateOutOfRange, detail);
        }

        _target = target;
        _error = ErrorCode.None;
        var arrived = _pose.DistanceTo(_target) <= 1e-6;
        _state = arrived ? MachineState.Ready : MachineState.Moving;

        return RobotResponse.Ok(command, arrived ? "已在目標位置" : "accepted");
    }

    private static RobotResponse Reject(CommandType command, ErrorCode code, string message)
        => RobotResponse.Fail(command, code, message);
}

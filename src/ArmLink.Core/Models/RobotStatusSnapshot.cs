namespace ArmLink.Core.Models;

/// <summary>
/// 機台的即時快照，也就是 Server 每 100ms 廣播一次的狀態封包內容。
/// 設計成不可變 <c>record</c>，因此可以安全地跨執行緒傳遞給 UI。
/// </summary>
/// <param name="Pose">當前座標。</param>
/// <param name="Mode">作業模式。</param>
/// <param name="State">機台狀態。</param>
/// <param name="Error">錯誤代碼。</param>
/// <param name="Speed">當前設定速度（mm/s）。</param>
/// <param name="Sequence">封包序號，可用來偵測丟包或順序錯亂。</param>
/// <param name="Timestamp">伺服器端產生封包的時間戳。</param>
public sealed record RobotStatusSnapshot(
    RobotPose Pose,
    RobotMode Mode,
    MachineState State,
    ErrorCode Error,
    double Speed = 0,
    long Sequence = 0,
    DateTimeOffset Timestamp = default);

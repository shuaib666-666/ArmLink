namespace ArmLink.Core.Models;

/// <summary>
/// 軸別識別，用於 <see cref="RobotLimits"/> 與 Jog 控制。
/// </summary>
public enum Axis
{
    /// <summary>X 軸（左右）。</summary>
    X = 0,

    /// <summary>Y 軸（前後）。</summary>
    Y = 1,

    /// <summary>Z 軸（上下）。</summary>
    Z = 2,
}

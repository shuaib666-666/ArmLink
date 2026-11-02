namespace ArmLink.Core.Models;

/// <summary>
/// 軟體行程極限（Soft Limit）。與硬體極限不同，它跑在 CPU 裡，是防止工程師手滑的第一道防線。
/// </summary>
public sealed record RobotLimits
{
    /// <summary>X 軸行程。</summary>
    public AxisRange X { get; init; } = new AxisRange(-500, 500);

    /// <summary>Y 軸行程。</summary>
    public AxisRange Y { get; init; } = new AxisRange(-500, 500);

    /// <summary>Z 軸行程（通常不能低於工作檯面）。</summary>
    public AxisRange Z { get; init; } = new AxisRange(0, 600);

    /// <summary>預設行程：XY ±500mm，Z 0~600mm。</summary>
    public static RobotLimits Default { get; } = new();

    /// <summary>取得指定軸的行程區間。</summary>
    public AxisRange GetRange(Axis axis) => axis switch
    {
        Axis.X => X,
        Axis.Y => Y,
        Axis.Z => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "未知的軸別"),
    };

    /// <summary>座標是否完全落在行程內。</summary>
    public bool Contains(RobotPose pose) => X.Contains(pose.X) && Y.Contains(pose.Y) && Z.Contains(pose.Z);

    /// <summary>把座標強制壓回行程內（用於 Clamp 策略而非拒絕策略）。</summary>
    public RobotPose Clamp(RobotPose pose) => new(X.Clamp(pose.X), Y.Clamp(pose.Y), Z.Clamp(pose.Z));

    /// <summary>
    /// 驗證座標是否合法，並回報第一個越界的軸。
    /// </summary>
    /// <param name="pose">待驗證座標。</param>
    /// <param name="violatedAxis">越界的軸；若座標合法則為 <c>null</c>。</param>
    /// <returns>合法回傳 <c>true</c>，否則 <c>false</c>。</returns>
    public bool TryValidate(RobotPose pose, out Axis? violatedAxis)
    {
        violatedAxis = null;

        if (!pose.IsFinite())
        {
            return false;
        }

        if (!X.Contains(pose.X))
        {
            violatedAxis = Axis.X;
            return false;
        }

        if (!Y.Contains(pose.Y))
        {
            violatedAxis = Axis.Y;
            return false;
        }

        if (!Z.Contains(pose.Z))
        {
            violatedAxis = Axis.Z;
            return false;
        }

        return true;
    }
}

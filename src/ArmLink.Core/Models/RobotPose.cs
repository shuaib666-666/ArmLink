using System.Globalization;

namespace ArmLink.Core.Models;

/// <summary>
/// 三軸笛卡兒座標，單位為毫米（mm）。
/// 設計為 <c>readonly record struct</c>：值語意、不可變、自帶相等比較，適合當成座標這種「值」。
/// </summary>
/// <param name="X">X 軸座標。</param>
/// <param name="Y">Y 軸座標。</param>
/// <param name="Z">Z 軸座標。</param>
public readonly record struct RobotPose(double X, double Y, double Z)
{
    /// <summary>原點。</summary>
    public static RobotPose Origin { get; } = new(0, 0, 0);

    /// <summary>判斷「已抵達」的容差，避免 double 比較的精度問題。</summary>
    private const double Epsilon = 1e-6;

    /// <summary>與另一點的歐幾里得距離（mm）。</summary>
    public double DistanceTo(RobotPose other)
    {
        var dx = other.X - X;
        var dy = other.Y - Y;
        var dz = other.Z - Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>疊加一段位移量。</summary>
    public RobotPose Translate(RobotPose offset) => new(X + offset.X, Y + offset.Y, Z + offset.Z);

    /// <summary>
    /// 朝目標直線逼近一步，單步距離上限為 <paramref name="maxDistance"/>。
    /// 這是模擬器插值運動的核心：每次 Tick 前進一小步，肉眼就能看到手臂「慢慢飛過去」。
    /// </summary>
    public RobotPose MoveTowards(RobotPose target, double maxDistance)
    {
        if (!IsFinite() || !target.IsFinite() || maxDistance <= 0)
        {
            return this;
        }

        var distance = DistanceTo(target);
        if (distance <= Epsilon || distance <= maxDistance)
        {
            return target;
        }

        var ratio = maxDistance / distance;
        return Translate(new RobotPose((target.X - X) * ratio, (target.Y - Y) * ratio, (target.Z - Z) * ratio));
    }

    /// <summary>三個分量是否皆為有限數（排除 NaN / Infinity），壞資料進不來。</summary>
    public bool IsFinite() => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:F3}, {Y:F3}, {Z:F3})");
}

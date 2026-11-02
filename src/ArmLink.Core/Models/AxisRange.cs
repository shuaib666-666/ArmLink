using System.Globalization;

namespace ArmLink.Core.Models;

/// <summary>
/// 單軸行程區間，含上下邊界。
/// </summary>
/// <param name="Min">行程下限（mm）。</param>
/// <param name="Max">行程上限（mm）。</param>
public readonly record struct AxisRange(double Min, double Max)
{
    /// <summary>區間本身是否合法（Min ≤ Max 且不是 NaN）。</summary>
    public bool IsValid => !double.IsNaN(Min) && !double.IsNaN(Max) && !double.IsInfinity(Min) && !double.IsInfinity(Max) && Min <= Max;

    /// <summary>數值是否落在區間內，NaN 一律視為不合格。</summary>
    public bool Contains(double value)
    {
        if (!double.IsFinite(value))
        {
            return false;
        }

        var (lower, upper) = Min <= Max ? (Min, Max) : (Max, Min);
        return value >= lower && value <= upper;
    }

    /// <summary>把數值壓回區間內，NaN 會被視為下限（安全的降級行為）。</summary>
    public double Clamp(double value)
    {
        if (!double.IsFinite(value))
        {
            return Min <= Max ? Min : Max;
        }

        var (lower, upper) = Min <= Max ? (Min, Max) : (Max, Min);
        return value < lower ? lower : value > upper ? upper : value;
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"[{Min:F3}, {Max:F3}]");
}

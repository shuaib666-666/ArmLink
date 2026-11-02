using ArmLink.Core.Models;

namespace ArmLink.Tests.Models;

/// <summary>
/// 軟體行程極限（Soft Limit）測試：越界座標必須被擋下來，不能靠手臂撞機台才發現。
/// </summary>
public class RobotLimitsTests
{
    [Fact]
    public void DefaultLimits_AcceptOrigin()
    {
        Assert.True(RobotLimits.Default.TryValidate(RobotPose.Origin, out var violated));
        Assert.Null(violated);
    }

    [Fact]
    public void BoundaryValues_AreInclusive()
    {
        var limits = RobotLimits.Default;

        Assert.True(limits.TryValidate(new RobotPose(500, 500, 600), out _)); // 上限含邊界
        Assert.True(limits.TryValidate(new RobotPose(-500, -500, 0), out _)); // 下限含邊界
    }

    [Theory]
    [InlineData(500.001, 0, 0, Axis.X)]
    [InlineData(-500.001, 0, 0, Axis.X)]
    [InlineData(0, 500.001, 0, Axis.Y)]
    [InlineData(0, 0, 600.001, Axis.Z)]
    [InlineData(0, 0, -0.001, Axis.Z)] // Z 不能低於檯面
    public void OutOfRangeCoordinates_ReportViolatedAxis(double x, double y, double z, Axis expected)
    {
        var ok = RobotLimits.Default.TryValidate(new RobotPose(x, y, z), out var violated);

        Assert.False(ok);
        Assert.Equal(expected, violated);
    }

    [Fact]
    public void NonFiniteCoordinates_AreRejectedWithoutAxis()
    {
        Assert.False(RobotLimits.Default.TryValidate(new RobotPose(double.NaN, 0, 0), out var violated));
        Assert.Null(violated);

        Assert.False(RobotLimits.Default.TryValidate(new RobotPose(0, double.PositiveInfinity, 0), out violated));
        Assert.Null(violated);
    }

    [Fact]
    public void Clamp_PullsCoordinatesBackIntoRange()
    {
        var clamped = RobotLimits.Default.Clamp(new RobotPose(9999, -9999, -50));

        Assert.Equal(500, clamped.X);
        Assert.Equal(-500, clamped.Y);
        Assert.Equal(0, clamped.Z);
    }

    [Fact]
    public void CustomLimits_AreRespected()
    {
        var limits = new RobotLimits
        {
            X = new AxisRange(-10, 10),
            Y = new AxisRange(-10, 10),
            Z = new AxisRange(0, 20),
        };

        Assert.True(limits.TryValidate(new RobotPose(10, 10, 20), out _));
        Assert.False(limits.TryValidate(new RobotPose(10.1, 0, 0), out var violated));
        Assert.Equal(Axis.X, violated);
    }

    [Fact]
    public void InvertedRange_StillBehavesCorrectly()
    {
        // 就算被寫反了也不能讓判斷失靈：內部會自動轉成正確順序。
        var range = new AxisRange(100, -100);

        Assert.True(range.Contains(0));
        Assert.False(range.Contains(200));
        Assert.Equal(100, range.Clamp(500));
    }
}

using ArmLink.Core.Exceptions;
using ArmLink.Core.Models;
using ArmLink.Core.Protocol;
using ArmLink.Core.Simulation;

namespace ArmLink.Tests.Simulation;

/// <summary>
/// 虛擬手臂狀態機測試：急停、復歸、模式限制、軟體極限、運動插值。
/// 這一層完全沒有 IO，所以跑得又快又穩。
/// </summary>
public class VirtualRobotMachineTests
{
    [Fact]
    public void Move_ToValidTarget_StartsMoving()
    {
        var machine = new VirtualRobotMachine();

        var response = machine.Execute(RobotCommand.Move(new RobotPose(100, 0, 0)));

        Assert.True(response.IsSuccess);
        Assert.Equal(MachineState.Moving, machine.State);
        Assert.Equal(CommandType.Move, response.Command);
    }

    [Fact]
    public void Advance_MovesTowardsTarget_AndStopsWhenArrived()
    {
        var machine = new VirtualRobotMachine(defaultSpeed: 100);

        machine.Execute(RobotCommand.Move(new RobotPose(100, 0, 0)));

        // 100 mm/s × 0.5 s = 50 mm，還沒到
        machine.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(50, machine.CurrentPose.X, 3);
        Assert.Equal(MachineState.Moving, machine.State);

        // 再 1 秒一定到（且不會衝過頭）
        machine.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(100, machine.CurrentPose.X, 6);
        Assert.Equal(MachineState.Ready, machine.State);
    }

    [Fact]
    public void Move_OutOfSoftwareLimit_IsRejected()
    {
        var machine = new VirtualRobotMachine();

        var response = machine.Execute(RobotCommand.Move(new RobotPose(9999, 0, 0)));

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCode.CoordinateOutOfRange, response.Error);
        Assert.Equal(MachineState.Ready, machine.State);
        Assert.Equal(RobotPose.Origin, machine.CurrentPose); // 位置完全沒動
    }

    [Fact]
    public void Move_WithNonFiniteCoordinate_IsRejected()
    {
        var machine = new VirtualRobotMachine();

        var response = machine.Execute(RobotCommand.Move(new RobotPose(double.NaN, 0, 0)));

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCode.InvalidArgument, response.Error);
    }

    [Fact]
    public void Jog_AppliesRelativeOffset_WhenEachMoveCompletes()
    {
        var machine = new VirtualRobotMachine(defaultSpeed: 1000);

        machine.Execute(RobotCommand.JogAxis(Axis.X, 10));
        machine.Advance(TimeSpan.FromSeconds(1));

        machine.Execute(RobotCommand.JogAxis(Axis.Y, -5));
        machine.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(10, machine.CurrentPose.X, 3);
        Assert.Equal(-5, machine.CurrentPose.Y, 3);
    }

    [Fact]
    public void Jog_DuringMotion_RetargetsFromCurrentPosition()
    {
        // 這是刻意的行為：Jog 永遠以「目前的實際位置」重算目標，
        // 因此連續 Jog 會中斷前一次沒走完的行程（與多數實際控制器的互動手感一致）。
        var machine = new VirtualRobotMachine(defaultSpeed: 100);

        machine.Execute(RobotCommand.JogAxis(Axis.X, 100));
        machine.Advance(TimeSpan.FromMilliseconds(500)); // 100 mm/s × 0.5 s = 50 mm
        Assert.Equal(50, machine.CurrentPose.X, 3);

        machine.Execute(RobotCommand.JogAxis(Axis.Y, 10)); // 新目標 = (50, 10, 0)
        machine.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(50, machine.CurrentPose.X, 3);
        Assert.Equal(10, machine.CurrentPose.Y, 3);
        Assert.Equal(MachineState.Ready, machine.State);
    }

    [Fact]
    public void Jog_IsRejectedInAutoMode()
    {
        var machine = new VirtualRobotMachine();

        machine.Execute(RobotCommand.SetMode(RobotMode.Auto));
        var response = machine.Execute(RobotCommand.JogAxis(Axis.X, 1));

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCode.MachineNotReady, response.Error);
    }

    [Fact]
    public void EmergencyStop_FreezesMotion_UntilReset()
    {
        var machine = new VirtualRobotMachine(defaultSpeed: 100);
        machine.Execute(RobotCommand.Move(new RobotPose(1000, 0, 0))); // X 上限 500 → 會被擋
        machine.Execute(RobotCommand.Move(new RobotPose(300, 0, 0)));
        machine.Advance(TimeSpan.FromMilliseconds(500));

        var stopResponse = machine.Execute(RobotCommand.EmergencyStop());
        Assert.True(stopResponse.IsSuccess);
        Assert.Equal(MachineState.EmergencyStop, machine.State);

        var frozen = machine.CurrentPose;
        machine.Advance(TimeSpan.FromSeconds(5)); // 急停期間任何推進都無效
        Assert.Equal(frozen, machine.CurrentPose);

        // 急停中下 Jog / Move 都要被拒絕
        var jogResponse = machine.Execute(RobotCommand.JogAxis(Axis.X, 10));
        Assert.False(jogResponse.IsSuccess);
        Assert.Equal(ErrorCode.EmergencyStopActive, jogResponse.Error);

        var moveResponse = machine.Execute(RobotCommand.Move(new RobotPose(10, 0, 0)));
        Assert.False(moveResponse.IsSuccess);
        Assert.Equal(ErrorCode.EmergencyStopActive, moveResponse.Error);

        // 復歸後恢復正常
        machine.Execute(RobotCommand.Reset());
        Assert.Equal(MachineState.Ready, machine.State);
        Assert.True(machine.Execute(RobotCommand.JogAxis(Axis.X, 10)).IsSuccess);
    }

    [Fact]
    public void SetSpeed_ValidatesRange()
    {
        var machine = new VirtualRobotMachine(maxSpeed: 600);

        Assert.False(machine.Execute(RobotCommand.SetSpeed(0)).IsSuccess);
        Assert.False(machine.Execute(RobotCommand.SetSpeed(-10)).IsSuccess);
        Assert.False(machine.Execute(RobotCommand.SetSpeed(601)).IsSuccess);

        var ok = machine.Execute(RobotCommand.SetSpeed(250));
        Assert.True(ok.IsSuccess);
        Assert.Equal(250, machine.GetSnapshot().Speed, 3);
    }

    [Fact]
    public void GetSnapshot_IncrementsSequenceNumber()
    {
        var machine = new VirtualRobotMachine();

        var first = machine.GetSnapshot();
        var second = machine.GetSnapshot();

        Assert.Equal(first.Sequence + 1, second.Sequence);
    }

    [Fact]
    public void Advance_WithNonPositiveElapsed_IsIgnored()
    {
        var machine = new VirtualRobotMachine(defaultSpeed: 100);
        machine.Execute(RobotCommand.Move(new RobotPose(100, 0, 0)));

        machine.Advance(TimeSpan.Zero);
        machine.Advance(TimeSpan.FromSeconds(-5));

        Assert.Equal(RobotPose.Origin, machine.CurrentPose);
    }

    [Fact]
    public void Ping_AlwaysAnswers()
    {
        var machine = new VirtualRobotMachine();

        var response = machine.Execute(RobotCommand.Ping());

        Assert.True(response.IsSuccess);
        Assert.Equal("pong", response.Message);
    }

    [Fact]
    public void RejectedResponse_IsConvertedToException_OnTheClientSide()
    {
        // 這條最後一道防線位於 TcpRobotClient.ExecuteAsync：ERR 會被轉成 CommandRejectedException，
        // 而不是讓呼叫端拿到「成功但其實沒動」的假象。
        var machine = new VirtualRobotMachine();
        var response = machine.Execute(RobotCommand.Move(new RobotPose(0, 0, -10))); // Z 低於檯面

        Assert.False(response.IsSuccess);

        var exception = new CommandRejectedException(response.Command, response.Error, response.Message);
        Assert.Equal(ErrorCode.CoordinateOutOfRange, exception.ErrorCode);
        Assert.Equal(CommandType.Move, exception.Command);
    }
}

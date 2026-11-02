using ArmLink.Core.Models;
using ArmLink.Core.Protocol;

namespace ArmLink.Tests.Protocol;

/// <summary>
/// 協定編解碼測試：正常封包必須完整還原，損壞封包必須「嘗試失敗」而不是拋例外。
/// </summary>
public class RobotProtocolTests
{
    // ---------------- 正常封包 ----------------

    [Fact]
    public void EncodeMoveCommand_ProducesExpectedWireFormat()
    {
        var command = RobotCommand.Move(new RobotPose(10.5, -3.25, 100));

        Assert.Equal("MOVE:10.500,-3.250,100.000", RobotProtocol.EncodeCommand(command));
    }

    [Fact]
    public void TryParseMoveCommand_RestoresExactValues()
    {
        var ok = RobotProtocol.TryParseCommand("MOVE:10.500,-3.250,100.000", out var command, out var reason);

        Assert.True(ok, reason);
        Assert.NotNull(command);
        Assert.Equal(CommandType.Move, command!.Type);
        Assert.Equal(10.5, command.Pose.X, 6);
        Assert.Equal(-3.25, command.Pose.Y, 6);
        Assert.Equal(100, command.Pose.Z, 6);
    }

    [Theory]
    [InlineData("ESTOP", CommandType.EmergencyStop)]
    [InlineData("RESET", CommandType.Reset)]
    [InlineData("HOME", CommandType.Home)]
    [InlineData("PING", CommandType.Ping)]
    public void TryParseCommands_WithoutArguments_ParsesCorrectly(string line, CommandType expected)
    {
        Assert.True(RobotProtocol.TryParseCommand(line, out var command, out var reason), reason);

        Assert.NotNull(command);
        Assert.Equal(expected, command!.Type);
    }

    [Theory]
    [InlineData("SPEED:250.500", CommandType.SetSpeed, 250.5)]
    [InlineData("MODE:AUTO", CommandType.SetMode, 1)]
    [InlineData("MODE:MANUAL", CommandType.SetMode, 0)]
    public void TryParseCommands_WithArguments_ParsesCorrectly(string line, CommandType expected, double expectedValue)
    {
        Assert.True(RobotProtocol.TryParseCommand(line, out var command, out var reason), reason);

        Assert.NotNull(command);
        Assert.Equal(expected, command!.Type);

        if (expected is CommandType.SetSpeed)
        {
            Assert.Equal(expectedValue, command.Speed!.Value, 6);
        }
        else
        {
            Assert.Equal((RobotMode)expectedValue, command.Mode!.Value);
        }
    }

    [Fact]
    public void StatusRoundTrip_PreservesEveryField()
    {
        var snapshot = new RobotStatusSnapshot(
            new RobotPose(1.25, 2.5, 3.75),
            RobotMode.Auto,
            MachineState.Moving,
            ErrorCode.CoordinateOutOfRange,
            88.5,
            4242,
            DateTimeOffset.UtcNow);

        var line = RobotProtocol.EncodeStatus(snapshot);
        Assert.True(RobotProtocol.TryParseStatus(line, out var parsed, out var reason), reason);

        Assert.NotNull(parsed);
        Assert.Equal(snapshot.Pose, parsed!.Pose);
        Assert.Equal(RobotMode.Auto, parsed.Mode);
        Assert.Equal(MachineState.Moving, parsed.State);
        Assert.Equal(ErrorCode.CoordinateOutOfRange, parsed.Error);
        Assert.Equal(88.5, parsed.Speed, 3);
        Assert.Equal(4242, parsed.Sequence);
    }

    [Fact]
    public void OkResponseRoundTrip_ParsesCommandAndMessage()
    {
        var line = RobotProtocol.EncodeResponse(RobotResponse.Ok(CommandType.Move, "accepted"));

        Assert.True(RobotProtocol.TryParseResponse(line, out var response, out var reason), reason);
        Assert.NotNull(response);
        Assert.True(response!.IsSuccess);
        Assert.Equal(CommandType.Move, response.Command);
        Assert.Equal("accepted", response.Message);
    }

    [Fact]
    public void ErrorResponseRoundTrip_ParsesCodeAndMessage()
    {
        var line = RobotProtocol.EncodeResponse(
            RobotResponse.Fail(CommandType.Move, ErrorCode.CoordinateOutOfRange, "X 軸超出軟體極限"));

        Assert.True(RobotProtocol.TryParseResponse(line, out var response, out var reason), reason);
        Assert.NotNull(response);
        Assert.False(response!.IsSuccess);
        Assert.Equal(CommandType.Move, response.Command);
        Assert.Equal(ErrorCode.CoordinateOutOfRange, response.Error);
        Assert.Contains("X 軸超出軟體極限", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedCommandResponse_UsesUnknownCommandToken()
    {
        // Server 收到連關鍵字都無法識別的封包時，只能用 UNKNOWN 回應。
        var line = RobotProtocol.EncodeResponse(
            RobotResponse.Fail(CommandType.Unknown, ErrorCode.MalformedPacket, "壞封包"));

        Assert.True(RobotProtocol.TryParseResponse(line, out var response, out var reason), reason);
        Assert.NotNull(response);
        Assert.Equal(CommandType.Unknown, response!.Command);
        Assert.Equal(ErrorCode.MalformedPacket, response.Error);
    }

    // ---------------- 損壞封包防呆 ----------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MOVE:1,2")]                 // 分量不足
    [InlineData("MOVE:1,2,3,4")]             // 分量過多
    [InlineData("MOVE:a,b,c")]               // 非數值
    [InlineData("MOVE:1,,3")]                // 空分量
    [InlineData("MOVE:NaN,2,3")]             // NaN
    [InlineData("MOVE:Infinity,2,3")]        // 無限大
    [InlineData("FLY:1,2,3")]                // 不存在的指令
    [InlineData("SPEED:abc")]
    [InlineData("MODE:TELEPATHY")]
    [InlineData("MOVE:1,2,3\u0001")]         // 控制字元
    public void TryParseCommand_RejectsMalformedPackets(string malformed)
    {
        var ok = RobotProtocol.TryParseCommand(malformed, out var command, out var reason);

        Assert.False(ok);
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryParseCommand_RejectsOversizedPacket()
    {
        var oversized = new string('X', RobotProtocol.MaxLineLength + 1);

        Assert.False(RobotProtocol.TryParseCommand(oversized, out _, out var reason));
        Assert.Contains("超過上限", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("STATE:1.000,2.000,3.000|MANUAL|MOVING|NONE|100.000")]   // 少一個欄位
    [InlineData("STATE:1.000,2.000,3.000|MANUAL|FLYING|NONE|100.000|1")]  // 不存在的狀態
    [InlineData("STATE:1.000,2.000,3.000|TELEPATHY|MOVING|NONE|100.000|1")]
    [InlineData("STATE:1.000,2.000,3.000|MANUAL|MOVING|NO_SUCH_ERROR|100.000|1")]
    [InlineData("STATE:NaN,2.000,3.000|MANUAL|MOVING|NONE|100.000|1")]
    [InlineData("STATE:1.000,2.000|MANUAL|MOVING|NONE|100.000|1")]       // 座標分量不足
    [InlineData("STATUS:1.000,2.000,3.000|MANUAL|MOVING|NONE|100.000|1")] // 關鍵字錯誤
    [InlineData("STATE:1.000,2.000,3.000|MANUAL|MOVING|NONE|100.000|abc")]// 序號錯誤
    public void TryParseStatus_RejectsMalformedPackets(string malformed)
    {
        var ok = RobotProtocol.TryParseStatus(malformed, out var snapshot, out var reason);

        Assert.False(ok);
        Assert.Null(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("OK:|pong")]                       // 缺指令
    [InlineData("ERR:MOVE|NOT_A_CODE|oops")]       // 缺錯誤碼
    [InlineData("WEIRD:PING|pong")]                // 不是 OK / ERR
    [InlineData("OK")]                             // 沒有 ':' 分隔符
    public void TryParseResponse_RejectsMalformedPackets(string malformed)
    {
        var ok = RobotProtocol.TryParseResponse(malformed, out var response, out var reason);

        Assert.False(ok);
        Assert.Null(response);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Parse_IsTolerantToLineEndingsAndCasing()
    {
        // 真實裝置常送出不一致的結尾符號與大小寫，解析器要能吃。
        Assert.True(RobotProtocol.TryParseCommand("move:1,2,3\r\n", out var c, out var reason), reason);
        Assert.NotNull(c);
        Assert.Equal(1, c!.Pose.X, 6);

        Assert.True(RobotProtocol.TryParseStatus("state:1,2,3|auto|moving|none|50|9\n", out var s, out reason), reason);
        Assert.NotNull(s);
        Assert.Equal(RobotMode.Auto, s!.Mode);
    }

    [Fact]
    public void UnknownCommandToken_RoundTripsThroughProtocol()
    {
        // Unknown 是協定層保留值：Server 用它回應「連關鍵字都無法識別」的封包。
        Assert.Equal("UNKNOWN", RobotProtocol.ToToken(CommandType.Unknown));
        Assert.True(RobotProtocol.TryParseCommandType("UNKNOWN", out var parsed), string.Empty);
        Assert.Equal(CommandType.Unknown, parsed);
    }
}

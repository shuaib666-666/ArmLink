using System.Globalization;
using ArmLink.Core.Exceptions;
using ArmLink.Core.Models;

namespace ArmLink.Core.Protocol;

/// <summary>
/// ArmLink 的文字協定Codec中心。
/// <para>
/// 協定設計（Line-based ASCII，以 <c>\n</c> 結框）：
/// <list type="bullet">
///   <item><description>客戶端指令：<c>MOVE:X,Y,Z</c> / <c>JOG:dX,dY,dZ</c> / <c>HOME</c> / <c>ESTOP</c> / <c>RESET</c> / <c>SPEED:mm/s</c> / <c>MODE:AUTO</c> / <c>PING</c></description></item>
///   <item><description>指令回應：<c>OK:MOVE|accepted</c> / <c>ERR:MOVE|COORDINATE_OUT_OF_RANGE|X axis out of range</c></description></item>
///   <item><description>狀態廣播：<c>STATE:X,Y,Z|MODE|MACHINE_STATE|ERROR_CODE|speed|seq</c></description></item>
/// </list>
/// </para>
/// 設計重點：所有解析都走 Try 模式，壞封包只會讓 Try 回傳 false，絕不拋例外，因此壞資料不可能造成應用崩潰。
/// </summary>
public static class RobotProtocol
{
    /// <summary>封包結尾符。</summary>
    public const char LineTerminator = '\n';

    /// <summary>欄位分隔符（<c>|</c>）。</summary>
    public const char FieldSeparator = '|';

    /// <summary>關鍵字與數值的分隔符（<c>:</c>）。</summary>
    public const char ValueSeparator = ':';

    /// <summary>座標分量分隔符（<c>,</c>）。</summary>
    public const char CoordinateSeparator = ',';

    /// <summary>單一封包的長度上限，用來防止記憶體被惡意或損壞的資料流撐爆。</summary>
    public const int MaxLineLength = 1024;

    /// <summary>狀態廣播封包的關鍵字。</summary>
    public const string StatusKeyword = "STATE";

    /// <summary>成功回應的關鍵字。</summary>
    public const string OkKeyword = "OK";

    /// <summary>失敗回應的關鍵字。</summary>
    public const string ErrorKeyword = "ERR";

    private const string MoveKeyword = "MOVE";
    private const string JogKeyword = "JOG";
    private const string HomeKeyword = "HOME";
    private const string EmergencyStopKeyword = "ESTOP";
    private const string ResetKeyword = "RESET";
    private const string SpeedKeyword = "SPEED";
    private const string ModeKeyword = "MODE";
    private const string PingKeyword = "PING";
    private const string UnknownKeyword = "UNKNOWN";

    private static readonly Dictionary<MachineState, string> MachineStateTokens = new()
    {
        [MachineState.Unknown] = "UNKNOWN",
        [MachineState.Booting] = "BOOTING",
        [MachineState.Ready] = "READY",
        [MachineState.Moving] = "MOVING",
        [MachineState.Error] = "ERROR",
        [MachineState.EmergencyStop] = "ESTOP_ACTIVE",
    };

    private static readonly Dictionary<RobotMode, string> RobotModeTokens = new()
    {
        [RobotMode.Manual] = "MANUAL",
        [RobotMode.Auto] = "AUTO",
    };

    private static readonly Dictionary<ErrorCode, string> ErrorCodeTokens = new()
    {
        [ErrorCode.None] = "NONE",
        [ErrorCode.UnknownCommand] = "UNKNOWN_COMMAND",
        [ErrorCode.MalformedPacket] = "MALFORMED_PACKET",
        [ErrorCode.InvalidArgument] = "INVALID_ARGUMENT",
        [ErrorCode.CoordinateOutOfRange] = "COORDINATE_OUT_OF_RANGE",
        [ErrorCode.SpeedOutOfRange] = "SPEED_OUT_OF_RANGE",
        [ErrorCode.EmergencyStopActive] = "EMERGENCY_STOP_ACTIVE",
        [ErrorCode.MachineBusy] = "MACHINE_BUSY",
        [ErrorCode.MachineNotReady] = "MACHINE_NOT_READY",
        [ErrorCode.InternalError] = "INTERNAL_ERROR",
    };

    private static readonly Dictionary<CommandType, string> CommandTokens = new()
    {
        [CommandType.Move] = MoveKeyword,
        [CommandType.Jog] = JogKeyword,
        [CommandType.Home] = HomeKeyword,
        [CommandType.EmergencyStop] = EmergencyStopKeyword,
        [CommandType.Reset] = ResetKeyword,
        [CommandType.SetSpeed] = SpeedKeyword,
        [CommandType.SetMode] = ModeKeyword,
        [CommandType.Ping] = PingKeyword,
        [CommandType.Unknown] = UnknownKeyword,
    };

    private static readonly Dictionary<string, MachineState> MachineStates = ReverseTokens(MachineStateTokens);
    private static readonly Dictionary<string, RobotMode> RobotModes = ReverseTokens(RobotModeTokens);
    private static readonly Dictionary<string, ErrorCode> ErrorCodes = ReverseTokens(ErrorCodeTokens);
    private static readonly Dictionary<string, CommandType> CommandTypes = ReverseTokens(CommandTokens);

    // ------------------------------------------------------------------
    // 指令：Encode / Decode
    // ------------------------------------------------------------------

    /// <summary>把指令物件序列化成協定字串（不含換行符）。</summary>
    /// <param name="command">要序列化的指令。</param>
    /// <returns>協定字串。</returns>
    /// <exception cref="RobotProtocolException">遇到不支援的指令類型。</exception>
    public static string EncodeCommand(RobotCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Type switch
        {
            CommandType.Move => string.Create(CultureInfo.InvariantCulture, $"{MoveKeyword}{ValueSeparator}{FormatPose(command.Pose)}"),
            CommandType.Jog => string.Create(CultureInfo.InvariantCulture, $"{JogKeyword}{ValueSeparator}{FormatPose(command.Pose)}"),
            CommandType.Home => HomeKeyword,
            CommandType.EmergencyStop => EmergencyStopKeyword,
            CommandType.Reset => ResetKeyword,
            CommandType.SetSpeed => string.Create(CultureInfo.InvariantCulture, $"{SpeedKeyword}{ValueSeparator}{(command.Speed ?? 0):F3}"),
            CommandType.SetMode => string.Create(CultureInfo.InvariantCulture, $"{ModeKeyword}{ValueSeparator}{ToToken(command.Mode ?? Models.RobotMode.Manual)}"),
            CommandType.Ping => PingKeyword,
            _ => throw new RobotProtocolException($"不支援的指令類型：{command.Type}", command.ToString()),
        };
    }

    /// <summary>
    /// 嘗試解析一行指令封包。
    /// </summary>
    /// <param name="line">原始字串（可含或不含換行符）。</param>
    /// <param name="command">解析成功時的指令物件。</param>
    /// <param name="failureReason">解析失敗時的原因，供日誌使用。</param>
    /// <returns>成功回傳 <c>true</c>。</returns>
    public static bool TryParseCommand(string? line, out RobotCommand? command, out string? failureReason)
    {
        command = null;

        if (!TryNormalize(line, out var payload, out failureReason))
        {
            return false;
        }

        var (keyword, argument) = SplitHead(payload);

        switch (keyword.ToUpperInvariant())
        {
            case MoveKeyword:
                if (!TryParsePose(argument, out var target, out failureReason))
                {
                    return false;
                }

                command = RobotCommand.Move(target);
                return true;

            case JogKeyword:
                if (!TryParsePose(argument, out var delta, out failureReason))
                {
                    return false;
                }

                command = RobotCommand.Jog(delta);
                return true;

            case SpeedKeyword:
                if (!TryParseDouble(argument, out var speed, out failureReason))
                {
                    return false;
                }

                command = RobotCommand.SetSpeed(speed);
                return true;

            case ModeKeyword:
                if (!TryParseRobotMode(argument, out var mode))
                {
                    failureReason = $"無法解析作業模式：{argument}";
                    return false;
                }

                command = RobotCommand.SetMode(mode);
                return true;

            case HomeKeyword when argument.Length == 0:
                command = RobotCommand.Home();
                return true;

            case EmergencyStopKeyword when argument.Length == 0:
                command = RobotCommand.EmergencyStop();
                return true;

            case ResetKeyword when argument.Length == 0:
                command = RobotCommand.Reset();
                return true;

            case PingKeyword when argument.Length == 0:
                command = RobotCommand.Ping();
                return true;

            default:
                failureReason = string.IsNullOrEmpty(keyword)
                    ? "封包缺少指令關鍵字"
                    : $"無法識別的指令關鍵字：{keyword}";
                return false;
        }
    }

    // ------------------------------------------------------------------
    // 狀態廣播：Encode / Decode
    // ------------------------------------------------------------------

    /// <summary>把狀態快照序列化成廣播封包字串（不含換行符）。</summary>
    /// <param name="snapshot">狀態快照。</param>
    /// <returns>協定字串。</returns>
    public static string EncodeStatus(RobotStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{StatusKeyword}{ValueSeparator}{FormatPose(snapshot.Pose)}{FieldSeparator}{ToToken(snapshot.Mode)}{FieldSeparator}{ToToken(snapshot.State)}{FieldSeparator}{ToToken(snapshot.Error)}{FieldSeparator}{snapshot.Speed:F3}{FieldSeparator}{snapshot.Sequence}");
    }

    /// <summary>
    /// 嘗試解析一行狀態廣播封包。
    /// </summary>
    /// <param name="line">原始字串。</param>
    /// <param name="snapshot">解析成功時的狀態快照。</param>
    /// <param name="failureReason">解析失敗時的原因。</param>
    /// <returns>成功回傳 <c>true</c>。</returns>
    public static bool TryParseStatus(string? line, out RobotStatusSnapshot? snapshot, out string? failureReason)
    {
        snapshot = null;

        if (!TryNormalize(line, out var payload, out failureReason))
        {
            return false;
        }

        var fields = payload.Split(FieldSeparator);
        if (fields.Length < 6)
        {
            failureReason = $"狀態封包欄位數量不足（期望 6，實際 {fields.Length}）";
            return false;
        }

        var (keyword, argument) = SplitHead(fields[0]);
        if (!string.Equals(keyword, StatusKeyword, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = $"這不是狀態封包，關鍵字為：{keyword}";
            return false;
        }

        if (!TryParsePose(argument, out var pose, out failureReason))
        {
            return false;
        }

        if (!TryParseRobotMode(fields[1], out var mode))
        {
            failureReason = $"無法解析作業模式：{fields[1]}";
            return false;
        }

        if (!TryParseMachineState(fields[2], out var state))
        {
            failureReason = $"無法解析機台狀態：{fields[2]}";
            return false;
        }

        if (!TryParseErrorCode(fields[3], out var error))
        {
            failureReason = $"無法解析錯誤代碼：{fields[3]}";
            return false;
        }

        if (!TryParseDouble(fields[4], out var speed, out failureReason))
        {
            return false;
        }

        if (!long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
        {
            failureReason = $"無法解析封包序號：{fields[5]}";
            return false;
        }

        // 協定不傳時間，統一由接收端蓋章，避免兩端時鐘不同步造成困擾。
        snapshot = new RobotStatusSnapshot(pose, mode, state, error, speed, sequence, DateTimeOffset.UtcNow);
        return true;
    }

    // ------------------------------------------------------------------
    // 指令回應：Encode / Decode
    // ------------------------------------------------------------------

    /// <summary>把 OK / ERR 回應序列化成協定字串（不含換行符）。</summary>
    /// <param name="response">回應物件。</param>
    /// <returns>協定字串。</returns>
    public static string EncodeResponse(RobotResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var head = response.IsSuccess
            ? string.Create(CultureInfo.InvariantCulture, $"{OkKeyword}{ValueSeparator}{ToToken(response.Command)}")
            : string.Create(CultureInfo.InvariantCulture, $"{ErrorKeyword}{ValueSeparator}{ToToken(response.Command)}{FieldSeparator}{ToToken(response.Error)}");

        var message = Sanitize(response.Message);
        return message.Length == 0 ? head : string.Create(CultureInfo.InvariantCulture, $"{head}{FieldSeparator}{message}");
    }

    /// <summary>
    /// 嘗試解析一行 OK / ERR 回應封包。
    /// </summary>
    /// <param name="line">原始字串。</param>
    /// <param name="response">解析成功時的回應物件。</param>
    /// <param name="failureReason">解析失敗時的原因。</param>
    /// <returns>成功回傳 <c>true</c>。</returns>
    public static bool TryParseResponse(string? line, out RobotResponse? response, out string? failureReason)
    {
        response = null;

        if (!TryNormalize(line, out var payload, out failureReason))
        {
            return false;
        }

        var fields = payload.Split(FieldSeparator);
        var (keyword, argument) = SplitHead(fields[0]);

        if (!TryParseCommandType(argument, out var command))
        {
            failureReason = $"回應封包指到無法識別的指令：{argument}";
            return false;
        }

        if (string.Equals(keyword, OkKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var message = fields.Length > 1 ? string.Join(FieldSeparator, fields.Skip(1)) : string.Empty;
            response = RobotResponse.Ok(command, message);
            return true;
        }

        if (string.Equals(keyword, ErrorKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (fields.Length < 3)
            {
                failureReason = $"ERR 封包欄位數量不足（期望 ≥3，實際 {fields.Length}）";
                return false;
            }

            if (!TryParseErrorCode(fields[1], out var code))
            {
                failureReason = $"無法解析錯誤代碼：{fields[1]}";
                return false;
            }

            response = RobotResponse.Fail(command, code, string.Join(FieldSeparator, fields.Skip(2)));
            return true;
        }

        failureReason = $"這不是 OK / ERR 回應，關鍵字為：{keyword}";
        return false;
    }

    // ------------------------------------------------------------------
    // Token 轉換（給外部方便查詢 / 測試用）
    // ------------------------------------------------------------------

    /// <summary>列舉轉協定 Token。</summary>
    public static string ToToken(MachineState state) => MachineStateTokens.TryGetValue(state, out var token) ? token : MachineStateTokens[MachineState.Unknown];

    /// <summary>列舉轉協定 Token。</summary>
    public static string ToToken(RobotMode mode) => RobotModeTokens.TryGetValue(mode, out var token) ? token : RobotModeTokens[Models.RobotMode.Manual];

    /// <summary>列舉轉協定 Token。</summary>
    public static string ToToken(ErrorCode code) => ErrorCodeTokens.TryGetValue(code, out var token) ? token : ErrorCodeTokens[ErrorCode.InternalError];

    /// <summary>列舉轉協定 Token。</summary>
    public static string ToToken(CommandType command) => CommandTokens.TryGetValue(command, out var token) ? token : string.Empty;

    /// <summary>協定 Token 轉機台狀態。</summary>
    public static bool TryParseMachineState(string? token, out MachineState state) => MachineStates.TryGetValue(NormalizeToken(token), out state);

    /// <summary>協定 Token 轉作業模式。</summary>
    public static bool TryParseRobotMode(string? token, out RobotMode mode) => RobotModes.TryGetValue(NormalizeToken(token), out mode);

    /// <summary>協定 Token 轉錯誤代碼。</summary>
    public static bool TryParseErrorCode(string? token, out ErrorCode code) => ErrorCodes.TryGetValue(NormalizeToken(token), out code);

    /// <summary>協定 Token 轉指令類型。</summary>
    public static bool TryParseCommandType(string? token, out CommandType command) => CommandTypes.TryGetValue(NormalizeToken(token), out command);

    /// <summary>把封包內容補上結尾換行符。</summary>
    public static string AppendTerminator(string payload) => payload + LineTerminator;

    // ------------------------------------------------------------------
    // 內部工具
    // ------------------------------------------------------------------

    /// <summary>座標 → <c>X,Y,Z</c> 字串。</summary>
    private static string FormatPose(RobotPose pose) =>
        string.Create(CultureInfo.InvariantCulture, $"{pose.X:F3},{pose.Y:F3},{pose.Z:F3}");

    /// <summary>解析座標字串。</summary>
    private static bool TryParsePose(string? text, out RobotPose pose, out string? failureReason)
    {
        pose = RobotPose.Origin;
        failureReason = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            failureReason = "座標欄位為空";
            return false;
        }

        var parts = text.Split(CoordinateSeparator);
        if (parts.Length != 3)
        {
            failureReason = $"座標必須剛好 3 個分量（實際 {parts.Length}）：{text}";
            return false;
        }

        if (!TryParseDouble(parts[0], out var x, out failureReason) ||
            !TryParseDouble(parts[1], out var y, out failureReason) ||
            !TryParseDouble(parts[2], out var z, out failureReason))
        {
            return false;
        }

        pose = new RobotPose(x, y, z);
        return true;
    }

    /// <summary>嚴格的浮點數解析：不吃 NaN / Infinity，也不吃地區性格式。</summary>
    private static bool TryParseDouble(string? text, out double value, out string? failureReason)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            failureReason = "數值欄位為空";
            return false;
        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            failureReason = $"無法解析數值：{text}";
            return false;
        }

        if (!double.IsFinite(value))
        {
            failureReason = $"數值不是有限數：{text}";
            value = 0;
            return false;
        }

        failureReason = null;
        return true;
    }

    /// <summary>
    /// 封包前置清理：去換行、去空白、長度檢查、控制字元檢查。
    /// 這一步擋掉了絕大多數「看起來很像封包」的垃圾資料。
    /// </summary>
    private static bool TryNormalize(string? line, out string payload, out string? failureReason)
    {
        payload = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            failureReason = "封包為空";
            return false;
        }

        var trimmed = line.Trim('\r', '\n', ' ', '\t');
        if (trimmed.Length == 0)
        {
            failureReason = "封包為空";
            return false;
        }

        if (trimmed.Length > MaxLineLength)
        {
            failureReason = $"封包長度 {trimmed.Length} 超過上限 {MaxLineLength}";
            return false;
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                failureReason = $"封包含有不可列印字元 U+{(int)character:X4}";
                return false;
            }
        }

        payload = trimmed;
        failureReason = null;
        return true;
    }

    /// <summary>把 <c>KEY:value</c> 切成關鍵字與內容。</summary>
    private static (string Keyword, string Value) SplitHead(string payload)
    {
        var colon = payload.IndexOf(ValueSeparator);
        return colon < 0
            ? (payload, string.Empty)
            : (payload[..colon], payload[(colon + 1)..]);
    }

    private static string NormalizeToken(string? token) => token?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>把 Token 中的分隔符號替換掉，避免訊息內容污染協定結構。</summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character is LineTerminator or FieldSeparator or '\r' ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static Dictionary<string, T> ReverseTokens<T>(IReadOnlyDictionary<T, string> source)
        where T : notnull
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            map[pair.Value] = pair.Key;
        }

        return map;
    }
}

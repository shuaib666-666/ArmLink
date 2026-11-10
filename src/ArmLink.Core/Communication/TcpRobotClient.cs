using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ArmLink.Core.Abstractions;
using ArmLink.Core.Exceptions;
using ArmLink.Core.Models;
using ArmLink.Core.Protocol;

namespace ArmLink.Core.Communication;

/// <summary>
/// 完全非同步的 TCP/IP 機械手臂客戶端。
/// <para>架構摘要：</para>
/// <list type="bullet">
///   <item><description>送出：呼叫端把指令丟進 <see cref="Channel{T}"/> 佇列就立刻返回，IO 背景執行，UI 永遠不會卡。</description></item>
///   <item><description>接收：一條長駐的讀取迴圈負責組框（以 <c>\n</c> 切行），支援跨封包的殘包拼接。</description></item>
///   <item><description>連線：單一的 Session 迴圈同時負責「連線 + 斷線偵測 + 自動重連」，避免多條背景工作互相競爭。</description></item>
///   <item><description>容錯：所有例外都轉成事件；<c>RobotException</c> 家族會被往上拋（給呼叫端處理），非預期例外一律被記錄下來而不是讓程式崩潰。</description></item>
/// </list>
/// </summary>
public sealed class TcpRobotClient : IRobotClient
{
    private readonly RobotClientOptions _options;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Channel<PendingRequest> _outbox = Channel.CreateUnbounded<PendingRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentQueue<TaskCompletionSource<RobotResponse>> _pendingResponses = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly byte[] _readBuffer;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _sessionCts;
    private Task? _sessionLoop;
    private volatile RobotStatusSnapshot? _latestStatus;
    private volatile ClientConnectionState _state = ClientConnectionState.Disconnected;
    private int _disposed;

    /// <summary>建立客戶端。</summary>
    /// <param name="options">連線設定。</param>
    public TcpRobotClient(RobotClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _readBuffer = new byte[Math.Max(256, options.ReceiveBufferSize)];
    }

    /// <summary>建立客戶端（使用預設設定）。</summary>
    /// <param name="host">IP 或主機名稱。</param>
    /// <param name="port">埠號。</param>
    public TcpRobotClient(string host, int port)
        : this(RobotClientOptions.Create(host, port))
    {
    }

    /// <inheritdoc/>
    public bool IsConnected => State == ClientConnectionState.Connected && (_tcpClient?.Connected ?? false);

    /// <inheritdoc/>
    public ClientConnectionState State => _state;

    /// <inheritdoc/>
    public RobotStatusSnapshot? LatestStatus => _latestStatus;

    /// <inheritdoc/>
    public event EventHandler<StatusReceivedEventArgs>? StatusReceived;

    /// <inheritdoc/>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <inheritdoc/>
    public event EventHandler<RobotLogEventArgs>? LogEmitted;

    /// <inheritdoc/>
    public event EventHandler<MalformedPacketEventArgs>? MalformedPacketReceived;

    // ------------------------------------------------------------------
    // 連線生命週期
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ConnectAsyncCore(cancellationToken);
    }

    private async Task ConnectAsyncCore(CancellationToken cancellationToken)
    {
        TaskCompletionSource firstAttempt;
        CancellationTokenSource sessionCts;

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionLoop is { IsCompleted: false })
            {
                // Session 還活著（可能正在重連），不要重複開。
                return;
            }

            firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _sessionCts = sessionCts;
            _sessionLoop = Task.Run(() => SessionLoopAsync(firstAttempt, sessionCts.Token), CancellationToken.None);
        }
        finally
        {
            _sessionGate.Release();
        }

        // 只等「第一次嘗試」的結果：成功了就返回，失敗了把例外往上丟，讓 UI 可以直接秀錯誤。
        // 之後失敗就交給自動重連機制處理，不再打擾呼叫端。
        try
        {
            await firstAttempt.Task.WaitAsync(_options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            firstAttempt.TrySetCanceled(cancellationToken);
            throw new RobotCommunicationException($"連線逾時：{_options.Host}:{_options.Port}（{_options.ConnectTimeout.TotalSeconds:F1} 秒）。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 把 Socket 層的原始例外統一收斂成我們自己的例外，呼叫端只要 catch RobotException 家族就夠了。
            throw new RobotCommunicationException($"無法連線至 {_options.Host}:{_options.Port}：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 單一 Session 迴圈：連線 → 跑 IO → 斷線 → 依退避策略重連，直到使用者中斷或放棄。
    /// </summary>
    private async Task SessionLoopAsync(TaskCompletionSource firstAttempt, CancellationToken token)
    {
        var attempt = 0;
        Exception? lastError = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                attempt++;
                SetState(attempt == 1 ? ClientConnectionState.Connecting : ClientConnectionState.Reconnecting, null);

                try
                {
                    var client = await OpenConnectionAsync(token).ConfigureAwait(false);
                    lastError = null;
                    attempt = 0;
                    firstAttempt.TrySetResult();
                    EmitLog(LogDirection.System, $"已連線 {_options.Host}:{_options.Port}");

                    await RunConnectionAsync(client, token).ConfigureAwait(false);
                    EmitLog(LogDirection.System, "連線已中斷");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    EmitLog(LogDirection.System, $"連線失敗：{ex.Message}");
                }

                // 第一次嘗試若失敗，這裡會把例外塞給正在等待的 ConnectAsync。
                firstAttempt.TrySetException(lastError ?? new RobotCommunicationException("無法連線至機台。"));

                CleanupConnection();
                FailPendingRequests(new RobotCommunicationException("連線已中斷，尚未收到回應。"));
                SetState(ClientConnectionState.Disconnected, lastError?.Message);

                if (!_options.AutoReconnect)
                {
                    break;
                }

                attempt = Math.Max(attempt, 1);
                if (!_options.Reconnect.ShouldRetry(attempt))
                {
                    EmitLog(LogDirection.System, $"已達最大重試次數（{_options.Reconnect.MaxAttempts}），停止重連。");
                    break;
                }

                var delay = _options.Reconnect.GetDelay(attempt);
                EmitLog(LogDirection.System, $"{delay.TotalSeconds:F1} 秒後自動重連（第 {attempt} 次）…");
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // 理論上到不了這裡，但工控程式的基本教養：背景迴圈永遠不能因為例外而讓整個行程掛掉。
            EmitLog(LogDirection.System, $"Session 迴圈發生未預期錯誤：{ex.Message}");
        }
        finally
        {
            CleanupConnection();
            FailPendingRequests(new RobotCommunicationException("連線已關閉，尚未收到回應。"));
            SetState(ClientConnectionState.Disconnected, null);
        }
    }

    private async Task<TcpClient> OpenConnectionAsync(CancellationToken token)
    {
        var client = new TcpClient
        {
            NoDelay = true, // 工控指令很小，禁用 Nagle 演算法降低延遲
        };

        using var connectTimeoutCts = new CancellationTokenSource(_options.ConnectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, connectTimeoutCts.Token);
        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, linkedCts.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        lock (_sync)
        {
            _tcpClient = client;
            _stream = client.GetStream();
        }

        SetState(ClientConnectionState.Connected, null);
        return client;
    }

    /// <summary>讀寫兩條背景工作並行執行，其中一條結束就代表這條連線結束了。</summary>
    private async Task RunConnectionAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        var writeLoop = Task.Run(() => WritePumpAsync(stream, connectionCts.Token), CancellationToken.None);
        try
        {
            await ReadLoopAsync(stream, connectionCts.Token).ConfigureAwait(false);
        }
        finally
        {
            await connectionCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await writeLoop.ConfigureAwait(false);
            }
            catch
            {
                // 寫入端的例外不需要往上拋，讀取端已經代表連線結束了。
            }
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
        CancellationTokenSource? sessionCts;
        lock (_sync)
        {
            sessionCts = _sessionCts;
            _sessionCts = null;
        }

        var loop = _sessionLoop;
        CleanupConnection();
        FailPendingRequests(new RobotCommunicationException("已由使用者中斷連線。"));

        if (sessionCts is not null)
        {
            try
            {
                await sessionCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 已經被釋放過了，忽略。
            }
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // Session 迴圈內部已經處理過所有例外。
            }
        }

        sessionCts?.Dispose();
        SetState(ClientConnectionState.Disconnected, "已斷線");
    }

    // ------------------------------------------------------------------
    // 指令送出
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    public Task SendCommandAsync(RobotCommand command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            throw new RobotCommunicationException("尚未連線，無法送出指令。");
        }

        var line = RobotProtocol.EncodeCommand(command);
        var completion = new TaskCompletionSource<RobotResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(line, command.Type, completion, expectsResponse: false);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<RobotResponse> ExecuteAsync(RobotCommand command, CancellationToken cancellationToken = default)
        => ExecuteAsync(command, _options.DefaultCommandTimeout, cancellationToken);

    /// <inheritdoc/>
    public async Task<RobotResponse> ExecuteAsync(RobotCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            throw new RobotCommunicationException("尚未連線，無法送出指令。");
        }

        var line = RobotProtocol.EncodeCommand(command);
        var completion = new TaskCompletionSource<RobotResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(line, command.Type, completion, expectsResponse: true);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
        var finished = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);

        if (finished == timeoutTask)
        {
            completion.TrySetException(new RobotTimeoutException($"等待 {RobotProtocol.ToToken(command.Type)} 回應逾時（{timeout.TotalMilliseconds:F0} ms）。"));
        }
        else
        {
            await timeoutCts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            var response = await completion.Task.ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                throw new CommandRejectedException(command.Type, response.Error, response.Message);
            }

            return response;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private void Enqueue(string line, CommandType command, TaskCompletionSource<RobotResponse> completion, bool expectsResponse)
    {
        if (!_outbox.Writer.TryWrite(new PendingRequest(line, command, completion, expectsResponse)))
        {
            throw new RobotCommunicationException("指令佇列已關閉。");
        }
    }

    // ------------------------------------------------------------------
    // 背景：寫入泵
    // ------------------------------------------------------------------

    private async Task WritePumpAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out var request))
                {
                    // 先排隊再送出：確保不會發生「回應比請求還早被處理」的錯配。
                    if (request.ExpectsResponse)
                    {
                        _pendingResponses.Enqueue(request.Completion);
                    }

                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(RobotProtocol.AppendTerminator(request.Line));
                        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                        await stream.FlushAsync(token).ConfigureAwait(false);
                        EmitLog(LogDirection.Transmit, request.Line);

                        if (!request.ExpectsResponse)
                        {
                            request.Completion.TrySetResult(RobotResponse.Ok(request.Command, "enqueued"));
                        }
                    }
                    catch (Exception ex)
                    {
                        request.Completion.TrySetException(ToKnownException(ex));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 連線被關閉，正常結束。
        }
        catch (Exception)
        {
            // 其他 IO 錯誤交給 Session 迴圈統一處理。
        }
    }

    // ------------------------------------------------------------------
    // 背景：讀取迴圈與封包分派
    // ------------------------------------------------------------------

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var pending = new StringBuilder();
        // 增量解碼器：UTF-8 多位元組字元可能被 TCP 分包從中間切開，
        // Decoder 會保留未完成的序列直到湊齊，不會產生亂碼。
        var decoder = Encoding.UTF8.GetDecoder();
        var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(_readBuffer.Length)];

        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(_readBuffer.AsMemory(), token).ConfigureAwait(false);
            if (read <= 0)
            {
                // read == 0 代表對方正常關閉連線。
                return;
            }

            var chars = decoder.GetChars(_readBuffer, 0, read, charBuffer, 0, flush: false);

            // UTF-8 的續位元組皆 >= 0x80，'\n' (0x0A) 不可能出現在多位元組序列裡，
            // 因此逐字元掃描切行是安全的。
            for (var i = 0; i < chars; i++)
            {
                var character = charBuffer[i];

                if (character == RobotProtocol.LineTerminator)
                {
                    var line = pending.ToString();
                    pending.Clear();
                    HandleLine(line);
                }
                else if (character != '\r')
                {
                    pending.Append(character);

                    // 超過長度上限卻還沒看到換行符 => 這條流一定有問題，直接丟棄重來。
                    if (pending.Length > RobotProtocol.MaxLineLength)
                    {
                        pending.Clear();
                        RaiseMalformed($"<累積超過 {RobotProtocol.MaxLineLength} 字元>", "封包超長且未收到換行符");
                    }
                }
            }
        }
    }

    private void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        EmitLog(LogDirection.Receive, line);

        if (line.StartsWith(RobotProtocol.StatusKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (RobotProtocol.TryParseStatus(line, out var status, out var reason))
            {
                _latestStatus = status;
                StatusReceived?.Invoke(this, new StatusReceivedEventArgs(status!));
            }
            else
            {
                RaiseMalformed(line, reason ?? "未知的狀態封包錯誤");
            }

            return;
        }

        if (line.StartsWith(RobotProtocol.OkKeyword, StringComparison.Ordinal) ||
            line.StartsWith(RobotProtocol.ErrorKeyword, StringComparison.Ordinal))
        {
            if (RobotProtocol.TryParseResponse(line, out var response, out var reason))
            {
                CompleteNextPending(response!);
            }
            else
            {
                RaiseMalformed(line, reason ?? "未知的回應封包錯誤");
            }

            return;
        }

        RaiseMalformed(line, "非預期的封包類型");
    }

    /// <summary>
    /// FIFO 地把回應配對給等待中的請求。已逾時（Task 已完成）的請求會被跳過並丟棄，避免錯配。
    /// </summary>
    private void CompleteNextPending(RobotResponse response)
    {
        while (_pendingResponses.TryDequeue(out var pending))
        {
            if (pending.Task.IsCompleted)
            {
                continue;
            }

            EmitLog(LogDirection.System, $"收到 {response}");
            pending.TrySetResult(response);
            return;
        }

        EmitLog(LogDirection.System, $"收到回應但沒有對應的請求：{response}");
    }

    private void FailPendingRequests(RobotCommunicationException exception)
    {
        while (_pendingResponses.TryDequeue(out var pending))
        {
            pending.TrySetException(exception);
        }
    }

    // ------------------------------------------------------------------
    // 事件與資源
    // ------------------------------------------------------------------

    private void SetState(ClientConnectionState state, string? reason)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, reason));
    }

    private void RaiseMalformed(string rawPacket, string reason)
    {
        EmitLog(LogDirection.System, $"[BAD PACKET] {reason}：{rawPacket}");
        MalformedPacketReceived?.Invoke(this, new MalformedPacketEventArgs(rawPacket, reason));
    }

    private void CleanupConnection()
    {
        NetworkStream? stream;
        TcpClient? client;

        lock (_sync)
        {
            stream = _stream;
            client = _tcpClient;
            _stream = null;
            _tcpClient = null;
        }

        try
        {
            stream?.Dispose();
        }
        catch
        {
            // 關閉時期的例外沒有價值。
        }

        try
        {
            client?.Dispose();
        }
        catch
        {
            // 同上。
        }
    }

    private static RobotCommunicationException ToKnownException(Exception exception) => exception switch
    {
        ObjectDisposedException => new RobotCommunicationException("連線已關閉，無法送出資料。", exception),
        IOException => new RobotCommunicationException($"Socket 讀寫失敗：{exception.Message}", exception),
        SocketException socketException => new RobotCommunicationException($"Socket 錯誤（{socketException.SocketErrorCode}）：{exception.Message}", exception),
        _ => new RobotCommunicationException($"送出指令時發生錯誤：{exception.Message}", exception),
    };

    private void EmitLog(LogDirection direction, string line)
    {
        try
        {
            LogEmitted?.Invoke(this, new RobotLogEventArgs(direction, line));
        }
        catch
        {
            // 訂閱者的例外不應該影響通訊主流程。
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 忽略。
        }

        _outbox.Writer.TryComplete();
        _sessionGate.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly record struct PendingRequest(
        string Line,
        CommandType Command,
        TaskCompletionSource<RobotResponse> Completion,
        bool ExpectsResponse);
}

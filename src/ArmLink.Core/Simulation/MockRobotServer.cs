using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ArmLink.Core.Abstractions;
using ArmLink.Core.Models;
using ArmLink.Core.Protocol;

namespace ArmLink.Core.Simulation;

/// <summary>
/// 內建的虛擬機台服務（Mock Server）。
/// <para>三條背景工作：</para>
/// <list type="bullet">
///   <item><description>Accept 迴圈：接受連線，每條連線再拆出一條讀取緒與一條寫入緒。</description></item>
///   <item><description>Broadcast 迴圈：以 <see cref="MockRobotServerOptions.MotionInterval"/> 推進插值運動，累積到 <see cref="MockRobotServerOptions.BroadcastInterval"/> 就對所有客戶端廣播座標。</description></item>
///   <item><description>Per-Client 迴圈：組框、解析指令、模擬處理延遲、回 OK / ERR。</description></item>
/// </list>
/// 任何單一連線發生例外都只會影響那條連線，伺服器本體繼續服務其他人。
/// </summary>
public sealed class MockRobotServer : IRobotServer
{
    private readonly MockRobotServerOptions _options;
    private readonly VirtualRobotMachine _machine;
    private readonly ConcurrentDictionary<Guid, ConnectedClient> _clients = new();
    private readonly SemaphoreSlim _startStopGate = new(1, 1);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _broadcastLoop;
    private volatile bool _running;
    private int _disposed;

    /// <summary>建立虛擬機台。</summary>
    /// <param name="options">設定值；省略則使用 <see cref="MockRobotServerOptions.LocalDefault"/>。</param>
    public MockRobotServer(MockRobotServerOptions? options = null)
    {
        _options = options ?? MockRobotServerOptions.LocalDefault;
        _machine = new VirtualRobotMachine(_options.Limits, _options.DefaultSpeed, _options.MaxSpeed);
    }

    /// <summary>建立虛擬機台並指定埠號（Port 設 0 可由系統自動分配）。</summary>
    /// <param name="port">埠號。</param>
    public MockRobotServer(int port)
        : this(new MockRobotServerOptions { Port = port })
    {
    }

    /// <inheritdoc/>
    public bool IsRunning => _running;

    /// <inheritdoc/>
    public int ClientCount => _clients.Count;

    /// <inheritdoc/>
    public int Port => _listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : _options.Port;

    /// <summary>可直接存取虛擬手臂（UI 或測試想偷看 / 強制改狀態時很好用）。</summary>
    public VirtualRobotMachine Machine => _machine;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _startStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listener is not null)
            {
                return; // 已經在跑了
            }

            var listener = new TcpListener(CreateEndpoint(_options.Host, _options.Port));
            listener.Start();
            _listener = listener;

            var cts = new CancellationTokenSource();
            _cts = cts;

            _acceptLoop = Task.Run(() => AcceptLoopAsync(cts.Token), CancellationToken.None);
            _broadcastLoop = Task.Run(() => BroadcastLoopAsync(cts.Token), CancellationToken.None);
            _running = true;
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _startStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cts = _cts;
            if (cts is null)
            {
                return;
            }

            _cts = null;
            await cts.CancelAsync().ConfigureAwait(false);

            try
            {
                _listener?.Stop();
            }
            catch
            {
                // 已停止或非預期的 Socket 錯誤，忽略。
            }

            var accept = _acceptLoop;
            var broadcast = _broadcastLoop;
            _acceptLoop = null;
            _broadcastLoop = null;

            if (broadcast is not null)
            {
                try
                {
                    await broadcast.ConfigureAwait(false);
                }
                catch
                {
                    // 停止過程中的例外不需往上拋。
                }
            }

            if (accept is not null)
            {
                try
                {
                    await accept.ConfigureAwait(false);
                }
                catch
                {
                    // 同上。
                }
            }

            foreach (var pair in _clients)
            {
                pair.Value.Outbox.Writer.TryComplete();
            }

            _clients.Clear();
            _listener?.Dispose();
            _listener = null;
            cts.Dispose();
            _running = false;
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Accept / Broadcast
    // ------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener!;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                var id = Guid.NewGuid();
                client.NoDelay = true;

                var outbox = Channel.CreateUnbounded<string>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

                if (!_clients.TryAdd(id, new ConnectedClient(id, outbox)))
                {
                    client.Dispose();
                    continue;
                }

                // 每條連線自成一個小世界：讀取緒 + 寫入緒。
                var connectionToken = token;
                _ = Task.Run(() => HandleClientAsync(id, client, outbox, connectionToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        var motionInterval = _options.MotionInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(20)
            : _options.MotionInterval;
        var broadcastInterval = _options.BroadcastInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(100)
            : _options.BroadcastInterval;

        using var timer = new PeriodicTimer(motionInterval);
        var sinceBroadcast = TimeSpan.Zero;
        var lastTick = DateTimeOffset.UtcNow;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            var elapsed = now - lastTick;
            lastTick = now;

            // 1) 推進插值運動（每 20ms）
            _machine.Advance(elapsed);

            // 2) 累積到 100ms 才廣播，符合「10Hz 狀態回報」的規格
            sinceBroadcast += elapsed;
            if (sinceBroadcast < broadcastInterval)
            {
                continue;
            }

            sinceBroadcast = TimeSpan.Zero;
            BroadcastToAll(RobotProtocol.EncodeStatus(_machine.GetSnapshot()));
        }
    }

    private void BroadcastToAll(string payload)
    {
        foreach (var pair in _clients)
        {
            pair.Value.Outbox.Writer.TryWrite(payload);
        }
    }

    // ------------------------------------------------------------------
    // Per-client 處理
    // ------------------------------------------------------------------

    private async Task HandleClientAsync(Guid id, TcpClient client, Channel<string> outbox, CancellationToken token)
    {
        try
        {
            var writeLoop = Task.Run(() => WriteLoopAsync(client, outbox.Reader, token), CancellationToken.None);
            await ReadLoopAsync(client.GetStream(), outbox.Writer, token).ConfigureAwait(false);

            outbox.Writer.TryComplete();
            try
            {
                await writeLoop.ConfigureAwait(false);
            }
            catch
            {
                // 客戶端已斷線。
            }
        }
        catch (OperationCanceledException)
        {
            // 伺服器停止服務。
        }
        catch
        {
            // 單一客戶端的任何錯誤都不應該影響其他人。
        }
        finally
        {
            outbox.Writer.TryComplete();
            _clients.TryRemove(id, out _);
            try
            {
                client.Dispose();
            }
            catch
            {
                // 忽略關閉時期的例外。
            }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, ChannelWriter<string> writer, CancellationToken token)
    {
        var buffer = new byte[1024];
        var pending = new StringBuilder();

        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read <= 0)
            {
                return; // 客戶端離線
            }

            for (var i = 0; i < read; i++)
            {
                var character = (char)buffer[i];

                if (character == RobotProtocol.LineTerminator)
                {
                    var line = pending.ToString();
                    pending.Clear();

                    // 逐行處理（await）：同一條連線的指令順序一定不會亂。
                    await ProcessLineAsync(line, writer, token).ConfigureAwait(false);
                }
                else if (character != '\r')
                {
                    pending.Append(character);
                    if (pending.Length > RobotProtocol.MaxLineLength)
                    {
                        pending.Clear(); // 髒資料，直接丟掉重來
                    }
                }
            }
        }
    }

    private async Task ProcessLineAsync(string line, ChannelWriter<string> writer, CancellationToken token)
    {
        // 故障注入：先丟一條畸形封包，模擬現場的雜訊線路
        if (_options.MalformedPacketRate > 0 && Random.Shared.NextDouble() < _options.MalformedPacketRate)
        {
            await SendAsync(writer, "STATE:12.3,@@@,|MOVING|NONE|100.000|999", token).ConfigureAwait(false);
        }

        if (!RobotProtocol.TryParseCommand(line, out var command, out var reason))
        {
            await SendAsync(
                writer,
                RobotProtocol.EncodeResponse(RobotResponse.Fail(CommandType.Unknown, ErrorCode.MalformedPacket, reason ?? "無法解析的封包")),
                token).ConfigureAwait(false);
            return;
        }

        // 模擬機台收到指令後的處理延遲（真實機台會有排程 + 加減速運算）
        if (_options.CommandProcessDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.CommandProcessDelay, token).ConfigureAwait(false);
        }

        var response = _options.AckFailureRate > 0 && Random.Shared.NextDouble() < _options.AckFailureRate
            ? RobotResponse.Fail(command!.Type, ErrorCode.MachineBusy, "機台忙碌中（模擬故障注入）")
            : _machine.Execute(command!);

        await SendAsync(writer, RobotProtocol.EncodeResponse(response), token).ConfigureAwait(false);

        // 立刻再送一筆狀態，讓 UI 不用等下一個 100ms 就能看到 state 變成 MOVING。
        await SendAsync(writer, RobotProtocol.EncodeStatus(_machine.GetSnapshot()), token).ConfigureAwait(false);
    }

    private static async Task WriteLoopAsync(TcpClient client, ChannelReader<string> reader, CancellationToken token)
    {
        var stream = client.GetStream();

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var payload))
                {
                    var bytes = Encoding.ASCII.GetBytes(RobotProtocol.AppendTerminator(payload));
                    await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 伺服器停止服務。
        }
        catch (IOException)
        {
            // 客戶端強制斷線。
        }
        catch (ObjectDisposedException)
        {
            // Socket 已釋放。
        }
    }

    private static Task SendAsync(ChannelWriter<string> writer, string payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return writer.WriteAsync(payload, token).AsTask();
    }

    private static IPEndPoint CreateEndpoint(string host, int port)
    {
        var address = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Loopback;

        return new IPEndPoint(address, port);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _startStopGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record ConnectedClient(Guid Id, Channel<string> Outbox);
}

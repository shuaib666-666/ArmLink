using System.Net;
using System.Net.Sockets;
using System.Text;
using ArmLink.Core.Abstractions;
using ArmLink.Core.Communication;
using ArmLink.Core.Exceptions;
using ArmLink.Core.Models;
using ArmLink.Core.Protocol;
using ArmLink.Core.Simulation;

namespace ArmLink.Tests.Communication;

/// <summary>
/// 客戶端 ↔ Mock Server 的整合測試：真的開 Socket、真的重連、真的逾時。
/// 另外也驗證「壞封包只會被記錄，不會讓程式崩潰」這條最重要的穩健性要求。
/// </summary>
public class TcpRobotClientTests
{
    private static readonly List<TcpClient> AcceptedClients = new();

    // ---------------- 基本通訊 ----------------

    [Fact]
    public async Task Client_ReceivesStatusBroadcast_FromMockServer()
    {
        await using var server = new MockRobotServer(0);
        await server.StartAsync();

        await using var client = new TcpRobotClient("127.0.0.1", server.Port);
        var status = new TaskCompletionSource<RobotStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusReceived += (_, e) => status.TrySetResult(e.Status);

        await client.ConnectAsync();

        var completed = await Task.WhenAny(status.Task, Task.Delay(5000));
        Assert.Same(status.Task, completed);

        var snapshot = await status.Task;
        Assert.Equal(MachineState.Ready, snapshot.State);
        Assert.Equal(120, snapshot.Speed, 3); // 預設速度
    }

    [Fact]
    public async Task ExecuteAsync_Ping_ReturnsOk()
    {
        await using var server = new MockRobotServer(new MockRobotServerOptions { Port = 0, CommandProcessDelay = TimeSpan.Zero });
        await server.StartAsync();

        await using var client = new TcpRobotClient("127.0.0.1", server.Port);
        await client.ConnectAsync();

        var response = await client.ExecuteAsync(RobotCommand.Ping());

        Assert.True(response.IsSuccess);
        Assert.Equal(CommandType.Ping, response.Command);
        Assert.Equal("pong", response.Message);
    }

    [Fact]
    public async Task ExecuteAsync_Move_ActuallyDrivesTheMachine()
    {
        await using var server = new MockRobotServer(new MockRobotServerOptions { Port = 0, CommandProcessDelay = TimeSpan.Zero });
        await server.StartAsync();

        await using var client = new TcpRobotClient("127.0.0.1", server.Port);
        await client.ConnectAsync();

        await client.ExecuteAsync(RobotCommand.Move(new RobotPose(60, 0, 0)));

        // 100ms 廣播 + 120mm/s，半秒內一定到
        var snapshot = await WaitUntilAsync(
            () => client.LatestStatus is { State: MachineState.Ready } s && Math.Abs(s.Pose.X - 60) < 0.01 ? s : null,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(snapshot);
        Assert.Equal(60, snapshot!.Pose.X, 2);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedCommand_ThrowsCommandRejectedException()
    {
        await using var server = new MockRobotServer(new MockRobotServerOptions { Port = 0, CommandProcessDelay = TimeSpan.Zero });
        await server.StartAsync();

        await using var client = new TcpRobotClient("127.0.0.1", server.Port);
        await client.ConnectAsync();

        var exception = await Assert.ThrowsAsync<CommandRejectedException>(
            () => client.ExecuteAsync(RobotCommand.Move(new RobotPose(99999, 0, 0))));

        Assert.Equal(ErrorCode.CoordinateOutOfRange, exception.ErrorCode);
        Assert.Equal(CommandType.Move, exception.Command);
    }

    // ---------------- 逾時與壞封包防呆 ----------------

    [Fact]
    public async Task ExecuteAsync_ThrowsTimeout_WhenServerNeverReplies()
    {
        using var listener = StartListener(_ => { });
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var client = new TcpRobotClient(new RobotClientOptions
        {
            Host = "127.0.0.1",
            Port = port,
            DefaultCommandTimeout = TimeSpan.FromMilliseconds(300),
            AutoReconnect = false,
        });

        await client.ConnectAsync();

        await Assert.ThrowsAsync<RobotTimeoutException>(() => client.ExecuteAsync(RobotCommand.Ping()));
    }

    [Fact]
    public async Task Client_ReportsMalformedPacket_InsteadOfCrashing()
    {
        using var listener = StartListener(client =>
        {
            _ = Task.Run(async () =>
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("STATE:12.3,@@@,|MOVING|NONE|100.000|999\n"), CancellationToken.None);
                await stream.FlushAsync();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("GARBAGE_FROM_HELL\n"), CancellationToken.None);
                await stream.FlushAsync();
            });
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var client = new TcpRobotClient(new RobotClientOptions
        {
            Host = "127.0.0.1",
            Port = port,
            AutoReconnect = false,
        });

        var malformed = new TaskCompletionSource<MalformedPacketEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MalformedPacketReceived += (_, e) => malformed.TrySetResult(e);

        await client.ConnectAsync();

        var completed = await Task.WhenAny(malformed.Task, Task.Delay(5000));
        Assert.Same(malformed.Task, completed);

        Assert.NotNull(await malformed.Task);
        Assert.True(client.IsConnected); // 就算吃到垃圾也不能斷線或崩潰
    }

    [Fact]
    public async Task MockServer_WithFaultInjection_StillKeepsClientAlive()
    {
        await using var server = new MockRobotServer(new MockRobotServerOptions
        {
            Port = 0,
            MalformedPacketRate = 1.0, // 每筆指令都先丟一條壞封包
            AckFailureRate = 0,
            CommandProcessDelay = TimeSpan.Zero,
        });
        await server.StartAsync();

        await using var client = new TcpRobotClient("127.0.0.1", server.Port);
        var malformedCount = 0;
        client.MalformedPacketReceived += (_, _) => Interlocked.Increment(ref malformedCount);

        await client.ConnectAsync();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.ExecuteAsync(RobotCommand.Ping());
            Assert.True(response.IsSuccess);
        }

        Assert.True(Volatile.Read(ref malformedCount) >= 5);
    }

    // ---------------- 連線失敗與自動重連 ----------------

    [Fact]
    public async Task ConnectAsync_ThrowsCommunicationException_WhenNobodyIsListening()
    {
        await using var client = new TcpRobotClient(new RobotClientOptions
        {
            Host = "127.0.0.1",
            Port = PickUnusedPort(),
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            AutoReconnect = false,
        });

        await Assert.ThrowsAsync<RobotCommunicationException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Client_ReconnectsAutomatically_WhenServerComesBack()
    {
        const int port = 19119;

        await using var server = new MockRobotServer(port);
        await server.StartAsync();

        await using var client = new TcpRobotClient(new RobotClientOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Reconnect = new ReconnectPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(200),
                MaxDelay = TimeSpan.FromMilliseconds(400),
                UseJitter = false,
            },
        });

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var everDisconnected = false;
        client.ConnectionStateChanged += (_, e) =>
        {
            if (e.State == ClientConnectionState.Disconnected)
            {
                everDisconnected = true;
            }

            if (e.State == ClientConnectionState.Connected && Volatile.Read(ref everDisconnected))
            {
                reconnected.TrySetResult();
            }
        };

        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        // 模擬機台重啟
        await server.StopAsync();
        await server.StartAsync();

        var completed = await Task.WhenAny(reconnected.Task, Task.Delay(10_000));
        Assert.Same(reconnected.Task, completed);
        Assert.True(client.IsConnected);
    }

    // ---------------- 退避策略 ----------------

    [Fact]
    public void ReconnectPolicy_BacksOffExponentially_AndRespectsMaxDelay()
    {
        var policy = new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(15),
            BackoffFactor = 2,
            UseJitter = false,
        };

        Assert.Equal(1, policy.GetDelay(1).TotalSeconds, 6);
        Assert.Equal(2, policy.GetDelay(2).TotalSeconds, 6);
        Assert.Equal(4, policy.GetDelay(3).TotalSeconds, 6);
        Assert.Equal(15, policy.GetDelay(20).TotalSeconds, 6); // 被打平在上限
    }

    [Fact]
    public void ReconnectPolicy_UnlimitedByDefault_ButHonorsMaxAttempts()
    {
        Assert.True(ReconnectPolicy.Default.ShouldRetry(1000));

        var limited = new ReconnectPolicy { MaxAttempts = 3 };
        Assert.True(limited.ShouldRetry(2));
        Assert.False(limited.ShouldRetry(3));
    }

    // ---------------- 工具 ----------------

    private static async Task<T?> WaitUntilAsync<T>(Func<T?> probe, TimeSpan timeout)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = probe();
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(25);
        }

        return null;
    }

    private static TcpListener StartListener(Action<TcpClient> onAccepted)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return; // listener 已停止
                }

                lock (AcceptedClients)
                {
                    AcceptedClients.Add(client); // 留住參考，避免 GC 把連線回收掉
                }

                onAccepted(client);
            }
        });

        return listener;
    }

    private static int PickUnusedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

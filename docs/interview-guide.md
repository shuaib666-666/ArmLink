# ArmLink 面试教学手册

> 目标：让你能在面试中**用自己的话**讲清楚这个项目的每一个设计决策，并经得起追问。
> 建议用法：先读「电梯陈述」背熟，再按层读概念，最后刷 Q&A。每个概念都标注了对应的真实代码位置。

---

## 0. 电梯陈述（30 秒版，先背这段）

> "ArmLink 是一个虚拟机械手臂上位机，用 WPF + .NET 8 做的。核心价值是**不接真机台也能跑通整条工业控制链路**：我设计了一套基于 TCP 的 ASCII 行协议，写了一个完全异步的客户端（Channel 队列 + 自动重连 + 指数退避），内置一个带故障注入的 Mock Server，再用 75 个 xUnit 测试把协议、边界防护和真实 Socket 行为全部锁死。UI 严格走 MVVM，code-behind 只有 20 行。GitHub Actions 上每次 push 自动跑全部测试。"

面试官追问什么，就跳到对应章节。

---

## 1. 架构总览

```
ArmLink.sln
├── src/ArmLink.Core     （net8.0 类库：协议、客户端、模拟器、异常体系）
├── src/ArmLink.UI       （net8.0-windows WPF：只做展示和交互）
└── tests/ArmLink.Tests  （xUnit：75 个测试）
```

**为什么这样切？** 依赖方向是 `UI → Core ← Tests`，Core 不引用任何 UI 东西。
所以：
- 测试不用启动 WPF 就能测全部业务逻辑；
- Core 理论上跨平台（net8.0），只有 UI 锁在 Windows（WPF 限制）；
- 面试话术：「关注点分离——协议和通讯是纯逻辑，UI 只是它的一层皮，将来换 Avalonia 或 MAUI，Core 一行不用改。」

### 每层的职责

| 层 | 文件 | 职责 |
|---|---|---|
| 协议 | `Protocol/RobotProtocol.cs`（544 行） | 编码指令、解码响应，**全部 Try 模式** |
| 通讯 | `Communication/TcpRobotClient.cs`（654 行） | 连接、收发、组帧、重连、超时 |
| 模拟 | `Simulation/VirtualRobotMachine.cs` + `MockRobotServer.cs` | 纯状态机 + Socket 壳，可注入故障 |
| 模型 | `Models/` | RobotPose、RobotLimits（软限位）、状态快照 |
| 异常 | `Exceptions/` | RobotException 基类 + 4 个具体异常 |

---

## 2. 协议层：为什么是 ASCII 行协议，怎么防呆

### 概念：文本协议 vs 二进制协议

真机台（如 FANUC、ABB）常用二进制协议（定长包头 + CRC）。这个项目选 ASCII 行协议，理由：
1. **可读性**——日志里直接看到 `MOVE:100,50,200`，调试效率高；
2. **实现聚焦**——面试项目的重点是通讯健壮性和测试，不是字节序和 CRC；
3. **可扩展**——行结构定了，加指令只是加关键字。

### 帧格式（背下来）

```
指令：  MOVE:X,Y,Z\n     JOG:dX,dY,dZ\n     HOME\n     ESTOP\n     SPEED:500\n     PING\n
响应：  OK:MOVE|accepted\n
        ERR:MOVE|COORDINATE_OUT_OF_RANGE|X axis out of range\n
广播：  STATE:100.0,50.0,200.0|MANUAL|READY|NONE|500|42\n
```

三层分隔符：`\n` 结帧、`|` 分字段、`,` 分坐标分量。

### 核心设计：Try 模式解析（面试最常问的点）

```csharp
public bool TryDecodeResponse(string line, out RobotResponse response, out string? error)
```

**为什么不用 `Parse` + 异常？** 坏封包在工业现场是**常态**不是异常：线缆干扰、半包、粘包、人为乱敲。如果每个坏包都抛异常，异常处理就成了正常流程，性能差且容易漏。Try 模式下坏数据只是 `false + 原因`，**坏数据永远不可能让程序崩溃**。

### 五道防呆防线（RobotProtocol.cs，对应 24 个损坏封包测试）

1. **长度上限** `MaxLineLength = 1024`——防恶意/损坏数据流撑爆内存；
2. **控制字符检查**——拒绝 `\n` 之外的控制字符，防二进制垃圾混入；
3. **字段数校验**——每个关键字固定字段数，多了少了都不行；
4. **数值解析用 `double.TryParse` + `InvariantCulture`**——拒绝 NaN/Infinity，且不依赖系统区域设置（中文系统的逗号小数点问题）；
5. **未知关键字 → `CommandType.Unknown`** 而不是抛异常，交由上层决定回 `ERR:UNKNOWN_COMMAND`。

---

## 3. 客户端：异步通讯的六个核心概念

这张图先看懂，后面的概念都是围绕它讲的：

（图见对话中——UI 按钮 → Channel 队列 → WritePump → Socket → MockServer → ReadLoop 组帧 → TCS 配对回 UI）

### 3.1 为什么「全异步」？

UI 线程（WPF 的 STA 线程）绝不能被网络 IO 阻塞，否则界面冻结、Windows 报「未响应」。所以从 `ConnectAsync` 到 `stream.WriteAsync` 全链路 `async/await`，没有一个 `.Result` 或 `.Wait()`。
**追问「await 到底做了什么」**：await 把方法切成一个状态机，IO 完成后由线程池恢复执行，等待期间线程被释放回去干活。

### 3.2 `Channel<T>` 发送队列（TcpRobotClient.cs 第 26 行）

```csharp
private readonly Channel<PendingRequest> _outbox = Channel.CreateUnbounded<PendingRequest>(
    new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
```

- 点按钮 = 往 Channel 里 `TryWrite` 一个请求就返回，**UI 永远不卡**；
- `WritePumpAsync` 是唯一的读取者（`SingleReader`），逐条写 Socket；
- Channel 是 .NET 专属的高性能生产者-消费者结构，无锁设计。
- **面试话术**：「我不让 UI 直接碰 Socket，中间隔一层队列，把『用户操作的速度』和『网络的速度』解耦。」

### 3.3 请求-响应用 `TaskCompletionSource` 配对

TCP 是流，响应不保证按请求顺序吗？——这个协议里服务端是串行应答的，所以客户端用 **FIFO 的 `ConcurrentQueue<TaskCompletionSource>`** 配对：发出需要响应的指令前，先把它的 TCS 排进队尾；收到一行响应，就从队头取一个 TCS `TrySetResult`。
关键细节：**先排队再发出去**（WritePump 里第 400 行注释），防止响应比请求更早被处理导致错配。

### 3.4 超时：`Task.WhenAny` 模式

```csharp
var finished = await Task.WhenAny(completion.Task, timeoutTask);
if (finished == timeoutTask)
    completion.TrySetException(new RobotTimeoutException(...));
```
谁先完成就处理谁；超时后给 TCS 塞异常，等待方立刻醒来。**不抛 TimeoutException 而抛 `RobotTimeoutException`**——业务异常体系统一，调用方一个 catch 就能接住全部。

### 3.5 自动重连：单一 Session 循环 + 指数退避 + Jitter

```
while (没被取消)
{
    连接 → 成功: attempt 归零, 跑 IO → 断线
                失败: attempt++
    若 AutoReconnect 且 attempt ≤ MaxAttempts:
        delay = GetDelay(attempt)   // 指数退避：1s, 2s, 4s, 8s... 封顶
        Task.Delay(delay) → 回到开头
}
```

- **指数退避（exponential backoff）**：每次重试等待时间翻倍，避免服务端刚崩溃就被一堆客户端瞬间冲垮；
- **Jitter（随机抖动）**：在退避时间上加随机偏移，避免多个客户端「同步」重试形成惊群（thundering herd）；
- **单一循环**管「连接 + IO + 重连」三件事，而不是三个后台任务互相抢状态——面试可讲：状态机只有一个，就不会有竞态；
- **第一次连接失败**会把异常传回 `ConnectAsync`（UI 能立刻显示「连不上」）；**之后的失败**归自动重连管，不再打扰调用方——这个分工是设计亮点。

### 3.6 「绝不崩溃」的三层兜底

1. Session 循环最外层还有个 catch-all：**背景循环永远不能因未预期例外挂掉整个进程**（第 200 行注释，工控程序的基本教养）；
2. 断线时 `FailPendingRequests`——所有还在等响应的请求立刻收到异常，不会有人挂着一个 Task 永远等下去；
3. 状态变化全部通过 `ConnectionStateChanged` / `MalformedPacketReceived` 事件往外抛，调用方决定怎么展示。

---

## 4. 模拟器：为什么拆成两个类

- **`VirtualRobotMachine`（纯状态机）**：只管运动逻辑——插值移动、软限位检查、急停/复位，**没有任何 IO**。构造时传入坐标和速度，`Execute(command)` 返回响应。因为无 IO，**单元测试毫秒级跑完**，不用开 Socket。
- **`MockRobotServer`（Socket 壳）**：TcpListener 接受连接 → 解析指令 → 喂给状态机 → 按 100ms 定时器广播 `STATE:` 快照。另支持**故障注入**：`MalformedPacketRate`（随机发坏包）、`AckFailureRate`（随机不回 ACK）——用来验证客户端的防呆和超时真的有效，而不是只测「顺路」的情况。

**面试话术**：「逻辑和传输分离，是我在这个项目里最坚持的原则。故障注入让我的测试覆盖了真实现场才会遇到的脏数据。」

### 软限位（software limits）

`RobotLimits` 定义 X/Y/Z 行程范围（±500 / 0~600 mm），状态机在执行前检查目标坐标，超出就回 `ERR:MOVE|COORDINATE_OUT_OF_RANGE`。**这是安全概念**：真机台上软限位是撞硬限位开关前的最后一道保护，面试官若是工控背景会认这个。

---

## 5. MVVM：三个值得讲的点

### 5.1 CommunityToolkit.Mvvm 的源码生成器

```csharp
[ObservableProperty]
private string _host = "127.0.0.1";   // 自动生成 public string Host { get; set; } 并触发 PropertyChanged
[RelayCommand]
private Task ConnectAsync() { ... }    // 自动生成 ConnectCommand
```
不用手写 `OnPropertyChanged` 样板代码。**追问「MVVM 三件套」**：Model（Core 里的模型）、View（XAML）、ViewModel（属性 + 命令），View 只做绑定，ViewModel 不知道 View 存在。

### 5.2 跨线程 UI 更新

Socket 回调来自线程池线程，直接改绑定属性会抛 `InvalidOperationException`（WPF 只允许 UI 线程碰 UI 元素）。方案：`IUiDispatcher` 接口包一层，实现里用 WPF 的 `Dispatcher` 切回 UI 线程再更新。
**为什么做成接口？**——ViewModel 里不直接引用 WPF 类型，单测里可以注入假的同步 Dispatcher。这就是「依赖注入让可测试性变好」的具体例子。

### 5.3 Code-behind 只有 20 行

`MainWindow.xaml.cs` 只有 `InitializeComponent()` 和 `DataContext = new MainViewModel()`。
连「日志自动滚动」这种看似必须碰控件的需求，都做成了**附加行为（Attached Behavior）** `AutoScrollBehavior`——MVVM 的精神不是教条，是「View 里不写逻辑，逻辑要么进 ViewModel、要么进可复用的行为」。

---

## 6. 测试：75 个测试怎么组织

| 套件 | 覆盖 | 亮点 |
|---|---|---|
| `RobotProtocolTests` | 编解码往返、24 种损坏封包 | 坏包矩阵：空串/超长/控制字符/NaN/字段缺失/未知关键字… |
| `RobotLimitsTests` | 软限位边界 | 上界、下界、恰好压线（边界值分析） |
| `VirtualRobotMachineTests` | 状态机 | 移动插值、急停后拒绝指令、RESET 复归 |
| `TcpRobotClientTests` | **真实 Socket 集成测试** | 随机端口开 MockServer，测广播接收、ACK 超时、坏包事件、**杀掉 server 再重启后客户端自动重连成功** |

**追问「单元测试 vs 集成测试」**：前三个是纯逻辑单测（毫秒级）；最后一个是把真实 TCP 栈跑起来的集成测试——真实世界的断线重连问题只有真 Socket 才能测出来。

**追问「怎么测异步」**：测试里用 `await` 等真实完成 + 有限超时保护，不 `Thread.Sleep` 猜时间，避免 CI 上的偶发失败（flaky test）。

---

## 7. 面试 Q&A 快答（15 题）

1. **为什么不用 JSON 协议？** 行协议更轻，解析可控；JSON 引入第三方依赖且解析失败模式不透明。关键是不管用什么格式，防呆逻辑都在协议层封死。
2. **为什么不用 gRPC / WebSocket？** 工控现场常见的是裸 TCP/串口，TCP 是最小公分母，最贴近面试目标（上位机开发）。
3. **粘包/半包怎么处理？** TCP 是字节流没有消息边界。读取循环累积到 buffer，按 `\n` 切行，残包留在 buffer 等下次拼接。
4. **为什么 Channel 不用 BlockingCollection？** Channel 是纯异步 API，`await` 等待不占线程；BlockingCollection 的阻塞等待会占住一个线程。
5. **TCS 为什么传 `RunContinuationsAsynchronously`？** 防止 `TrySetResult` 把 continuation 同步跑到调用线程上，造成死锁或意外重入。
6. **`ConfigureAwait(false)` 为什么到处都是？** Core 是类库不该依赖 UI 上下文；避免 await 后强行回到 UI 线程（也避免死锁）。
7. **自动重连怎么防止无限重试？** `MaxAttempts` 上限 + 每次失败递增 attempt，成功连接后 attempt 归零。
8. **急停（E-Stop）在设计上有什么特殊？** 最高优先级指令；急停后机器进 `EmergencyStop` 状态，普通指令被拒绝（`EMERGENCY_STOP_ACTIVE`），必须 RESET 才能恢复——安全状态机。
9. **如果两个线程同时发指令会怎样？** 都只是写 Channel，线程安全；真正写 Socket 的只有 WritePump 一个消费者，天然串行。
10. **`volatile` 用在哪、为什么？** `_state` 和 `_latestStatus` 被多线程读写，volatile 保证可见性（不需要原子性复杂操作，读个最新值就够）。
11. **软限位为什么在 Core 检查而不是 UI？** UI 只是客户端之一；协议层、模拟器、任何第三方客户端都必须被同一道防线保护。安全检查要放在离机器最近的地方。
12. **怎么模拟脏网络？** MockServer 故障注入（随机坏包 / 随机不 ACK），这是比「正常路径测试」有价值得多的测试。
13. **CI 怎么配的？** GitHub Actions，windows-latest（WPF 必须Windows），restore → build Release → dotnet test，约 1 分钟全绿。
14. **项目最难的 bug？** 建议讲真实经历：如响应配对错配风险（先排队再发送解决）或 DPI 虚拟化导致的截图坐标错乱（讲排查过程：怀疑→假设→验证）。
15. **下一步会怎么扩展？** 传输层抽象接口 + Serial 实现（证明可替换）；轨迹录制回放；3D 可视化。说得出路线图 = 有架构 ownership。

---

## 8. 万一被问到不会的

诚实 + 展示思路：「这块我目前用 X 方案实现，我知道它在 Y 场景下有局限，如果要解决我倾向 Z。」面试官考的是工程判断，不是八股背诵。

## 9. 面试前 checklist

- [ ] 电梯陈述能脱口而出
- [ ] 帧格式三种报文能默写
- [ ] Channel → WritePump → ReadLoop → TCS 这条数据流能白板画出来
- [ ] 指数退避 + Jitter 的动机说得清（防惊群）
- [ ] Try 模式 + 24 个坏包测试这个组合能讲 2 分钟
- [ ] 打开 GitHub repo 现场演示：截图、README、绿色 CI badge

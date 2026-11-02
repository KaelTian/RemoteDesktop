# RemoteDesktop（.NET 10 + DXGI Desktop Duplication / GDI 兜底）

可运行的远程桌面原型：被控端截屏推 JPEG 帧，控制端 WPF 渲染画面并回传鼠标/键盘/滚轮输入。

采集后端自动选择：优先 DXGI Desktop Duplication（物理/控制台会话，开销最低），不可用时自动回退到 GDI BitBlt（RDP、虚拟显示等场景仍可工作）。

## 项目结构

```
RemoteDesktop.sln
├─ Remote.Agent       被控端（控制台程序）
│   ├─ Program.cs         参数解析 + DPI 感知
│   ├─ IScreenCapture.cs  采集接口 + 协议常量
│   ├─ CaptureFactory.cs  DXGI 优先、GDI 兜底
│   ├─ DxgiCapture.cs     DXGI Desktop Duplication + JPEG
│   ├─ GdiCapture.cs      GDI BitBlt 兜底 + JPEG
│   ├─ CursorRenderer.cs  把系统光标画进帧（DXGI/GDI 共用）
│   ├─ AgentServer.cs     握手/鉴权 + 每连接推帧循环 + ReceiveInputAsync + InputTracker
│   ├─ InputInjector.cs   SendInput 注入（鼠标/键盘/滚轮，含扩展键）
│   ├─ DesktopInfo.cs     会话/输入桌面/完整性级别探测（排查用）
│   └─ Log.cs             滚动文件日志
└─ Remote.Client          控制端（WPF 集控）
    ├─ App.xaml(.cs)         全局异常日志
    ├─ RemoteSession.cs      单机会话：握手 / 收帧解码 / 输入 / 流设置
    ├─ SessionView.xaml(.cs) 网格单元：持有最近一帧 + WriteableBitmap 复用
    ├─ MainWindow.xaml(.cs)  多机网格 + 选中放大 + 坐标映射 + 输入路由
    └─ Log.cs                滚动文件日志
```

## 架构与数据流

> 命名以代码为准：**Agent = 被控端**（监听端口，TCP 服务端），**Client = 控制端**（主动连接，TCP 客户端）。下文所有方向都按这个命名。

### 链路总览

```text
控制端 Client（TCP 客户端 / WPF）               被控端 Agent（TCP 服务端 / 控制台）
┌────────────────────────────────┐             ┌──────────────────────────────────┐
│ RemoteSession                  │             │ AgentServer                      │
│  ├─ 握手 + 鉴权                │  ① 握手     │  ├─ 握手 + 鉴权                  │
│  ├─ ReceiveLoop   收 JPEG 帧   │ ←─────────→ │  ├─ 推帧循环 CaptureJpeg         │
│  ├─ Send...       发输入指令   │  ② 推帧     │  ├─ ReceiveInputAsync  收指令    │
│  └─ SetStream     流设置       │  ③ 指令     │  └─ IScreenCapture / SendInput   │
└────────────────────────────────┘             └──────────────────────────────────┘
```

同一条 TCP 连接上全双工：一个方向循环推帧，另一个方向循环发输入，互不阻塞。

### 采集后端：DXGI vs GDI（核心差异）

两个后端实现同一个 `IScreenCapture.CaptureJpeg(quality)`，`AgentServer` 不感知后端。**核心差异只在“一帧 BGRA 像素从哪里来”**；拿到像素之后，光标绘制、JPEG 编码、TCP 发送完全共用同一条路径。

| 维度 | DXGI Desktop Duplication（优先） | GDI BitBlt（兜底） |
|---|---|---|
| 像素来源 | DWM 合成器输出的 GPU 纹理 | 屏幕 DC 的像素拷贝 |
| 是否整屏拷贝 | 否：GPU `CopyResource` 到 staging，再 `Map` 下采 | 是：每个采样周期都 `BitBlt` 整屏 |
| 新帧语义 | `AcquireNextFrame` 只有桌面变化才返回新帧，15ms 超时返回 null | 每周期都产出一帧，与是否变化无关 |
| 长期资源 | duplication / staging / D3D11 device，需要处理失效重建 | 复用 Bitmap + Graphics，失败后退避 1 秒 |
| 典型开销 | 空闲时几乎为 0，CPU 拷贝只发生在有变化时 | 每帧固定 CPU 拷贝，开销明显更高 |
| 多显示器 | 只采第一个 `AttachedToDesktop` 输出 | 采整个虚拟桌面（可拼接、原点可为负） |
| RDP/远程会话 | 不支持（`E_ACCESSDENIED`），自动回退 GDI | 可用（会话保持连接时） |
| 锁屏 / UAC 安全桌面 | 接口失效，需销毁重建；期间无画面 | BitBlt 失败并退避；期间无画面 |

#### DXGI 链路（优先）

1. `CreateDXGIFactory1` 枚举 adapter/output，选第一个 `AttachedToDesktop` 的输出（不能假设“适配器 0 + 输出 0”，混合显卡/多屏会选错）。
2. `D3D11CreateDevice`：传入 adapter 时 `DriverType` 必须是 `Unknown`，必须带 `BgraSupport`，FeatureLevel 11.0 → 10.1 → 10.0 依次尝试。
3. `IDXGIOutput1.DuplicateOutput(device)` 得到 `IDXGIOutputDuplication`。
4. 每次采集调用 `AcquireNextFrame(15ms)` 等新帧，拿到 `IDXGIResource`（GPU 纹理）。
5. `CopyResource` 到 CPU 可读的 staging texture，`Map` 后按 `RowPitch` 逐行拷出 BGRA 像素，然后 `ReleaseFrame()`。
6. 像素 `Marshal.Copy` 进 `Bitmap`，再由 `CursorRenderer` 画光标、按 quality 编码 JPEG。

DXGI 的 duplication 是有生命周期的，以下情况都会失效，必须**销毁 duplication/device 后重建**（1 秒冷却，避免空转）：

- `DXGI_ERROR_ACCESS_LOST`：锁屏、切会话、桌面类型变化
- `DXGI_ERROR_DEVICE_REMOVED / DEVICE_RESET`：显卡驱动重置
- `DXGI_ERROR_SESSION_DISCONNECTED`：会话断开
- `DXGI_ERROR_INVALID_CALL (0x887A0001)`：UAC 安全桌面切换时实测返回它（文档写的是 ACCESS_LOST）；另外切桌面瞬间最后一帧 `ReleaseFrame` 失败，也会让 DWM 认为上一帧没释放，后续一直报 INVALID_CALL。**这个错误也必须触发重建**，否则 `_duplication` 永远非 null，采集会永久冻结到重启 Agent。

> 安全桌面期间本来就不允许截屏（DXGI 和 GDI 都看不到 UAC 弹窗），区别是 GDI 结束后自然恢复，DXGI 必须重建 duplication 才能恢复。

`auto` 模式的 GDI 回退只发生在 Agent **启动时**；运行中 DXGI 失效走上面的重建流程，不会热切换到 GDI。

#### GDI 链路（兜底）

1. `GetSystemMetrics(SM_*VIRTUALSCREEN)` 取整个虚拟桌面范围（多屏拼接，原点可能为负）。
2. `GetDC(NULL)` 拿屏幕 DC，`BitBlt` 到内存 Bitmap 的 DC，flags 为 `SRCCOPY | CAPTUREBLT`。
3. 直接在 Bitmap DC 上调用 `CursorRenderer.Draw` 画光标，然后按 quality 存成 JPEG。
4. 复用 Bitmap/Graphics；桌面尺寸变化时重建；`BitBlt` 失败后 1 秒退避（锁屏/RDP 断开时返回 `Win32Error=5`）。

### 方向一：Agent → Client（推帧）

Agent 每个连接一个推帧循环（`AgentServer.HandleClientAsync`）：

1. 取本连接独立的 `StreamSettings`；`StreamSettings` 默认 25fps / quality 70，但控制端连上后会立刻发 `type=5` 改成网格模式 3fps / 60。
2. 调 `_capture.CaptureJpeg(quality)`：DXGI 超时或 GDI 失败返回 `null`，本周期直接跳过。
3. 拿到 JPEG 后通过 `WriteAsync` + `SemaphoreSlim writeLock` 串行写入：先 `jpeg.Length(int32)`，再 `jpegBytes`，然后 `Flush`。
4. 按 `1000 / fps` 的间隔补 `Task.Delay`，避免空转。
5. 每 5 秒输出一条统计（frames / bytes / avgFps / nullCapture）。

多个控制端连接共享同一个 `IScreenCapture` 实例；`DxgiCapture` / `GdiCapture` 内部都有 `lock (_sync)`，保证同一时刻只有一帧在拷贝/编码。

Client 侧 `RemoteSession.ReceiveLoop` 在后台 Task 中循环：`ReadInt32` 读长度 → 校验 `0 < length <= 100MB` → `ReadBytes(length)` 读满并校验 → JPEG 解码成 `Bgra32` 的 `BitmapSource`（`Freeze()`）→ 更新尺寸 → 触发 `FrameReady`。`MainWindow` 再切回 UI 线程，`SessionView` 用复用的 `WriteableBitmap` 更新网格画面，选中的那台同时更新放大视图。

### 方向二：Client → Agent（指令）

Client 侧 `RemoteSession` 每个方法都是一条固定结构的消息，统一走 `Send(Action<BinaryWriter>)`：持有 `_sendLock` 写完并 `Flush`，避免多线程把一条消息写到一半。

- `type=1` 鼠标移动 → `MoveTo(x, y)`
- `type=2` 鼠标按键 → `Button(button, down, x, y)`，`x=y=-1` 表示只发按键不移动光标（丢焦/断线时释放按键）
- `type=3` 键盘 → `Key(vkCode, down)`
- `type=4` 滚轮 → `Wheel(delta)`
- `type=5` 流设置 → 更新该连接的 fps / quality

鼠标坐标是相对采集区域左上角的像素坐标：`MainWindow.TryMapToSelected` 先按等比缩放 + 黑边计算把窗口坐标映射回帧坐标，鼠标移动另外做 8ms 节流。Agent 侧 `ToScreenX/Y` 再加上采集区域的偏移并裁剪到边界。

Agent 侧 `ReceiveInputAsync` 在独立 Task 中运行，先读 `type(byte)`，再按该类型**完全相同的顺序**读后续字段，然后 `switch` 分派给 `InputInjector`（`SendInput`）。`InputTracker` 记录当前仍按下的键/按钮，连接断开时 `ReleaseAll()` 统一补发抬起，避免远端卡键。

### 重点：两个方向用的是同一套读写机制

不要把推帧和指令看成两套协议——它们只是同一条规则下的两种消息：

- 双向都用 `BinaryWriter` / `BinaryReader`，在同一个 TCP 字节流上按**发送顺序 = 接收顺序**读写。
- `BinaryWriter` 的编码是固定的：`int32` → 4 字节小端、`bool` → 1 字节、`byte[]` → 原样字节（不带长度前缀）。
- 读端用 `ReadInt32()` / `ReadBoolean()` / `ReadBytes(n)` 精确读满对应字节，**没有任何地方假设“一次 Read 就是一条完整消息”**。

```text
推帧（Agent → Client，循环）
[ jpegLength:int32 ][ jpegBytes... ] [ jpegLength:int32 ][ jpegBytes... ] ...

指令（Client → Agent，按 type 区分）
[ type:byte=1 ][ x:int32 ][ y:int32 ]
[ type:byte=2 ][ button:int32 ][ down:bool ][ x:int32 ][ y:int32 ]
[ type:byte=3 ][ vkCode:int32 ][ down:bool ]
[ type:byte=4 ][ delta:int32 ]
[ type:byte=5 ][ fps:int32 ][ quality:int32 ]
```

因此 TCP 的粘包/拆包对协议没有影响：怎么分片都由 `BinaryReader` 在内部循环读满。真正需要保证的是两端协议一致——字段顺序、类型、字节数任何一边改了，另一边必须同步改，否则现象会很像“粘包”（实际是字节错位）。

写端串行化（Client 的 `_sendLock`、Agent 的 `writeLock`）保证一条消息不会被交错；读端每个方向各有一个独立循环，全双工互不干扰。完整的字段级定义见下文《协议》表。

### 线程模型

- **Agent**：`AcceptTcpClientAsync` 接受连接，每个客户端一个 `HandleClientAsync`；其中推帧循环是主流程，`ReceiveInputAsync` 另起一个 Task 专门读指令。采集实例全局共享，内部加锁。
- **Client**：`ConnectAsync` 完成握手后，`Task.Run(ReceiveLoop)` 在后台收帧；`FrameReady` 在后台线程触发，由 `MainWindow` 用 `Dispatcher.BeginInvoke` 切回 UI；输入发送由 `_sendLock` 串行化。
- **断开**：Client 取消 CTS + 关闭 `TcpClient`；Agent 侧读/写抛异常后进入 `finally`，`InputTracker.ReleaseAll()` 释放仍按下的键和鼠标键。

## 运行要求

- Windows 10 / 11
- .NET 10 SDK（目标机器若没装 SDK，需要安装 .NET 10 Desktop Runtime）
- 首次构建需要联网还原 NuGet 包（`Vortice.DXGI`、`Vortice.Direct3D11`、`System.Drawing.Common`）

> 部署时**不要只复制 exe**：必须带上整个输出目录里的 DLL（或 `dotnet publish -c Release -r win-x64 --self-contained` 生成自包含目录），否则会因缺少依赖直接闪退，且来不及写日志。

## 使用方法

**被控端**（目标机器，需在已登录的交互式会话中运行）：

```powershell
# 自动选择采集后端，设置访问口令
dotnet run --project Remote.Agent -- --token 你的口令

# 可选参数
#   --port 33899          监听端口
#   --capture auto|dxgi|gdi
#   --token <口令>        不设置则启动时打印安全警告
```

首次运行记得放行防火墙端口 33899（或先在本机用 127.0.0.1 测试）。

**控制端（集控）**：

```powershell
dotnet run --project Remote.Client
```

目标框支持多台机器，用逗号/空格分隔，可写 `IP:端口`（默认 33899）：

```text
192.168.1.10, 192.168.1.11, 192.168.1.12:33999
```

点「连接全部」后每台机器占一个网格画面。网格默认 3fps、质量 60，只做监控省带宽；**点击某一格**放大到全尺寸并切到 25fps、质量 75，此时鼠标/键盘直接操作该机器；点「返回网格」降回低帧率。窗口失焦会自动释放按住的键，避免远端卡键。

## 协议

均为小端。

| 方向 | 格式 |
|---|---|
| 客户端→服务端 | `magic(uint32=0x52444D31)` + `tokenLength(int32)` + `token(UTF-8 bytes)` |
| 服务端→客户端 | `status(byte, 0=OK/1=口令错误)`；OK 时后跟 `width(int32) height(int32)` |
| 服务端→客户端 | `jpegLength(int32) + jpegBytes`（循环推帧） |
| 客户端→服务端 | `type=1` 鼠标移动：`x(int32) y(int32)` |
| 客户端→服务端 | `type=2` 鼠标按键：`button(int32,0左/1右/2中) down(bool) x(int32) y(int32)`；`x=y=-1` 表示只发按键不移动光标 |
| 客户端→服务端 | `type=3` 键盘：`vkCode(int32) down(bool)` |
| 客户端→服务端 | `type=4` 滚轮：`delta(int32)` |
| 客户端→服务端 | `type=5` 流设置：`fps(int32) quality(int32)`（每连接独立，1-60fps / 30-95 质量） |

鼠标坐标相对采集区域左上角，服务端按采集区域（可能带负偏移的多屏坐标）平移并裁剪。

## 排查 / 日志

两端都会写持久化日志（单文件超过 4MB 自动轮换）：

- 被控端：`%LOCALAPPDATA%\RemoteDesktop\agent.log`
- 控制端：`%LOCALAPPDATA%\RemoteDesktop\client.log`

Agent 启动时会打印日志路径。双击启动失败时窗口会停住显示错误（从终端启动则不阻塞）。

| 现象 | 日志线索 | 原因 / 处理 |
|---|---|---|
| 双击 exe 闪退 | `Agent fatal` + 异常栈，或 `端口 ... 已被占用` | 旧 Agent 占用端口，先结束旧进程；其余看堆栈 |
| Agent 启动即退出 | `无法获取桌面尺寸` | 会话切换/RDP 重连瞬间尺寸为 0；新版已改为占位尺寸继续运行 |
| 客户端“已连接”但黑屏 | Agent：`BitBlt ... Win32Error=5` / `DXGI ... 0x80070005` | 被控端锁屏、RDP 已断开/最小化，或处于安全桌面；保持会话解锁连接 |
| 客户端“已连接但 5 秒内未收到画面” | Agent 端没有 `first frame` | 同上，对照 Agent 日志 |
| Agent 日志 `bad magic` | 客户端连到了旧版 Agent | 结束旧 Agent，确保两端都是最新构建 |
| 输入无效 | Agent：`SendInput/SetCursorPos ... Win32Error=0` 且 `sendInputProbe=False`，前台与 Agent 同级或更低 | **安全软件拦截模拟输入**：360 的“模拟按键防护/远控防护”、火绒等会静默丢弃 `SendInput`/`PostMessage`（`GetLastError` 为 0）。把 `Remote.Agent.exe` 及发布目录加入安全软件信任，重启 Agent，启动日志 `sendInputProbe=True` 才算恢复 |
| 输入无效 | Agent：`SendInput ... Win32Error=0` 且 `target='... integrity=High'` | **UIPI 拦截**（拦截时不设置错误码）；以管理员身份运行 Agent |
| 输入无效 | Agent：`SendInput ... Win32Error=5` | UIPI（目标窗口以管理员运行）或输入桌面不是 Default（锁屏）；以相同权限运行 Agent |
| 输入无效但没有失败日志 | Agent 日志没有 `first input packet` | 客户端没发送（画面未获得键盘焦点），或协议不匹配 |
| 控制到了错误的屏幕/会话 | Agent 日志 `session=2 activeConsole=1` | Agent 只控制它所在会话；要控制物理桌面需在控制台会话运行 |

Agent 启动日志会记录关键环境：`session`、`activeConsole`、`integrity`（Agent 自身权限级别）、`inputDesktop`（`Default` 正常，`Winlogon` = 锁屏/UAC 安全桌面）、采集后端、分辨率、exe 路径与构建时间。每次点击还会记录 `target='窗口标题' (进程, integrity=..., class=...)`，即点击坐标下真正接收鼠标的窗口及其权限级别——如果它是 `High` 而 Agent 是 `Medium`，点击会被 UIPI 丢弃。

启动日志里的 `sendInputProbe` 是本地注入探测结果：`False` 表示 Agent 启动时就无法注入，优先排查安全软件（360 模拟按键防护等）和权限级别，而不是采集或网络问题。

## 已知限制

- **未加密**：口令只做明文比对，传输内容（含画面）可被嗅探；**仅限可信局域网**。公网请自行加 TLS/VPN。
- **DXGI 不支持 RDP/远程会话**（会得到 `E_ACCESSDENIED`）：此时自动回退到 GDI。但 RDP 会话被断开/最小化/锁屏时，GDI 的 BitBlt 也会返回拒绝访问（日志 `Win32Error=5`），此时无法截屏；必须保持会话解锁连接。
- 被控端必须是已登录、未锁屏的交互式会话；UAC 安全桌面会黑屏。
- 被控端以普通权限运行时，无法操作以管理员权限运行的窗口（Windows UIPI 完整性级别限制），例如前台是以管理员打开的 PowerShell、任务管理器；此时需要用管理员权限运行 Agent。
- **安全软件会拦截模拟输入**：360 安全卫士的“模拟按键防护/远控防护”、火绒等会静默丢弃 Agent 的 `SendInput`（日志 `Win32Error=0`、启动 `sendInputProbe=False`），表现为画面正常但鼠标键盘无效。部署时必须把 `Remote.Agent.exe` 和发布目录加入安全软件信任列表，不要只临时关闭防护。
- DXGI 只采集第一个连接到桌面的输出（单显示器）；GDI 采集整个虚拟桌面（多显示器拼接）。
- 仅支持 32bpp BGRA 桌面格式；HDR/10bit 桌面会抛 `NotSupportedException`（需先做格式转换）。
- 整屏 JPEG 编码，带宽较高（1080p 约 200KB/帧）；未做脏矩形/增量，公网/低带宽建议后续升级 H.264 或 Windows.Graphics.Capture。集控网格靠 `type=5` 把每路降到 3fps 控制总带宽。
- 集控端对同一台机器**一机一连接**：网格和放大复用同一会话。不要对同一 Agent 重复建连，多连接会共享采集并互相抢帧。
- 暂不支持自动重连：连接断开后需要重新点「连接全部」（已连接的机器会跳过）。

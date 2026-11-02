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
│   ├─ AgentServer.cs     握手/鉴权 + TCP 推帧 + 收输入
│   └─ InputInjector.cs   SendInput 注入（扩展键/滚轮）
└─ Remote.Client      控制端（WPF）
    ├─ App.xaml(.cs)        全局异常日志
    └─ MainWindow.xaml(.cs) 连接/鉴权 UI + WriteableBitmap 渲染 + 输入
```

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

**控制端**：

```powershell
dotnet run --project Remote.Client
```

界面里填目标 IP 和口令，点「连接」。画面区域支持鼠标移动/左右中键、滚轮、键盘；窗口失焦会自动释放按住的键，避免远端卡键。

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
| 输入无效 | Agent：`SendInput ... Win32Error=0` | **UIPI 拦截的典型表现**（拦截时不设置错误码）；看同条日志的 `target='...' integrity=High`，以管理员身份运行 Agent |
| 输入无效 | Agent：`SendInput ... Win32Error=5` | UIPI（目标窗口以管理员运行）或输入桌面不是 Default（锁屏）；以相同权限运行 Agent |
| 输入无效但没有失败日志 | Agent 日志没有 `first input packet` | 客户端没发送（画面未获得键盘焦点），或协议不匹配 |
| 控制到了错误的屏幕/会话 | Agent 日志 `session=2 activeConsole=1` | Agent 只控制它所在会话；要控制物理桌面需在控制台会话运行 |

Agent 启动日志会记录关键环境：`session`、`activeConsole`、`integrity`（Agent 自身权限级别）、`inputDesktop`（`Default` 正常，`Winlogon` = 锁屏/UAC 安全桌面）、采集后端、分辨率、exe 路径与构建时间。每次点击还会记录 `target='窗口标题' (进程, integrity=..., class=...)`，即点击坐标下真正接收鼠标的窗口及其权限级别——如果它是 `High` 而 Agent 是 `Medium`，点击会被 UIPI 丢弃。

## 已知限制

- **未加密**：口令只做明文比对，传输内容（含画面）可被嗅探；**仅限可信局域网**。公网请自行加 TLS/VPN。
- **DXGI 不支持 RDP/远程会话**（会得到 `E_ACCESSDENIED`）：此时自动回退到 GDI。但 RDP 会话被断开/最小化/锁屏时，GDI 的 BitBlt 也会返回拒绝访问（日志 `Win32Error=5`），此时无法截屏；必须保持会话解锁连接。
- 被控端必须是已登录、未锁屏的交互式会话；UAC 安全桌面会黑屏。
- 被控端以普通权限运行时，无法操作以管理员权限运行的窗口（Windows UIPI 完整性级别限制）。
- DXGI 只采集第一个连接到桌面的输出（单显示器）；GDI 采集整个虚拟桌面（多显示器拼接）。
- 仅支持 32bpp BGRA 桌面格式；HDR/10bit 桌面会抛 `NotSupportedException`（需先做格式转换）。
- 整屏 JPEG 编码，带宽较高（1080p 约 200KB/帧）；未做脏矩形/增量，公网/低带宽建议后续升级 H.264 或 Windows.Graphics.Capture。

using System.Runtime.InteropServices;

namespace Remote.Agent;

/// <summary>
/// 把控制端发来的输入指令注入到本机（被控端执行）。坐标使用虚拟桌面绝对坐标。
/// </summary>
public static class InputInjector
{
    public static void MoveTo(int x, int y)
    {
        if (!SetCursorPos(x, y)) ReportFailure($"SetCursorPos({x},{y})", Marshal.GetLastWin32Error());
    }

    public static void MouseButton(int button, bool down, bool moveTo, int x, int y)
    {
        if ((uint)button > 2) return; // 协议边界：只接受左/右/中

        bool moved = true;
        if (moveTo)
        {
            moved = SetCursorPos(x, y);
            if (!moved) ReportFailure($"SetCursorPos({x},{y})", Marshal.GetLastWin32Error());
            else WarnIfCursorElsewhere(x, y);
        }

        uint flag = button switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0,
        };

        bool sent = false, posted = false, sendAttempted = false;
        int error = 0;

        // 抬起优先沿用按下成功的路径：按下走窗口消息、抬起走真实注入，目标控件会卡在按下态
        bool preferPost = !down && moveTo && ReadButtonPath(button) == PathPostMessage;

        if (!preferPost)
        {
            sendAttempted = true;
            sent = SendMouseInput(flag, 0, moveTo, x, y, out error);
        }
        if (!sent && moveTo) posted = TryPostMouse(button, down, x, y, out error);
        if (!sent && !posted && preferPost)
        {
            sendAttempted = true;
            sent = SendMouseInput(flag, 0, moveTo, x, y, out error);
        }

        if (sendAttempted && !sent)
            ReportFailure($"SendInput 鼠标 button={button} down={down} pos=({x},{y}) posted={posted}", error, injection: true);

        WriteButtonPath(button, down ? (sent ? PathSendInput : posted ? PathPostMessage : PathUnknown) : PathUnknown);

        var target = DesktopInfo.CursorWindowInfo();
        Log.Info($"mouse button={button} down={down} move={moveTo} pos=({x},{y}) moveOk={moved} sendOk={sent} postOk={posted} " +
                 $"sessionState={DesktopInfo.SessionConnectState()} " +
                 $"target='{target.Title}' ({target.ProcessName}, integrity={target.Integrity}, class='{target.ClassName}')");
    }

    /// <summary>
    /// 注入探测：光标右移 1px 再移回。
    /// 零位移 MOVE 会被系统当空操作直接返回成功，证明不了按键能不能投递；
    /// 非零位移才能真正暴露整条注入链是否被拦（被拦时返回 0）。
    /// </summary>
    public static bool Probe(bool logFailure = true)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE, dx = 1 } } },
            new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE, dx = -1 } } },
        };

        if (TrySendInput(inputs, out int error)) return true;

        if (logFailure)
        {
            Log.Warn($"SendInput 探测失败（光标 ±1px）: Win32Error={error} " +
                     $"sessionState={DesktopInfo.SessionConnectState()} winsta={DesktopInfo.WindowStationName()} " +
                     $"desktop={DesktopInfo.ThreadDesktopName()} inputDesktop={DesktopInfo.InputDesktopName()} " +
                     $"integrity={DesktopInfo.CurrentIntegrityLevel()}");
        }
        return false;
    }

    public static void Wheel(int delta)
    {
        if (!SendMouseInput(MOUSEEVENTF_WHEEL, delta, false, 0, 0, out int error))
            ReportFailure($"SendInput 滚轮 delta={delta}", error, injection: true);
    }

    public static void Key(int vkCode, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vkCode,
                    wScan = (ushort)MapVirtualKey((uint)vkCode, MAPVK_VK_TO_VSC),
                    dwFlags = (down ? 0 : KEYEVENTF_KEYUP) | (IsExtendedKey(vkCode) ? KEYEVENTF_EXTENDEDKEY : 0),
                },
            },
        };
        if (!TrySendInput(new[] { input }, out int error))
            ReportFailure($"SendInput 键盘 vk={vkCode} down={down}", error, injection: true);
    }

    /// <summary>
    /// 注入一次鼠标事件。atPoint=true 时把“绝对定位 + 按键”放进同一次 SendInput：
    /// 输入流里是原子的，不会出现光标还没挪到位、按键已按旧坐标投递。
    /// </summary>
    private static bool SendMouseInput(uint flags, int mouseData, bool atPoint, int x, int y, out int error)
    {
        if (!atPoint)
        {
            return TrySendInput(new[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } },
                },
            }, out error);
        }

        var (dx, dy) = ToAbsolute(x, y);
        return TrySendInput(new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    },
                },
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } },
            },
        }, out error);
    }

    private static bool TrySendInput(INPUT[] inputs, out int error)
    {
        error = 0;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == (uint)inputs.Length) return true;
        error = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>屏幕坐标 → SendInput 的 0..65535 虚拟桌面归一化坐标。</summary>
    private static (int dx, int dy) ToAbsolute(int x, int y)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        int rx = Math.Clamp(x - left, 0, width - 1);
        int ry = Math.Clamp(y - top, 0, height - 1);
        return (rx * 65535 / Math.Max(1, width - 1), ry * 65535 / Math.Max(1, height - 1));
    }

    /// <summary>SetCursorPos 成功但实际落点不同：DPI 感知或坐标映射不一致（多屏、缩放）的典型信号。</summary>
    private static void WarnIfCursorElsewhere(int x, int y)
    {
        if (!GetCursorPos(out var actual) || (actual.X == x && actual.Y == y)) return;
        Log.Warn($"SetCursorPos 请求 ({x},{y})，实际光标在 ({actual.X},{actual.Y})，坐标映射可能不一致（DPI/多屏）");
    }

    private static long _lastFailureLogTicks;

    private static void ReportFailure(string what, int error, bool injection = false)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFailureLogTicks) < 1000) return;
        Interlocked.Exchange(ref _lastFailureLogTicks, now);

        var target = DesktopInfo.CursorWindowInfo();
        var fg = DesktopInfo.ForegroundWindowInfo();
        string agentIntegrity = DesktopInfo.CurrentIntegrityLevel();
        bool agentElevated = agentIntegrity is "High" or "System";
        bool foregroundElevated = fg.Integrity is "High" or "System";

        string hint = (injection, error) switch
        {
            // 文档明确：被 UIPI 拦截时 SendInput 返回 0 且不设置 GetLastError
            (true, 0) when foregroundElevated && !agentElevated =>
                "前台窗口权限高于 Agent（UIPI）：以管理员身份运行 Agent，或降低目标程序权限。",
            (true, 0) when !foregroundElevated =>
                "注入被静默丢弃，但前台窗口与 Agent 同级，说明不是 UIPI 权限问题。" +
                "优先排查：安全软件/其它远控软件的模拟输入过滤、BlockInput、以及是否同机自测（前台其实是控制端窗口）。",
            (true, 0) => "注入被静默丢弃（UIPI 拦截时不设置错误码）：前台窗口权限更高，或输入桌面不是 Default。",
            (true, 5) => "拒绝访问：输入桌面可能是锁屏/安全桌面，或目标窗口权限更高。",
            _ => "",
        };

        Log.Warn($"{what} 失败: Win32Error={error}。{hint} " +
                 $"agentIntegrity={agentIntegrity} sessionState={DesktopInfo.SessionConnectState()} " +
                 $"winsta={DesktopInfo.WindowStationName()} desktop={DesktopInfo.ThreadDesktopName()} " +
                 $"inputDesktop={DesktopInfo.InputDesktopName()} globalSendInputOk={Probe(logFailure: false)} " +
                 $"target='{target.Title}' ({target.ProcessName}, pid={target.ProcessId}, integrity={target.Integrity}, class='{target.ClassName}') " +
                 $"foreground='{fg.Title}' ({fg.ProcessName}, integrity={fg.Integrity})");
    }

    /// <summary>
    /// SendInput 被拦时的兜底：向光标下的窗口投递鼠标消息。
    /// 这不是真实输入（没有鼠标捕获、命中测试和 hover 状态），只对少数控件有效，
    /// 能走 SendInput 就不要走这里。
    /// </summary>
    private static bool TryPostMouse(int button, bool down, int screenX, int screenY, out int error)
    {
        error = 0;
        if (DesktopInfo.SessionConnectState() != "Active") return false;

        uint message = (button, down) switch
        {
            (0, true) => WM_LBUTTONDOWN,
            (0, false) => WM_LBUTTONUP,
            (1, true) => WM_RBUTTONDOWN,
            (1, false) => WM_RBUTTONUP,
            (2, true) => WM_MBUTTONDOWN,
            (2, false) => WM_MBUTTONUP,
            _ => 0,
        };
        if (message == 0) return false;

        int wParam = down
            ? button switch { 0 => MK_LBUTTON, 1 => MK_RBUTTON, 2 => MK_MBUTTON, _ => 0 }
            : 0;

        // 命中的可能是深层子窗口；逐级上溯，不同控件在不同层级处理鼠标消息
        IntPtr hwnd = WindowFromPoint(new POINT { X = screenX, Y = screenY });
        for (int depth = 0; hwnd != IntPtr.Zero && depth < 4; depth++)
        {
            var point = new POINT { X = screenX, Y = screenY };
            if (ScreenToClient(hwnd, ref point))
            {
                IntPtr lParam = (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));
                PostMessage(hwnd, WM_MOUSEMOVE, (IntPtr)wParam, lParam); // 先补移动，控件才会更新内部命中状态
                if (PostMessage(hwnd, message, (IntPtr)wParam, lParam)) return true;
                error = Marshal.GetLastWin32Error();
            }
            hwnd = GetParent(hwnd);
        }

        Log.Warn($"PostMessage 点击失败: Win32Error={error} pos=({screenX},{screenY}) msg=0x{message:X}");
        return false;
    }

    // 记录每个按键按下时实际生效的注入方式，抬起时沿用同一条路径
    private const byte PathUnknown = 0;
    private const byte PathSendInput = 1;
    private const byte PathPostMessage = 2;
    private static readonly object PathLock = new();
    private static readonly byte[] ButtonPath = new byte[3];

    private static byte ReadButtonPath(int button)
    {
        lock (PathLock) return ButtonPath[button];
    }

    private static void WriteButtonPath(int button, byte path)
    {
        lock (PathLock) ButtonPath[button] = path;
    }

    // 这些键需要 KEYEVENTF_EXTENDEDKEY，否则方向键/Delete/右 Alt 等会被当成小键盘/左键
    private static bool IsExtendedKey(int vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28  // PgUp/PgDn/End/Home/方向键
        or 0x2D or 0x2E                                              // Insert/Delete
        or 0x5B or 0x5C or 0x5D                                      // Win/App
        or 0x6F                                                      // 小键盘 /
        or 0x90                                                      // NumLock
        or 0xA3 or 0xA5;                                             // 右 Ctrl/Alt

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const int MK_LBUTTON = 0x0001;
    private const int MK_RBUTTON = 0x0002;
    private const int MK_MBUTTON = 0x0010;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

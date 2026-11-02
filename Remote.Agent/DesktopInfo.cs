using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Remote.Agent;

/// <summary>用于排查“连上了但没画面/输入无效”的环境信息。</summary>
public static class DesktopInfo
{
    public static int SessionId => Process.GetCurrentProcess().SessionId;
    public static uint ActiveConsoleSessionId => WTSGetActiveConsoleSessionId();

    /// <summary>会话连接状态：Active 才能正常注入输入；Disconnected/Idle 时 SendInput 会被拒绝。</summary>
    public static string SessionConnectState()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, SessionId, WTS_CONNECT_STATE, out buffer, out _) || buffer == IntPtr.Zero)
                return "?";
            int state = Marshal.ReadInt32(buffer);
            return state switch
            {
                0 => "Active",
                1 => "Connected",
                2 => "ConnectQuery",
                3 => "Shadow",
                4 => "Disconnected",
                5 => "Idle",
                6 => "Listen",
                7 => "Reset",
                8 => "Down",
                9 => "Init",
                _ => state.ToString(),
            };
        }
        catch { return "?"; }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    public static string WindowStationName() => UserObjectName(GetProcessWindowStation());

    public static string ThreadDesktopName() => UserObjectName(GetThreadDesktop(GetCurrentThreadId()));

    private static string UserObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "?";
        var name = new StringBuilder(256);
        return GetUserObjectInformation(handle, UOI_NAME, name, name.Capacity * sizeof(char), out _)
            ? name.ToString()
            : "?";
    }

    /// <summary>当前输入桌面名称。Default=正常；Winlogon=锁屏/UAC 安全桌面；打不开通常说明会话不活跃。</summary>
    public static string InputDesktopName()
    {
        IntPtr desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
            return $"<open-failed:{Marshal.GetLastWin32Error()}>";

        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, UOI_NAME, name, name.Capacity * sizeof(char), out _))
                return $"<name-failed:{Marshal.GetLastWin32Error()}>";
            return name.ToString();
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>本进程的完整性级别（Low/Medium/High/System）。UIPI 要求注入方不低于目标窗口。</summary>
    public static string CurrentIntegrityLevel()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return "?";
            return IntegrityFromToken(token);
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    public readonly record struct WindowInfo(
        IntPtr Handle, int ProcessId, string ProcessName, string Title, string ClassName, string Integrity);

    /// <summary>当前前台窗口所属进程及其完整性级别。</summary>
    public static WindowInfo ForegroundWindowInfo() => DescribeWindow(GetForegroundWindow());

    /// <summary>鼠标光标下方的顶层窗口（点击真正会落到这个窗口），用于判断 UIPI 拦截。</summary>
    public static WindowInfo CursorWindowInfo()
    {
        if (!GetCursorPos(out POINT point)) return DescribeWindow(IntPtr.Zero);
        return WindowAtPoint(point.X, point.Y);
    }

    public static WindowInfo WindowAtPoint(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd != IntPtr.Zero) hwnd = GetAncestor(hwnd, GA_ROOT);
        return DescribeWindow(hwnd);
    }

    private static WindowInfo DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return new WindowInfo(IntPtr.Zero, 0, "?", "<none>", "?", "?");

        GetWindowThreadProcessId(hwnd, out uint pid);
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);

        string name = "?";
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }

        return new WindowInfo(hwnd, (int)pid, name, title.ToString(), className.ToString(),
            ProcessIntegrityLevel((int)pid));
    }

    private static string ProcessIntegrityLevel(int pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return "?";
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return "?";
            return IntegrityFromToken(token);
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    private static string IntegrityFromToken(IntPtr token)
    {
        int length = 0;
        GetTokenInformation(token, TOKEN_INTEGRITY_LEVEL, IntPtr.Zero, 0, out length);
        if (length == 0) return "?";

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TOKEN_INTEGRITY_LEVEL, buffer, length, out _)) return "?";

            IntPtr sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
            if (sid == IntPtr.Zero) return "?";

            int subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount <= 0) return "?";

            int rid = Marshal.ReadInt32(sid, 8 + (subAuthorityCount - 1) * 4);
            return rid switch
            {
                0x1000 => "Low",
                0x2000 => "Medium",
                0x2100 => "MediumPlus",
                0x3000 => "High",
                0x4000 => "System",
                _ => $"0x{rid:X}",
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;
    private const int WTS_CONNECT_STATE = 8;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int TOKEN_INTEGRITY_LEVEL = 25;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int infoClass,
        out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint GA_ROOT = 2;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
}

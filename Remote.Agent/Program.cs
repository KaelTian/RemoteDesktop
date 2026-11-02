using System.Runtime.InteropServices;
using Remote.Agent;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Error("Unhandled exception", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Error("Unobserved task exception", e.Exception);
    e.SetObserved();
};

// 必须在任何取坐标/注入的调用之前设置，否则 GetSystemMetrics/SetCursorPos 会被 DPI 虚拟化成逻辑坐标
DpiAwareness.Enable();

bool inputProbe = InputInjector.Probe();

Log.Info($"=== agent starting === args=[{string.Join(" ", args)}] pid={Environment.ProcessId} " +
         $"session={DesktopInfo.SessionId} sessionState={DesktopInfo.SessionConnectState()} " +
         $"activeConsole={DesktopInfo.ActiveConsoleSessionId} user={Environment.UserName} " +
         $"interactive={Environment.UserInteractive} integrity={DesktopInfo.CurrentIntegrityLevel()} " +
         $"winsta={DesktopInfo.WindowStationName()} desktop={DesktopInfo.ThreadDesktopName()} " +
         $"inputDesktop={DesktopInfo.InputDesktopName()} sendInputProbe={inputProbe} " +
         $"exe={Environment.ProcessPath} exeLastWrite={TryGetLastWrite(Environment.ProcessPath)}");

Console.WriteLine("==============================================");
Console.WriteLine(" Remote Desktop Agent (DXGI + GDI fallback)");
Console.WriteLine("==============================================");
Console.WriteLine($"[Agent] log: {Log.FilePath}");

int port = 33899;
string captureMode = "auto";
string? token = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--capture" when i + 1 < args.Length:
            captureMode = args[++i];
            break;
        case "--token" when i + 1 < args.Length:
            token = args[++i];
            break;
        case "--help":
            Console.WriteLine("用法: Remote.Agent [--port 33899] [--capture auto|dxgi|gdi] [--token 口令]");
            return;
    }
}

try
{
    using var capture = CaptureFactory.Create(captureMode);
    Log.Info($"capture backend={capture.Backend} {capture.Width}x{capture.Height} @({capture.Left},{capture.Top})");
    await new AgentServer(capture, token).RunAsync(port);
}
catch (Exception ex)
{
    Log.Error("Agent fatal", ex);
    Console.WriteLine($"Agent crashed: {ex}");
    Console.WriteLine($"日志: {Log.FilePath}");
    ConsoleHelper.PauseIfOwnConsole("按回车键退出..."); // 双击启动时让窗口停住，方便看错误
}

static string TryGetLastWrite(string? path)
{
    try { return path != null && File.Exists(path) ? File.GetLastWriteTime(path).ToString("O") : "?"; }
    catch { return "?"; }
}

static class DpiAwareness
{
    public static void Enable()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } // PER_MONITOR_AWARE_V2
        catch { try { SetProcessDPIAware(); } catch { /* 忽略 */ } }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}

static class ConsoleHelper
{
    /// <summary>仅当控制台窗口是本进程独占（双击启动）时暂停，避免窗口一闪而过。</summary>
    public static void PauseIfOwnConsole(string message)
    {
        try
        {
            if (!Environment.UserInteractive) return;
            var processes = new uint[16];
            uint count = GetConsoleProcessList(processes, (uint)processes.Length);
            if (count != 1) return; // 从终端启动时不阻塞
            Console.WriteLine(message);
            Console.ReadLine();
        }
        catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}

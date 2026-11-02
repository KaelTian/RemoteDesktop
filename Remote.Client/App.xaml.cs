using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Remote.Client;

public partial class App : Application
{
    public App()
    {
        Log.Info($"=== client starting === pid={Environment.ProcessId} session={TrySession()} " +
                 $"user={Environment.UserName} exe={Environment.ProcessPath} exeLastWrite={TryLastWrite()}");

        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("UI exception", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static int TrySession()
    {
        try { return Process.GetCurrentProcess().SessionId; } catch { return -1; }
    }

    private static string TryLastWrite()
    {
        try
        {
            return Environment.ProcessPath != null && File.Exists(Environment.ProcessPath)
                ? File.GetLastWriteTime(Environment.ProcessPath).ToString("O")
                : "?";
        }
        catch { return "?"; }
    }
}

using System.IO;
using System.Text;

namespace Remote.Client;

/// <summary>
/// 简单文件日志：%LOCALAPPDATA%\RemoteDesktop\client.log（不可写时回退到程序目录）。
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly string[] Paths = BuildPaths("client.log");

    public static string FilePath => Paths[0];

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static string[] BuildPaths(string fileName)
    {
        var list = new List<string>();
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                list.Add(Path.Combine(local, "RemoteDesktop", fileName));
        }
        catch { }

        try { list.Add(Path.Combine(AppContext.BaseDirectory, fileName)); } catch { }
        return list.ToArray();
    }

    private static void Write(string level, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

        lock (Sync)
        {
            foreach (string path in Paths)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                        File.Move(path, path + ".old", overwrite: true);
                    File.AppendAllText(path, line, Encoding.UTF8);
                    return;
                }
                catch
                {
                    // 尝试下一个路径
                }
            }
        }
    }
}

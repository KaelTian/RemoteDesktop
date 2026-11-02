using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Remote.Agent;

/// <summary>
/// TCP 服务端：握手（鉴权 + 分辨率）→ 循环推 JPEG 帧，同时接收输入指令。
/// 协议（均为小端）：
///   客户端 → 服务端: magic(uint32=0x52444D31), tokenLength(int32), token(bytes, UTF-8)
///   服务端 → 客户端: status(byte, 0=OK/1=口令错误)，OK 时后跟 width(int32), height(int32)
///   服务端 → 客户端: jpegLength(int32) + jpegBytes（循环）
///   客户端 → 服务端: type(byte)
///       1 = 鼠标移动: x(int32) y(int32)
///       2 = 鼠标按键: button(int32,0左/1右/2中) down(bool) x(int32) y(int32)
///       3 = 键盘按键: vkCode(int32) down(bool)
///       4 = 滚轮: delta(int32)
///       5 = 流设置: fps(int32) quality(int32)（集控网格用低帧率，放大时提高）
/// 鼠标坐标均为相对采集区域左上角的像素坐标，服务端按采集区域平移。
/// </summary>
public class AgentServer
{
    private const int DefaultFps = 25;
    private const int DefaultQuality = 70;

    private readonly IScreenCapture _capture;
    private readonly string? _requiredToken;

    public AgentServer(IScreenCapture capture, string? requiredToken)
    {
        _capture = capture;
        _requiredToken = requiredToken;
    }

    public async Task RunAsync(int port = 33899)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Log.Error($"端口 {port} 已被占用，可能已有旧 Agent 进程在运行。请先结束旧进程。", ex);
            throw;
        }

        string env = $"backend={_capture.Backend} {_capture.Width}x{_capture.Height} @({_capture.Left},{_capture.Top}) " +
                     $"session={DesktopInfo.SessionId} sessionState={DesktopInfo.SessionConnectState()} " +
                     $"activeConsole={DesktopInfo.ActiveConsoleSessionId} " +
                     $"integrity={DesktopInfo.CurrentIntegrityLevel()} winsta={DesktopInfo.WindowStationName()} " +
                     $"desktop={DesktopInfo.ThreadDesktopName()} inputDesktop={DesktopInfo.InputDesktopName()} " +
                     $"sendInputProbe={InputInjector.Probe()} " +
                     $"token={(!string.IsNullOrEmpty(_requiredToken) ? "on" : "OFF")}";
        Console.WriteLine($"[Agent] listening on 0.0.0.0:{port}");
        Console.WriteLine($"[Agent] capture: {_capture.Backend} {_capture.Width}x{_capture.Height} @({_capture.Left},{_capture.Top})");
        Console.WriteLine($"[Agent] session={DesktopInfo.SessionId} activeConsole={DesktopInfo.ActiveConsoleSessionId} inputDesktop={DesktopInfo.InputDesktopName()}");
        Console.WriteLine(string.IsNullOrEmpty(_requiredToken)
            ? "[Agent] 警告：未设置访问口令，能连上该端口的人都可以控制本机（仅限可信局域网）。用 --token 设置口令。"
            : "[Agent] 已启用访问口令。");
        Log.Info($"listening port={port} {env}");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var stream = client.GetStream();
        client.NoDelay = true;
        var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var writeLock = new SemaphoreSlim(1, 1);
        InputTracker? input = null;
        Task? inputTask = null;

        Log.Info($"{remote} connected");
        Console.WriteLine($"[Agent] client connected: {remote}");

        try
        {
            stream.ReadTimeout = 10_000; // 握手阶段防慢速连接长期占用
            if (!await HandshakeAsync(stream, reader, writer, writeLock, remote))
                return;

            stream.ReadTimeout = Timeout.Infinite;
            stream.WriteTimeout = 10_000; // 客户端停止读取时不要永久卡住
            Console.WriteLine($"[Agent] {remote} authenticated");
            var fg = DesktopInfo.ForegroundWindowInfo();
            Log.Info($"{remote} authenticated; capture={_capture.Backend} {_capture.Width}x{_capture.Height} " +
                     $"session={DesktopInfo.SessionId} sessionState={DesktopInfo.SessionConnectState()} " +
                     $"inputDesktop={DesktopInfo.InputDesktopName()} integrity={DesktopInfo.CurrentIntegrityLevel()} " +
                     $"winsta={DesktopInfo.WindowStationName()} sendInputProbe={InputInjector.Probe()} " +
                     $"foreground='{fg.Title}' ({fg.ProcessName}, pid={fg.ProcessId}, integrity={fg.Integrity})");

            var streamSettings = new StreamSettings();
            input = new InputTracker();
            inputTask = Task.Run(() => ReceiveInputAsync(reader, input, streamSettings, remote));

            long frames = 0, bytes = 0, nullFrames = 0;
            long statsStart = Environment.TickCount64, statsAt = statsStart;

            while (true)
            {
                var started = Stopwatch.GetTimestamp();
                byte[]? jpeg = _capture.CaptureJpeg(streamSettings.Quality);
                if (jpeg != null)
                {
                    frames++;
                    bytes += jpeg.Length;
                    if (frames == 1) Log.Info($"{remote} first frame {jpeg.Length} bytes");

                    await WriteAsync(stream, writer, writeLock, w =>
                    {
                        w.Write(jpeg.Length);
                        w.Write(jpeg);
                    });
                }
                else
                {
                    nullFrames++;
                }

                long now = Environment.TickCount64;
                if (now - statsAt >= 5000)
                {
                    double elapsed = Math.Max(0.001, (now - statsStart) / 1000.0);
                    Log.Info($"{remote} frames={frames} bytes={bytes / 1024}KB avgFps={frames / elapsed:F1} " +
                             $"nullCapture={nullFrames} fps={streamSettings.Fps} quality={streamSettings.Quality}");
                    statsAt = now;
                }

                var interval = TimeSpan.FromMilliseconds(1000.0 / streamSettings.Fps);
                var remaining = interval - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            }
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine($"[Agent] 采集不可用: {ex.Message}");
            Log.Error($"{remote} capture unsupported: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Console.WriteLine($"[Agent] {remote} disconnected");
            Log.Info($"{remote} disconnected: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Agent] {remote} session failed: {ex}");
            Log.Error($"{remote} session failed", ex);
        }
        finally
        {
            client.Close();
            if (inputTask != null)
            {
                try { await inputTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* 读取任务因断线退出 */ }
            }
            input?.ReleaseAll(); // 释放仍在按下的键/鼠标，避免远端卡键
            writeLock.Dispose();
            Console.WriteLine($"[Agent] {remote} closed");
            Log.Info($"{remote} closed");
        }
    }

    private async Task<bool> HandshakeAsync(NetworkStream stream, BinaryReader reader,
        BinaryWriter writer, SemaphoreSlim writeLock, string remote)
    {
        uint magic = reader.ReadUInt32();
        if (magic != Protocol.Magic)
        {
            Console.WriteLine($"[Agent] {remote} 协议不匹配 (0x{magic:X8})，断开（客户端版本过旧？）");
            Log.Warn($"{remote} bad magic 0x{magic:X8}");
            return false;
        }

        int tokenLength = reader.ReadInt32();
        if (tokenLength is < 0 or > Protocol.MaxTokenBytes)
        {
            Console.WriteLine($"[Agent] {remote} 非法口令长度 {tokenLength}，断开");
            Log.Warn($"{remote} bad token length {tokenLength}");
            return false;
        }

        string token = tokenLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(ReadExact(reader, tokenLength));
        bool accepted = string.IsNullOrEmpty(_requiredToken)
            || TokensEqual(token, _requiredToken);

        await WriteAsync(stream, writer, writeLock, w =>
        {
            w.Write(accepted ? (byte)0 : (byte)1);
            if (accepted)
            {
                w.Write(_capture.Width);
                w.Write(_capture.Height);
            }
        });

        if (accepted)
            Log.Info($"{remote} handshake ok, tokenLength={tokenLength}");
        else
        {
            Console.WriteLine($"[Agent] {remote} 拒绝连接：口令错误");
            Log.Warn($"{remote} rejected: wrong token");
        }

        return accepted;
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        byte[] buffer = reader.ReadBytes(count);
        if (buffer.Length != count) throw new EndOfStreamException();
        return buffer;
    }

    private static bool TokensEqual(string provided, string required)
    {
        byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(required));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task WriteAsync(NetworkStream stream, BinaryWriter writer,
        SemaphoreSlim writeLock, Action<BinaryWriter> body)
    {
        await writeLock.WaitAsync();
        try
        {
            body(writer);
            writer.Flush();
            await stream.FlushAsync();
        }
        finally
        {
            writeLock.Release();
        }
    }

    private void ReceiveInputAsync(BinaryReader reader, InputTracker input,
        StreamSettings streamSettings, string remote)
    {
        bool firstPacket = true;
        try
        {
            while (true)
            {
                byte type = reader.ReadByte();
                if (firstPacket)
                {
                    firstPacket = false;
                    Log.Info($"{remote} first input packet type={type}");
                }

                switch (type)
                {
                    case 1:
                        InputInjector.MoveTo(ToScreenX(reader.ReadInt32()), ToScreenY(reader.ReadInt32()));
                        break;

                    case 2:
                    {
                        int button = reader.ReadInt32();
                        bool down = reader.ReadBoolean();
                        int rawX = reader.ReadInt32();
                        int rawY = reader.ReadInt32();
                        // 坐标为 -1 表示只发按键、不移动光标（客户端失焦或断开时释放按键）
                        bool moveTo = rawX >= 0 && rawY >= 0;
                        input.Button(button, down, moveTo,
                            moveTo ? ToScreenX(rawX) : 0,
                            moveTo ? ToScreenY(rawY) : 0);
                        break;
                    }

                    case 3:
                        input.Key(reader.ReadInt32(), reader.ReadBoolean());
                        break;

                    case 4:
                        input.Wheel(reader.ReadInt32());
                        break;

                    case 5:
                    {
                        int fps = reader.ReadInt32();
                        int quality = reader.ReadInt32();
                        streamSettings.Fps = fps;
                        streamSettings.Quality = quality;
                        Log.Info($"{remote} stream fps={streamSettings.Fps} quality={streamSettings.Quality}");
                        break;
                    }

                    default:
                        Log.Warn($"{remote} unknown input type={type}, closing input loop");
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            // 客户端断开
        }
        catch (Exception ex)
        {
            Log.Error($"{remote} input loop failed", ex);
        }
    }

    private int ToScreenX(int x) =>
        Math.Clamp(_capture.Left + x, _capture.Left, _capture.Left + _capture.Width - 1);

    private int ToScreenY(int y) =>
        Math.Clamp(_capture.Top + y, _capture.Top, _capture.Top + _capture.Height - 1);

    /// <summary>每个连接独立的推流参数，客户端可用 type=5 随时调整。</summary>
    private sealed class StreamSettings
    {
        private int _fps = DefaultFps;
        private int _quality = DefaultQuality;

        public int Fps
        {
            get => Volatile.Read(ref _fps);
            set => Volatile.Write(ref _fps, Math.Clamp(value, 1, 60));
        }

        public int Quality
        {
            get => Volatile.Read(ref _quality);
            set => Volatile.Write(ref _quality, Math.Clamp(value, 30, 95));
        }
    }

    /// <summary>记录本连接注入后仍处于按下状态的键/鼠标，断线时统一释放，避免远端卡键。</summary>
    private sealed class InputTracker
    {
        private readonly object _lock = new();
        private readonly HashSet<int> _keys = new();
        private readonly HashSet<int> _buttons = new();

        public void Key(int vk, bool down)
        {
            lock (_lock)
            {
                if (down) _keys.Add(vk);
                else _keys.Remove(vk);
            }
            InputInjector.Key(vk, down);
        }

        public void Button(int button, bool down, bool moveTo, int x, int y)
        {
            lock (_lock)
            {
                if (down) _buttons.Add(button);
                else _buttons.Remove(button);
            }
            InputInjector.MouseButton(button, down, moveTo, x, y);
        }

        public void Wheel(int delta) => InputInjector.Wheel(delta);

        public void ReleaseAll()
        {
            int[] keys, buttons;
            lock (_lock)
            {
                keys = _keys.ToArray();
                buttons = _buttons.ToArray();
                _keys.Clear();
                _buttons.Clear();
            }

            foreach (int vk in keys) InputInjector.Key(vk, false);
            foreach (int button in buttons) InputInjector.MouseButton(button, false, false, 0, 0);
        }
    }
}

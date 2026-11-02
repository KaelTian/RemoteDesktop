using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Remote.Client;

/// <summary>
/// 一台被控机的连接会话：握手、收帧、发送输入与流设置。
/// 一机一连接；网格和放大视图复用同一份帧，不重复建连。
/// </summary>
public sealed class RemoteSession : IDisposable
{
    private const uint ProtocolMagic = 0x52444D31; // "RDM1"
    private const int MaxFrameBytes = 100 * 1024 * 1024;

    private readonly object _sendLock = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;
    private BinaryReader? _reader;
    private BinaryWriter? _writer;
    private volatile bool _connected;
    private int _frameWidth;
    private int _frameHeight;

    public RemoteSession(string host, int port, string token)
    {
        Host = host;
        Port = port;
        Token = token;
    }

    public string Host { get; }
    public int Port { get; }
    public string Token { get; }
    public string DisplayName => Port == 33899 ? Host : $"{Host}:{Port}";
    public bool IsConnected => _connected;
    public int FrameWidth => Volatile.Read(ref _frameWidth);
    public int FrameHeight => Volatile.Read(ref _frameHeight);

    /// <summary>在后台线程触发，调用方负责切回 UI 线程。</summary>
    public event Action<RemoteSession, BitmapSource>? FrameReady;
    public event Action<RemoteSession, string>? StateChanged;

    public async Task ConnectAsync()
    {
        if (_connected) return;
        SetState("连接中...");

        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(Host, Port);
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            stream.ReadTimeout = 10_000; // 握手阶段防慢速连接长期占用

            var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            byte[] token = Encoding.UTF8.GetBytes(Token);
            writer.Write(ProtocolMagic);
            writer.Write(token.Length);
            writer.Write(token);
            writer.Flush();

            byte status = reader.ReadByte();
            if (status != 0)
                throw new InvalidOperationException("口令错误");

            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            if (width <= 0 || height <= 0)
                throw new IOException("服务端返回了非法分辨率");

            Volatile.Write(ref _frameWidth, width);
            Volatile.Write(ref _frameHeight, height);

            _tcp = tcp;
            _reader = reader;
            _writer = writer;
            _connected = true;
            stream.ReadTimeout = Timeout.Infinite;
            SetState($"已连接 ({width}x{height})");

            // 网格默认低帧率省带宽；选中放大时再由 UI 提高到 25fps
            SetStream(3, 60);

            _ = Task.Run(ReceiveLoop);
        }
        catch (Exception ex)
        {
            try { tcp.Close(); } catch { }
            _connected = false;
            SetState($"连接失败: {ex.Message}");
            throw;
        }
    }

    private void ReceiveLoop()
    {
        var reader = _reader;
        if (reader == null) return;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int length = reader.ReadInt32();
                if (length <= 0 || length > MaxFrameBytes)
                    throw new IOException($"非法帧长度 {length}");

                byte[] jpeg = reader.ReadBytes(length);
                if (jpeg.Length != length)
                    throw new EndOfStreamException();

                BitmapSource frame = Decode(jpeg);
                Volatile.Write(ref _frameWidth, frame.PixelWidth);
                Volatile.Write(ref _frameHeight, frame.PixelHeight);
                FrameReady?.Invoke(this, frame);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            if (!_cts.IsCancellationRequested) SetState("连接已断开");
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) SetState($"连接异常: {ex.Message}");
        }
        finally
        {
            _connected = false;
            Cleanup();
        }
    }

    private static BitmapSource Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg, writable: false);
        BitmapSource frame = BitmapDecoder
            .Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
            .Frames[0];

        if (frame.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = frame;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            frame = converted;
        }
        else
        {
            frame.Freeze();
        }

        return frame;
    }

    /// <summary>请求服务端调整本连接的帧率与 JPEG 质量。</summary>
    public void SetStream(int fps, int quality) =>
        Send(w => { w.Write((byte)5); w.Write(fps); w.Write(quality); });

    public void SendMouseMove(int x, int y) =>
        Send(w => { w.Write((byte)1); w.Write(x); w.Write(y); });

    public void SendMouseButton(int button, bool down, int x, int y) =>
        Send(w =>
        {
            w.Write((byte)2);
            w.Write(button);
            w.Write(down);
            w.Write(x);
            w.Write(y);
        });

    public void SendWheel(int delta) =>
        Send(w => { w.Write((byte)4); w.Write(delta); });

    public void SendKey(int vkCode, bool down) =>
        Send(w => { w.Write((byte)3); w.Write(vkCode); w.Write(down); });

    private void Send(Action<BinaryWriter> body)
    {
        var writer = _writer;
        if (writer == null) return;

        lock (_sendLock)
        {
            try
            {
                body(writer);
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                SetState("连接已断开");
            }
        }
    }

    public void Disconnect()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        Cleanup();
        SetState("未连接");
    }

    private void Cleanup()
    {
        _connected = false;
        try { _tcp?.Close(); } catch { }
        _tcp = null;
        _reader = null;
        _writer = null;
    }

    private void SetState(string state) => StateChanged?.Invoke(this, state);

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}

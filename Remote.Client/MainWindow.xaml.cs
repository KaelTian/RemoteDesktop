using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Remote.Client;

public partial class MainWindow : Window
{
    private const uint ProtocolMagic = 0x52444D31; // "RDM1"
    private const int Port = 33899;
    private const int MaxFrameBytes = 100 * 1024 * 1024;
    private const int MouseMoveThrottleMs = 8;

    private TcpClient? _tcp;
    private BinaryWriter? _writer;
    private BinaryReader? _reader;
    private int _srvW, _srvH;
    private readonly object _sendLock = new();
    private readonly HashSet<int> _pressedKeys = new();
    private readonly HashSet<int> _pressedButtons = new();
    private long _lastMouseMoveTicks;
    private volatile bool _connected;

    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;

    private readonly DispatcherTimer _statusTimer;
    private long _connectedAtTicks;
    private long _lastFrameTicks;
    private long _lastStatsLogTicks;
    private long _framesTotal;
    private long _bytesTotal;
    private bool _noFrameWarned;
    private bool _sendFailureLogged;
    private bool _uiSizeWarned;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    // ─────────── 连接 / 断开 ───────────

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_connected) return;

        ConnectBtn.IsEnabled = false;
        StatusText.Text = "连接中...";
        Log.Info($"connecting to {IpBox.Text.Trim()}:{Port} tokenLength={TokenBox.Password.Length}");

        TcpClient? tcp = null;
        try
        {
            tcp = new TcpClient();
            await tcp.ConnectAsync(IpBox.Text.Trim(), Port);
            var stream = tcp.GetStream();
            var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            // 鉴权握手：magic + 口令
            byte[] token = Encoding.UTF8.GetBytes(TokenBox.Password);
            writer.Write(ProtocolMagic);
            writer.Write(token.Length);
            writer.Write(token);
            writer.Flush();

            byte status = reader.ReadByte();
            if (status != 0)
            {
                tcp.Close();
                Log.Warn("handshake rejected: wrong token");
                DisconnectUi("口令错误");
                return;
            }

            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            if (width <= 0 || height <= 0)
                throw new IOException("服务端返回了非法分辨率");

            _tcp = tcp;
            _writer = writer;
            _reader = reader;
            _srvW = width;
            _srvH = height;
            _connected = true;

            _connectedAtTicks = Environment.TickCount64;
            _lastFrameTicks = 0;
            _lastStatsLogTicks = _connectedAtTicks;
            _framesTotal = 0;
            _bytesTotal = 0;
            _noFrameWarned = false;
            _sendFailureLogged = false;
            _uiSizeWarned = false;

            DisconnectBtn.IsEnabled = true;
            StatusText.Text = $"已连接 ({width}x{height})，等待画面...";
            Log.Info($"handshake ok {width}x{height}");
            _ = Task.Run(ReceiveLoopAsync);
        }
        catch (Exception ex)
        {
            try { tcp?.Close(); } catch { }
            Log.Error($"connect failed: {ex.Message}", ex);
            DisconnectUi($"连接失败: {ex.Message}");
        }
    }

    private void DisconnectBtn_Click(object sender, RoutedEventArgs e) => DisconnectUi("未连接");

    private void DisconnectUi(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => DisconnectUi(status)));
            return;
        }

        bool wasActive = _connected || _tcp != null;
        _connected = false;
        _pressedKeys.Clear();
        _pressedButtons.Clear();
        _noFrameWarned = false;

        try { _tcp?.Close(); } catch { }
        _tcp = null;
        _writer = null;
        _reader = null;

        if (wasActive) Log.Info($"disconnected: {status}");

        ConnectBtn.IsEnabled = true;
        DisconnectBtn.IsEnabled = false;
        StatusText.Text = status;
        ScreenImage.Source = null;
        ScreenImage.ReleaseMouseCapture();
        _bitmap = null;
        _pixelBuffer = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        DisconnectUi("未连接");
        base.OnClosed(e);
    }

    // ─────────── 收帧渲染 ───────────

    private async Task ReceiveLoopAsync()
    {
        var reader = _reader;
        if (reader == null) return;

        try
        {
            while (true)
            {
                int length = reader.ReadInt32();
                if (length <= 0 || length > MaxFrameBytes)
                    throw new IOException($"非法帧长度 {length}");

                byte[] jpeg = reader.ReadBytes(length);
                if (jpeg.Length != length)
                    throw new EndOfStreamException();

                await Dispatcher.InvokeAsync(() => ShowFrame(jpeg));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            Log.Info($"receive ended: {ex.GetType().Name}: {ex.Message}; frames={_framesTotal}");
            await Dispatcher.InvokeAsync(() =>
            {
                if (_connected) DisconnectUi("连接已断开");
            });
        }
        catch (Exception ex)
        {
            // 解码/渲染异常等：断开并显示原因，避免 UI 停留在“已连接”
            Log.Error("receive loop failed", ex);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_connected) DisconnectUi($"连接异常: {ex.Message}");
            });
        }
    }

    private void ShowFrame(byte[] jpeg)
    {
        if (!_connected) return; // 断开后丢弃排队中的延迟帧

        using var ms = new MemoryStream(jpeg, writable: false);
        BitmapSource frame = BitmapDecoder
            .Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
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

        // 分辨率可能中途变化（GDI 重建/显示切换），以实际帧尺寸为准
        if (_srvW > 0 && (_srvW != frame.PixelWidth || _srvH != frame.PixelHeight))
            Log.Warn($"resolution changed {_srvW}x{_srvH} -> {frame.PixelWidth}x{frame.PixelHeight}");
        _srvW = frame.PixelWidth;
        _srvH = frame.PixelHeight;

        int stride = frame.PixelWidth * 4;
        int size = stride * frame.PixelHeight;
        if (_pixelBuffer == null || _pixelBuffer.Length != size)
            _pixelBuffer = new byte[size];

        frame.CopyPixels(_pixelBuffer, stride, 0);

        if (_bitmap == null
            || _bitmap.PixelWidth != frame.PixelWidth
            || _bitmap.PixelHeight != frame.PixelHeight)
        {
            _bitmap = new WriteableBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Bgra32, null);
            ScreenImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight), _pixelBuffer, stride, 0);

        _framesTotal++;
        _bytesTotal += jpeg.Length;
        if (_framesTotal == 1)
            Log.Info($"first frame {jpeg.Length}B {frame.PixelWidth}x{frame.PixelHeight}");

        _lastFrameTicks = Environment.TickCount64;
        _noFrameWarned = false;
    }

    // ─────────── 坐标映射（扣掉 Uniform 黑边） ───────────

    private bool TryMapToServer(Point p, out int x, out int y)
    {
        x = y = 0;
        double cw = ScreenImage.ActualWidth, ch = ScreenImage.ActualHeight;
        if (cw <= 0 || ch <= 0)
        {
            if (!_uiSizeWarned)
            {
                _uiSizeWarned = true;
                Log.Warn($"screen image has zero size ({cw}x{ch}); input mapping disabled");
            }
            return false;
        }
        if (_srvW <= 0 || _srvH <= 0) return false;

        double scale = Math.Min(cw / _srvW, ch / _srvH);
        double imageX = (p.X - (cw - _srvW * scale) / 2) / scale;
        double imageY = (p.Y - (ch - _srvH * scale) / 2) / scale;

        if (imageX < 0 || imageY < 0 || imageX >= _srvW || imageY >= _srvH) return false;
        x = (int)imageX;
        y = (int)imageY;
        return true;
    }

    // ─────────── 输入捕获 ───────────

    private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_connected) return;

        long now = Environment.TickCount64;
        if (now - _lastMouseMoveTicks < MouseMoveThrottleMs) return;
        if (!TryMapToServer(e.GetPosition(ScreenImage), out int x, out int y)) return;

        _lastMouseMoveTicks = now;
        Send(w => { w.Write((byte)1); w.Write(x); w.Write(y); });
    }

    private void ScreenImage_MouseButton(object sender, MouseButtonEventArgs e)
    {
        if (!_connected) return;

        int button = e.ChangedButton switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1,
        };
        if (button < 0) return;

        bool down = e.ButtonState == MouseButtonState.Pressed;
        ScreenImage.Focus();
        bool mapped = TryMapToServer(e.GetPosition(ScreenImage), out int x, out int y);

        if (down)
        {
            if (!mapped) return; // 在画面外按下，忽略
            ScreenImage.CaptureMouse();
            _pressedButtons.Add(button);
        }
        else
        {
            ScreenImage.ReleaseMouseCapture();
            _pressedButtons.Remove(button);
            // 释放必须发出去，否则远端会卡键；映射不到时发 -1（只释放不移动）
            if (!mapped) { x = -1; y = -1; }
        }

        Log.Info($"send click button={button} down={down} x={x} y={y}");
        Send(w =>
        {
            w.Write((byte)2);
            w.Write(button);
            w.Write(down);
            w.Write(x);
            w.Write(y);
        });
        e.Handled = true;
    }

    private void ScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_connected) return;
        Send(w => { w.Write((byte)4); w.Write(e.Delta); });
        e.Handled = true;
    }

    private bool IsTypingInInput() => IpBox.IsKeyboardFocusWithin || TokenBox.IsKeyboardFocusWithin;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_connected || IsTypingInInput()) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        _pressedKeys.Add(vk);
        Send(w => { w.Write((byte)3); w.Write(vk); w.Write(true); });
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!_connected || IsTypingInInput()) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        _pressedKeys.Remove(vk);
        Send(w => { w.Write((byte)3); w.Write(vk); w.Write(false); });
        e.Handled = true;
    }

    // 窗口失活时释放按住的键/鼠标，避免远端“卡键”
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        foreach (int vk in _pressedKeys)
            Send(w => { w.Write((byte)3); w.Write(vk); w.Write(false); });
        _pressedKeys.Clear();

        foreach (int button in _pressedButtons)
            Send(w => { w.Write((byte)2); w.Write(button); w.Write(false); w.Write(-1); w.Write(-1); });
        _pressedButtons.Clear();

        ScreenImage.ReleaseMouseCapture();
    }

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
                if (!_sendFailureLogged)
                {
                    _sendFailureLogged = true;
                    Log.Warn($"send failed: {ex.GetType().Name}: {ex.Message}");
                }
                Dispatcher.BeginInvoke(new Action(() => DisconnectUi("连接已断开")));
            }
        }
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        if (!_connected) return;

        long now = Environment.TickCount64;
        long last = _lastFrameTicks == 0 ? _connectedAtTicks : _lastFrameTicks;
        double sinceFrame = (now - last) / 1000.0;

        if (sinceFrame > 5)
        {
            if (!_noFrameWarned)
            {
                _noFrameWarned = true;
                StatusText.Text = _lastFrameTicks == 0
                    ? $"已连接 ({_srvW}x{_srvH})，但 5 秒内未收到画面"
                    : $"已连接 ({_srvW}x{_srvH})，画面已停更 {sinceFrame:F0} 秒";
                Log.Warn($"no frame: sinceLast={sinceFrame:F1}s lastFrameTicks={_lastFrameTicks}; " +
                         "检查被控端日志（锁屏/安全桌面/采集失败/旧协议进程）");
            }
            return;
        }

        _noFrameWarned = false;
        double totalSeconds = Math.Max(1, now - _connectedAtTicks) / 1000.0;

        if (now - _lastStatsLogTicks >= 5000)
        {
            _lastStatsLogTicks = now;
            Log.Info($"frames={_framesTotal} bytes={_bytesTotal / 1024}KB avgFps={_framesTotal / totalSeconds:F1}");
        }

        StatusText.Text = $"已连接 ({_srvW}x{_srvH}) | {_framesTotal / totalSeconds:F0} fps";
    }
}

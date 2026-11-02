using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Remote.Agent;

/// <summary>
/// GDI BitBlt 采集。作为 DXGI 不可用时的兜底（典型场景：RDP/远程会话、锁屏、无物理输出）。
/// </summary>
public sealed class GdiCapture : IScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    private readonly object _sync = new();
    private readonly ImageCodecInfo _jpegCodec;
    private Bitmap _bitmap;
    private Graphics _graphics;
    private bool _disposed;
    private DateTime _nextAttemptUtc;
    private static long _lastFailureLogTicks;
    private long _perfFrames;
    private long _perfBitbltTicks;
    private long _perfCursorTicks;
    private long _perfEncodeTicks;

    public GdiCapture()
    {
        _jpegCodec = ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/jpeg");
        TryReadMetrics();

        // 会话切换/RDP 重连瞬间 GetSystemMetrics 可能返回 0；不要因此退出，先用占位尺寸，桌面恢复后自动重建
        if (Width <= 0 || Height <= 0)
        {
            Log.Warn($"GDI 暂时拿不到桌面尺寸（会话未激活/正在重连？），使用占位 1024x768 继续运行");
            Width = 1024;
            Height = 768;
        }

        _bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
        Log.Info($"GDI init {Width}x{Height} @({Left},{Top})");
    }

    public string Backend => "GDI";
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public byte[]? CaptureJpeg(int quality)
    {
        lock (_sync)
        {
            if (_disposed) return null;
            if (DateTime.UtcNow < _nextAttemptUtc) return null; // 失败后退避，避免空转

            // RDP 会话缩放/分辨率变化时重建缓冲区（先建新对象，成功后再释放旧对象）
            if (TryReadMetrics())
            {
                var newBitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
                var newGraphics = Graphics.FromImage(newBitmap);
                _graphics.Dispose();
                _bitmap.Dispose();
                _bitmap = newBitmap;
                _graphics = newGraphics;
                Log.Info($"GDI desktop changed: {Width}x{Height} @({Left},{Top})");
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                _nextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                ReportFailure("GetDC", Marshal.GetLastWin32Error());
                return null;
            }

            IntPtr bitmapDc = _graphics.GetHdc();
            try
            {
                long tBitStart = Stopwatch.GetTimestamp();
                if (!BitBlt(bitmapDc, 0, 0, Width, Height, screenDc, Left, Top, SRCCOPY | CAPTUREBLT))
                {
                    _nextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    ReportFailure($"BitBlt({Width}x{Height} @{Left},{Top})", Marshal.GetLastWin32Error());
                    return null;
                }

                long tBitEnd = Stopwatch.GetTimestamp();
                CursorRenderer.Draw(bitmapDc, Left, Top, Width, Height);
                long tDrawEnd = Stopwatch.GetTimestamp();
                _perfBitbltTicks += tBitEnd - tBitStart;
                _perfCursorTicks += tDrawEnd - tBitEnd;
            }
            finally
            {
                _graphics.ReleaseHdc(bitmapDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }

            _nextAttemptUtc = DateTime.MinValue;

            long tEncodeStart = Stopwatch.GetTimestamp();
            using var ms = new MemoryStream();
            using var eps = new EncoderParameters(1);
            eps.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            _bitmap.Save(ms, _jpegCodec, eps);
            byte[] frame = ms.ToArray();
            _perfEncodeTicks += Stopwatch.GetTimestamp() - tEncodeStart;
            LogPerf();
            return frame;
        }
    }

    private void LogPerf()
    {
        if (++_perfFrames < 100) return;

        double f = 1000.0 / Stopwatch.Frequency / _perfFrames;
        Log.Info($"GDI perf {_perfFrames}帧 avg bitblt={_perfBitbltTicks * f:F1}ms " +
                 $"cursor={_perfCursorTicks * f:F1}ms encode={_perfEncodeTicks * f:F1}ms");
        _perfFrames = 0;
        _perfBitbltTicks = 0;
        _perfCursorTicks = 0;
        _perfEncodeTicks = 0;
    }

    private static void ReportFailure(string what, int error)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFailureLogTicks) < 1000) return;
        Interlocked.Exchange(ref _lastFailureLogTicks, now);
        string hint = error == 5
            ? "（会话可能已锁屏/断开：RDP 必须保持连接且解锁；UAC 安全桌面也不可截屏）"
            : "";
        Log.Warn($"GDI 采集失败 {what}: Win32Error={error}{hint}");
    }

    private bool TryReadMetrics()
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) return false;

        bool changed = left != Left || top != Top || width != Width || height != Height;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        return changed && _bitmap != null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _graphics.Dispose();
            _bitmap.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, uint rop);
}

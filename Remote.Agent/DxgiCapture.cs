using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Remote.Agent;

/// <summary>
/// 基于 DXGI Desktop Duplication 的桌面捕获器（被控端核心）。
/// 帧直接从 DWM 合成器输出拿，不占 GDI 全屏拷贝的开销。
/// </summary>
public sealed class DxgiCapture : IScreenCapture
{
    // 错误码直接用 Vortice 的 ResultCode，不要手写十六进制：这里曾把 ACCESS_LOST 写成 0x887A0022
    //（实际是 NOT_CURRENTLY_AVAILABLE），锁屏/切分辨率后认不出来、采集永远不重建。
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/jpeg");
    private static readonly TimeSpan RecreateCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecreateLogCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AcquireLogCooldown = TimeSpan.FromSeconds(5);
    private static readonly FeatureLevel[] FeatureLevels =
        { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };

    private readonly object _sync = new();
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private Format _stagingFormat;
    private DateTime _nextRecreateAttemptUtc;
    private DateTime _nextRecreateLogUtc;
    private DateTime _nextAcquireLogUtc;
    private bool _disposed;
    private long _perfFrames;
    private long _perfCaptureTicks;
    private long _perfEncodeTicks;

    public string Backend => "DXGI";
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public DxgiCapture() => Initialize();

    private void Initialize()
    {
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();

        // 不能用“适配器 0 + 输出 0”，混合显卡 / 多显示器时会选错。挑选挂着桌面的输出。
        IDXGIAdapter1? adapter = null;
        IDXGIOutput? output = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var candidate).Success; i++)
        {
            for (uint j = 0; candidate.EnumOutputs(j, out var candidateOutput).Success; j++)
            {
                if (candidateOutput.Description.AttachedToDesktop)
                {
                    adapter = candidate;
                    output = candidateOutput;
                    break;
                }
                candidateOutput.Dispose();
            }
            if (output != null) break;
            candidate.Dispose();
        }

        if (adapter == null || output == null)
            throw new InvalidOperationException("未找到连接到桌面的 DXGI 输出。");

        string adapterName = adapter.Description1.Description;

        try
        {
            // 传了 adapter 时 DriverType 必须为 Unknown，用 Hardware 会得到 E_INVALIDARG。
            // BgraSupport 是 Desktop Duplication 的硬性要求；FeatureLevel 不能传 null（同样 E_INVALIDARG）。
            var hr = D3D11CreateDevice(adapter, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport, FeatureLevels, out _device, out _context);
            if (hr.Failure)
                throw new InvalidOperationException($"D3D11CreateDevice 失败: 0x{hr.Code:X8}");

            using var output1 = output.QueryInterface<IDXGIOutput1>();
            try
            {
                _duplication = output1.DuplicateOutput(_device);
            }
            catch (SharpGenException ex)
            {
                throw new InvalidOperationException(
                    $"DuplicateOutput 失败: 0x{ex.HResult:X8}。常见原因：当前是 RDP/远程会话（DXGI 桌面复制不支持 RDP）、" +
                    "桌面已锁屏、UAC 安全桌面，或已有其他程序在复制桌面。", ex);
            }

            var desc = output.Description;
            Left = desc.DesktopCoordinates.Left;
            Top = desc.DesktopCoordinates.Top;
            Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            Log.Info($"DXGI init adapter='{adapterName}' output={desc.DeviceName} {Width}x{Height} @({Left},{Top})");
        }
        catch
        {
            _context?.Dispose();
            _device?.Dispose();
            throw;
        }
        finally
        {
            adapter.Dispose();
            output.Dispose();
        }

        _staging?.Dispose();
        _staging = null;
        _nextRecreateAttemptUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 取一帧并编码为 JPEG。超时（无新帧）返回 null。
    /// 同一实例被多客户端共享，内部加锁保证只有一个线程持有 duplication 帧。
    /// </summary>
    public byte[]? CaptureJpeg(int quality = 70)
    {
        lock (_sync)
        {
            if (_disposed) return null;

            if (_duplication == null)
            {
                if (DateTime.UtcNow < _nextRecreateAttemptUtc) return null;
                try
                {
                    Initialize();
                    Log.Info("DXGI 采集已重建");
                }
                catch (Exception ex)
                {
                    _nextRecreateAttemptUtc = DateTime.UtcNow + RecreateCooldown;
                    if (DateTime.UtcNow >= _nextRecreateLogUtc)
                    {
                        _nextRecreateLogUtc = DateTime.UtcNow + RecreateLogCooldown;
                        Log.Warn($"重建 DXGI 采集失败（锁屏/安全桌面/无物理输出？）: {ex.GetType().Name}: {ex.Message}");
                    }
                    return null;
                }
            }

            // 超时放短：没有新帧时也要尽快返回，否则光标位置最多 100ms 才更新一次
            long tBegin = Stopwatch.GetTimestamp();
            var hr = _duplication!.AcquireNextFrame(15, out _, out IDXGIResource? resource);
            if (hr.Failure)
            {
                RecoverFromAcquireFailure(hr);
                return null;
            }
            if (resource == null) return null;

            try
            {
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                EnsureStaging(texture.Description);
                _context.CopyResource(_staging!, texture);
                byte[] pixels = MapPixels();
                long tAfterCapture = Stopwatch.GetTimestamp();
                byte[] jpeg = EncodeJpeg(pixels, quality);
                LogPerf(tBegin, tAfterCapture);
                return jpeg;
            }
            catch (SharpGenException ex)
            {
                Log.Warn($"DXGI 帧处理失败（设备移除？将重建采集）: 0x{ex.HResult:X8}: {ex.Message}");
                ReleaseDevices();
                return null;
            }
            finally
            {
                resource.Dispose();
                _duplication?.ReleaseFrame();
            }
        }
    }

    private void LogPerf(long tBegin, long tAfterCapture)
    {
        long tEnd = Stopwatch.GetTimestamp();
        _perfCaptureTicks += tAfterCapture - tBegin;
        _perfEncodeTicks += tEnd - tAfterCapture;
        if (++_perfFrames < 100) return;

        double cap = _perfCaptureTicks * 1000.0 / Stopwatch.Frequency / _perfFrames;
        double enc = _perfEncodeTicks * 1000.0 / Stopwatch.Frequency / _perfFrames;
        Log.Info($"DXGI perf {_perfFrames}帧 avg capture={cap:F1}ms encode={enc:F1}ms");
        _perfFrames = 0;
        _perfCaptureTicks = 0;
        _perfEncodeTicks = 0;
    }

    private void RecoverFromAcquireFailure(Result hr)
    {
        // DXGI 与 Direct3D11 都有 ResultCode 类型，这里必须写全名
        if (hr == Vortice.DXGI.ResultCode.AccessLost || hr == Vortice.DXGI.ResultCode.SessionDisconnected
            || hr == Vortice.DXGI.ResultCode.DeviceRemoved || hr == Vortice.DXGI.ResultCode.DeviceReset)
        {
            Log.Warn($"DXGI 采集需重建 0x{hr.Code:X8}（锁屏/UAC/切会话/改分辨率/设备移除）");
            ReleaseDevices();
            return;
        }

        if (hr == Vortice.DXGI.ResultCode.WaitTimeout) return;

        // 其它错误每帧都会复现，必须限流，否则日志写盘会拖垮整个 Agent
        if (DateTime.UtcNow >= _nextAcquireLogUtc)
        {
            _nextAcquireLogUtc = DateTime.UtcNow + AcquireLogCooldown;
            Log.Warn($"DXGI AcquireNextFrame 失败 0x{hr.Code:X8}");
        }
    }

    private void EnsureStaging(Texture2DDescription src)
    {
        // JPEG 编码链路按 32bpp BGRA 处理；HDR/10bit 桌面会得出错误颜色，明确报错而不是静默出错
        if (src.Format is not (Format.B8G8R8A8_UNorm or Format.B8G8R8X8_UNorm))
            throw new NotSupportedException($"不支持的桌面像素格式: {src.Format}（HDR/10bit 暂未处理）");

        // 以实际帧尺寸为准，避免分辨率切换瞬间用旧的 Width/Height 读越界
        Width = (int)src.Width;
        Height = (int)src.Height;

        if (_staging != null && _stagingFormat == src.Format
            && _staging.Description.Width == src.Width && _staging.Description.Height == src.Height)
            return;

        // 分辨率变化后旧的 staging 尺寸不匹配，必须重建
        _staging?.Dispose();
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = src.Width,
            Height = src.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = src.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        _stagingFormat = src.Format;
        Log.Info($"DXGI staging {src.Width}x{src.Height} format={src.Format}");
    }

    private unsafe byte[] MapPixels()
    {
        var mapped = _context.Map(_staging!, 0, MapMode.Read, MapFlags.None);
        try
        {
            int stride = Width * 4;
            byte[] pixels = new byte[(long)stride * Height];
            fixed (byte* dst = pixels)
            {
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy((byte*)mapped.DataPointer + y * mapped.RowPitch,
                        dst + y * stride, stride, stride);
            }
            return pixels;
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private byte[] EncodeJpeg(byte[] bgraPixels, int quality)
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, Width, Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(bgraPixels, 0, bits.Scan0, bgraPixels.Length); }
        finally { bmp.UnlockBits(bits); }

        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { CursorRenderer.Draw(hdc, Left, Top, Width, Height); }
            finally { g.ReleaseHdc(hdc); }
        }

        using var ms = new MemoryStream();
        using var eps = new EncoderParameters(1);
        eps.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bmp.Save(ms, JpegCodec, eps);
        return ms.ToArray();
    }

    private void ReleaseDevices()
    {
        _duplication?.Dispose();
        _duplication = null;
        _context?.Dispose();
        _device?.Dispose();
        _staging?.Dispose();
        _staging = null;
        _nextRecreateAttemptUtc = DateTime.UtcNow + RecreateCooldown;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseDevices();
        }
    }
}

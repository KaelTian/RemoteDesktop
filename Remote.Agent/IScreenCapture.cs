namespace Remote.Agent;

/// <summary>
/// 屏幕采集后端。坐标为虚拟桌面像素坐标；输入注入时由服务端把 (0..Width, 0..Height) 平移回屏幕坐标。
/// </summary>
public interface IScreenCapture : IDisposable
{
    string Backend { get; }
    int Left { get; }
    int Top { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>抓一帧并编码为 JPEG；无新帧时返回 null。</summary>
    byte[]? CaptureJpeg(int quality);
}

public static class Protocol
{
    public const uint Magic = 0x52444D31;           // "RDM1"
    public const int MaxTokenBytes = 4096;
    public const int MaxFrameBytes = 100 * 1024 * 1024;
}

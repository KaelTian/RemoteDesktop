namespace Remote.Agent;

public static class CaptureFactory
{
    public static IScreenCapture Create(string mode)
    {
        switch (mode.ToLowerInvariant())
        {
            case "gdi":
                return new GdiCapture();
            case "dxgi":
                return new DxgiCapture();
            default:
                try
                {
                    return new DxgiCapture();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Agent] DXGI 采集不可用，回退到 GDI：{ex.Message}");
                    Log.Warn($"DXGI 采集不可用，回退到 GDI: {ex.GetType().Name}: {ex.Message}");
                    return new GdiCapture();
                }
        }
    }
}

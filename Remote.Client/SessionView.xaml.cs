using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Remote.Client;

/// <summary>网格中的一台机器画面。持有最近一帧，选中放大时直接复用，不重复解码。</summary>
public partial class SessionView : UserControl
{
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;

    public SessionView(RemoteSession session)
    {
        InitializeComponent();
        Session = session;
        TitleText.Text = session.DisplayName;
        StateText.Text = "未连接";
    }

    public RemoteSession Session { get; }
    public BitmapSource? LastFrame { get; private set; }

    /// <summary>点击该格请求放大。</summary>
    public event Action<SessionView>? Activated;

    public void UpdateFrame(BitmapSource frame)
    {
        LastFrame = frame;

        int stride = frame.PixelWidth * 4;
        int size = stride * frame.PixelHeight;
        if (_pixelBuffer == null || _pixelBuffer.Length != size)
            _pixelBuffer = new byte[size];
        frame.CopyPixels(_pixelBuffer, stride, 0);

        if (_bitmap == null
            || _bitmap.PixelWidth != frame.PixelWidth
            || _bitmap.PixelHeight != frame.PixelHeight)
        {
            _bitmap = new WriteableBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96,
                PixelFormats.Bgra32, null);
            FrameImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight),
            _pixelBuffer, stride, 0);
    }

    public void SetState(string state) => StateText.Text = state;

    private void OnMouseDown(object sender, MouseButtonEventArgs e) => Activated?.Invoke(this);
}

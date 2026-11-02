using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Remote.Client;

public partial class MainWindow : Window
{
    private const int GridFps = 3;
    private const int GridQuality = 60;
    private const int DetailFps = 25;
    private const int DetailQuality = 75;
    private const int DefaultPort = 33899;
    private const int MouseMoveThrottleMs = 8;

    private readonly List<SessionView> _views = new();
    private readonly HashSet<int> _pressedKeys = new();
    private readonly HashSet<int> _pressedButtons = new();
    private RemoteSession? _selected;
    private long _lastMouseMoveTicks;

    public MainWindow() => InitializeComponent();

    // ─────────── 连接 / 断开 ───────────

    private async void ConnectAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = ParseTargets(HostsBox.Text);
        if (targets.Count == 0)
        {
            StatusText.Text = "请输入至少一个目标";
            return;
        }

        ConnectBtn.IsEnabled = false;
        string token = TokenBox.Password;
        var pending = new List<RemoteSession>();

        foreach (var (host, port) in targets)
        {
            if (_views.Any(v => v.Session.Host == host && v.Session.Port == port)) continue;

            var session = new RemoteSession(host, port, token);
            var view = new SessionView(session);
            view.Activated += OnViewActivated;
            session.FrameReady += OnFrameReady;
            session.StateChanged += OnStateChanged;

            _views.Add(view);
            GridPanel.Children.Add(view);
            pending.Add(session);
            Log.Info($"added {session.DisplayName}");
        }

        GridPanel.Columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_views.Count)));
        StatusText.Text = $"连接 {pending.Count} 台...";

        await Task.WhenAll(pending.Select(async session =>
        {
            try { await session.ConnectAsync(); }
            catch { /* 失败原因已通过 StateChanged 显示 */ }
        }));

        DisconnectBtn.IsEnabled = _views.Count > 0;
        ConnectBtn.IsEnabled = true;
        UpdateStatus();
    }

    private void DisconnectAll_Click(object sender, RoutedEventArgs e) => DisconnectAll();

    private void DisconnectAll()
    {
        LeaveDetail();
        foreach (var view in _views)
        {
            view.Session.FrameReady -= OnFrameReady;
            view.Session.StateChanged -= OnStateChanged;
            view.Session.Dispose();
        }
        _views.Clear();
        GridPanel.Children.Clear();
        DisconnectBtn.IsEnabled = false;
        ConnectBtn.IsEnabled = true;
        StatusText.Text = "未连接";
    }

    protected override void OnClosed(EventArgs e)
    {
        DisconnectAll();
        base.OnClosed(e);
    }

    // ─────────── 网格 / 放大 ───────────

    private void OnViewActivated(SessionView view)
    {
        if (_selected != null && !ReferenceEquals(_selected, view.Session))
            _selected.SetStream(GridFps, GridQuality);

        _selected = view.Session;
        GridPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        BackBtn.Visibility = Visibility.Visible;
        DetailTitle.Text = $"{_selected.DisplayName}  {(_selected.IsConnected ? "已连接" : "未连接")}";
        DetailImage.Source = view.LastFrame;
        DetailImage.Focus();

        // 放大时提高帧率与质量，返回网格再降回去
        _selected.SetStream(DetailFps, DetailQuality);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => LeaveDetail();

    private void LeaveDetail()
    {
        if (_selected != null)
        {
            ReleasePressedInput(_selected);
            _selected.SetStream(GridFps, GridQuality);
        }

        _selected = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        GridPanel.Visibility = Visibility.Visible;
        BackBtn.Visibility = Visibility.Collapsed;
        DetailImage.Source = null;
    }

    private void OnFrameReady(RemoteSession session, BitmapSource frame)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var view = FindView(session);
            if (view == null) return;

            view.UpdateFrame(frame);
            if (ReferenceEquals(_selected, session))
                DetailImage.Source = view.LastFrame;
        }));
    }

    private void OnStateChanged(RemoteSession session, string state)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var view = FindView(session);
            view?.SetState(state);

            if (ReferenceEquals(_selected, session))
                DetailTitle.Text = $"{session.DisplayName}  {state}";

            UpdateStatus();
        }));
    }

    private SessionView? FindView(RemoteSession session) =>
        _views.FirstOrDefault(v => ReferenceEquals(v.Session, session));

    private void UpdateStatus()
    {
        if (_views.Count == 0)
        {
            StatusText.Text = "未连接";
            return;
        }

        int online = _views.Count(v => v.Session.IsConnected);
        StatusText.Text = $"{online}/{_views.Count} 台在线";
    }

    // ─────────── 输入（只发给当前放大的那一台） ───────────

    private bool TryMapToSelected(Point p, out int x, out int y)
    {
        x = y = 0;
        var session = _selected;
        if (session == null) return false;

        double cw = DetailImage.ActualWidth, ch = DetailImage.ActualHeight;
        if (cw <= 0 || ch <= 0) return false;

        int sw = session.FrameWidth, sh = session.FrameHeight;
        if (sw <= 0 || sh <= 0) return false;

        double scale = Math.Min(cw / sw, ch / sh);
        double imageX = (p.X - (cw - sw * scale) / 2) / scale;
        double imageY = (p.Y - (ch - sh * scale) / 2) / scale;
        if (imageX < 0 || imageY < 0 || imageX >= sw || imageY >= sh) return false;

        x = (int)imageX;
        y = (int)imageY;
        return true;
    }

    private void Detail_MouseMove(object sender, MouseEventArgs e)
    {
        var session = _selected;
        if (session == null) return;

        long now = Environment.TickCount64;
        if (now - _lastMouseMoveTicks < MouseMoveThrottleMs) return;
        if (!TryMapToSelected(e.GetPosition(DetailImage), out int x, out int y)) return;

        _lastMouseMoveTicks = now;
        session.SendMouseMove(x, y);
    }

    private void Detail_MouseButton(object sender, MouseButtonEventArgs e)
    {
        var session = _selected;
        if (session == null) return;

        int button = e.ChangedButton switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1,
        };
        if (button < 0) return;

        bool down = e.ButtonState == MouseButtonState.Pressed;
        DetailImage.Focus();
        bool mapped = TryMapToSelected(e.GetPosition(DetailImage), out int x, out int y);

        if (down)
        {
            if (!mapped) return; // 画面外按下，忽略
            DetailImage.CaptureMouse();
            _pressedButtons.Add(button);
        }
        else
        {
            DetailImage.ReleaseMouseCapture();
            _pressedButtons.Remove(button);
            // 释放必须发出去，否则远端卡键；映射不到时发 -1
            if (!mapped) { x = -1; y = -1; }
        }

        session.SendMouseButton(button, down, x, y);
        e.Handled = true;
    }

    private void Detail_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _selected?.SendWheel(e.Delta);
        e.Handled = true;
    }

    private bool IsTypingInInput() => HostsBox.IsKeyboardFocusWithin || TokenBox.IsKeyboardFocusWithin;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var session = _selected;
        if (session == null || IsTypingInInput()) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        _pressedKeys.Add(vk);
        session.SendKey(vk, true);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        var session = _selected;
        if (session == null || IsTypingInInput()) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        _pressedKeys.Remove(vk);
        session.SendKey(vk, false);
        e.Handled = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        ReleasePressedInput(_selected);
        DetailImage.ReleaseMouseCapture();
    }

    private void ReleasePressedInput(RemoteSession? session)
    {
        if (session != null)
        {
            foreach (int vk in _pressedKeys) session.SendKey(vk, false);
            foreach (int button in _pressedButtons)
                session.SendMouseButton(button, false, -1, -1);
        }

        _pressedKeys.Clear();
        _pressedButtons.Clear();
    }

    // ─────────── 目标解析 ───────────

    private static List<(string Host, int Port)> ParseTargets(string text)
    {
        var result = new List<(string, int)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string host = raw;
            int port = DefaultPort;

            int colon = raw.LastIndexOf(':');
            if (colon > 0 && int.TryParse(raw[(colon + 1)..], out int parsed)
                && parsed is > 0 and <= 65535)
            {
                host = raw[..colon];
                port = parsed;
            }

            if (seen.Add($"{host}:{port}"))
                result.Add((host, port));
        }

        return result;
    }
}

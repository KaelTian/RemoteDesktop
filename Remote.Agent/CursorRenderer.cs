using System.Runtime.InteropServices;

namespace Remote.Agent;

/// <summary>
/// 把系统鼠标指针画进截屏。DXGI/GDI 的桌面像素都不包含光标，不画的话控制端看不到鼠标位置。
/// </summary>
internal static class CursorRenderer
{
    private const int CURSOR_SHOWING = 0x0001;
    private const uint DI_NORMAL = 0x0003;

    public static void Draw(IntPtr hdc, int offsetX, int offsetY, int clipWidth, int clipHeight)
    {
        try
        {
            var cursor = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref cursor) || (cursor.flags & CURSOR_SHOWING) == 0) return;
            if (cursor.hCursor == IntPtr.Zero) return;

            if (!GetIconInfo(cursor.hCursor, out ICONINFO info)) return;
            try
            {
                int x = cursor.ptScreenPos.X - (int)info.xHotspot - offsetX;
                int y = cursor.ptScreenPos.Y - (int)info.yHotspot - offsetY;
                if (x < -64 || y < -64 || x > clipWidth || y > clipHeight) return;

                DrawIconEx(hdc, x, y, cursor.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            }
        }
        catch
        {
            // 光标绘制失败不影响截屏
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}

using System.Runtime.InteropServices;

namespace MyTube.Services;

internal static class NativeMethods
{
    public const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>ウィンドウがあるモニターの全体と作業領域（タスクバーを除く）。物理ピクセル。</summary>
    public static (RECT Monitor, RECT Work)? GetMonitorRects(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? (info.rcMonitor, info.rcWork) : null;
    }

    /// <summary>Windows 11 の角丸を指定する（古い Windows では何もしない）。</summary>
    public static void SetRoundCorners(IntPtr hwnd, bool round)
    {
        var value = round ? 2 /* DWMWCP_ROUND */ : 1 /* DWMWCP_DONOTROUND */;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    public static void SetBorderColor(IntPtr hwnd, byte r, byte g, byte b)
    {
        var value = r | (g << 8) | (b << 16);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(int));
    }
}

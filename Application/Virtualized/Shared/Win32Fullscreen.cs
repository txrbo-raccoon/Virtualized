using System.Runtime.InteropServices;

namespace Virtualized.Shared;

/// <summary>
/// Fullscreen for the main window on Windows. WebView2 only grows a fullscreen element to the WebView
/// bounds, and PhotinoWindow.SetFullScreen adds WS_OVERLAPPEDWINDOW back on exit (which breaks the
/// chromeless Cerberus frame) without restoring the old bounds. The window is already WS_POPUP, so
/// covering the monitor is enough for the shell to treat it as fullscreen and hide the taskbar.
/// Must be called on the UI thread.
/// </summary>
internal static class Win32Fullscreen
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Where the window was before going fullscreen; null while windowed.</summary>
    private static WindowPlacement? _restore;

    public static bool IsFullscreen => _restore is not null;

    public static void Enter(IntPtr hwnd)
    {
        if (_restore is not null || hwnd == IntPtr.Zero)
            return;

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowPlacement(hwnd, ref placement) ||
            !GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info))
            return;

        _restore = placement;
        var monitor = info.Monitor;
        SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
            SWP_NOOWNERZORDER | SWP_FRAMECHANGED);

        // Cerberus rounds the corners again on every non-maximized WM_SIZE, which the move above just sent.
        var preference = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    public static void Exit(IntPtr hwnd)
    {
        if (_restore is not { } placement || hwnd == IntPtr.Zero)
            return;

        _restore = null;
        var maximized = placement.ShowCmd == SW_SHOWMAXIMIZED;

        // Back to the normal bounds first, then maximize: a window that was maximized when it went
        // fullscreen is still flagged as maximized, so restoring straight to maximized wouldn't move it.
        placement.ShowCmd = SW_SHOWNORMAL;
        SetWindowPlacement(hwnd, ref placement);
        if (maximized)
            ShowWindow(hwnd, SW_SHOWMAXIMIZED);

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }
}

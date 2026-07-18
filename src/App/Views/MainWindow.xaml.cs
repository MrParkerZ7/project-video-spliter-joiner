using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // T-056 — keep the maximized window clamped to the monitor work area
        // (does not cover the taskbar) via a WM_GETMINMAXINFO hook. SingleBorderWindow +
        // WindowChrome usually handles this, but the hook makes it correct by construction.
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
    }

    // ---------------- Caption interactions ----------------

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click on the caption (non-button area) toggles maximize/restore.
        // Single drag is handled by WindowChrome; DragMove is a fallback for the
        // non-chrome portion and must not run while maximized.
        if (e.ClickCount == 2)
        {
            ToggleMaxRestore();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => ToggleMaxRestore();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaxRestore()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Update the max/restore button tooltip; the glyph itself is bound via converter.
        if (MaxRestoreButton is not null)
        {
            MaxRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }
    }

    // ---------------- Maximize clamp (taskbar-safe) ----------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;     // work area excludes the taskbar
                var full = info.rcMonitor;

                // Position/size the maximized window to the work area, expressed
                // relative to the monitor origin (which is what WM_GETMINMAXINFO wants).
                mmi.ptMaxPosition.x = Math.Abs(work.left - full.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - full.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}

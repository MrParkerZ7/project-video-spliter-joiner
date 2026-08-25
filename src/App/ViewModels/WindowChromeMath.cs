using System;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Pure, WPF/Win32-free math for the custom window chrome (SPEC-015 I23). Extracted from
/// <c>MainWindow</c>'s <c>WM_GETMINMAXINFO</c> P/Invoke handler so the maximized-bounds clamp is
/// unit-testable without a real window or monitor — the handler supplies the raw monitor rects.
/// </summary>
public static class WindowChromeMath
{
    /// <summary>
    /// The maximized-window position + size that fills the monitor WORK area (excluding the taskbar),
    /// expressed relative to the monitor origin — exactly what <c>WM_GETMINMAXINFO</c>'s
    /// <c>ptMaxPosition</c>/<c>ptMaxSize</c> expect. Inputs are the monitor's work rect and the full
    /// monitor rect's origin, in raw device pixels (SPEC-015 I23).
    /// </summary>
    public static (int PosX, int PosY, int SizeX, int SizeY) MaximizedWorkAreaBounds(
        int workLeft, int workTop, int workRight, int workBottom, int fullLeft, int fullTop)
    {
        return (
            Math.Abs(workLeft - fullLeft),
            Math.Abs(workTop - fullTop),
            Math.Abs(workRight - workLeft),
            Math.Abs(workBottom - workTop));
    }
}

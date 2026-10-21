using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AIQuotaBar.App.Interop;

public sealed class TaskbarHost
{
    private readonly Window _window;
    private readonly DispatcherTimer _alignTimer;
    private IntPtr _windowHandle;
    private IntPtr _taskbarHandle;
    private bool _embedded;
    private (int Width, int Height, int Radius) _regionSize;

    public TaskbarHost(Window window)
    {
        _window = window;
        _alignTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _alignTimer.Tick += (_, _) => Reposition();
    }

    public bool IsEmbedded => _embedded;

    public void Attach()
    {
        _windowHandle = new WindowInteropHelper(_window).EnsureHandle();
        var style = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE,
            new IntPtr(style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
        Reposition();
        _alignTimer.Start();
    }

    public void Detach() => _alignTimer.Stop();

    public void Reposition()
    {
        if (!NativeMethods.IsWindow(_windowHandle)) return;
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !NativeMethods.GetWindowRect(taskbar, out var bar))
        {
            NativeMethods.ShowWindow(_windowHandle, 0);
            _taskbarHandle = IntPtr.Zero;
            return;
        }

        var styleChanged = false;
        if (_taskbarHandle != taskbar || NativeMethods.GetParent(_windowHandle) != taskbar)
        {
            styleChanged = true;
            var style = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_STYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_STYLE,
                new IntPtr((style & ~NativeMethods.WS_POPUP) | NativeMethods.WS_CHILD | NativeMethods.WS_CLIPSIBLINGS));
            NativeMethods.SetParent(_windowHandle, taskbar);
            // Null is also success when the previous parent was the desktop.
            _embedded = NativeMethods.GetParent(_windowHandle) == taskbar;
            if (!_embedded)
            {
                NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_STYLE,
                    new IntPtr((style & ~NativeMethods.WS_CHILD) | NativeMethods.WS_POPUP));
            }
            _taskbarHandle = taskbar;
        }

        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        PixelRect? tray = null;
        var trayHandle = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (NativeMethods.GetWindowRect(trayHandle, out var trayRect) && trayRect.Width > 0 && trayRect.Height > 0)
            tray = ToPixels(trayRect);

        var target = TaskbarLayout.Place(ToPixels(bar), tray, _window.Width, _window.Height, scale);
        var monitor = ReadMonitor(taskbar);
        if (monitor.rcMonitor.Width <= 0 || monitor.rcMonitor.Height <= 0) return;
        if (!NativeMethods.IsWindowVisible(taskbar) ||
            Math.Min(bar.Bottom, monitor.rcMonitor.Bottom) - Math.Max(bar.Top, monitor.rcMonitor.Top) <= 2 ||
            Math.Min(bar.Right, monitor.rcMonitor.Right) - Math.Max(bar.Left, monitor.rcMonitor.Left) <= 2)
        {
            NativeMethods.ShowWindow(_windowHandle, 0);
            return;
        }
        if (!_embedded)
            target = TaskbarLayout.PlacePopup(ToPixels(bar), ToPixels(monitor.rcWork), target.Width, target.Height, (int)(6 * scale));

        var origin = new NativeMethods.POINT { X = target.Left, Y = target.Top };
        if (_embedded && !NativeMethods.ScreenToClient(taskbar, ref origin)) return;
        NativeMethods.SetWindowPos(_windowHandle, _embedded ? IntPtr.Zero : NativeMethods.HWND_TOPMOST,
            origin.X, origin.Y, target.Width, target.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW |
            (styleChanged ? NativeMethods.SWP_FRAMECHANGED : 0));
        ApplyRegion(target.Width, target.Height, (int)Math.Round(7 * scale));
    }

    public void PositionFlyout(Window flyout)
    {
        if (!NativeMethods.GetWindowRect(_windowHandle, out var anchor)) return;
        var handle = new WindowInteropHelper(flyout).EnsureHandle();
        var dpi = NativeMethods.GetDpiForWindow(_taskbarHandle);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var monitor = ReadMonitor(_windowHandle);
        if (monitor.rcWork.Width <= 0 || monitor.rcWork.Height <= 0) return;
        var rect = TaskbarLayout.PlacePopup(ToPixels(anchor), ToPixels(monitor.rcWork),
            (int)Math.Round(flyout.Width * scale), (int)Math.Round(flyout.Height * scale), (int)Math.Round(8 * scale));
        NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            NativeMethods.SWP_NOACTIVATE);
    }

    private void ApplyRegion(int width, int height, int radius)
    {
        if (_regionSize == (width, height, radius)) return;
        var region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;
        if (NativeMethods.SetWindowRgn(_windowHandle, region, true) == 0)
            NativeMethods.DeleteObject(region);
        else
            _regionSize = (width, height, radius);
    }

    private static NativeMethods.MONITORINFO ReadMonitor(IntPtr window)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(window, 2), ref info);
        return info;
    }

    private static PixelRect ToPixels(NativeMethods.RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}

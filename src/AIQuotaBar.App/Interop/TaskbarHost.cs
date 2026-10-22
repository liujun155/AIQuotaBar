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
    private (int Width, int Height, int Radius) _regionSize;

    public TaskbarHost(Window window)
    {
        _window = window;
        _alignTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _alignTimer.Tick += (_, _) => Reposition();
    }

    public void Attach()
    {
        _windowHandle = new WindowInteropHelper(_window).EnsureHandle();
        var style = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE,
            new IntPtr(style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));

        // 挂钩 Win32 消息，针对非激活悬浮窗确保鼠标点击正常分发（返回 MA_NOACTIVATE）
        var source = HwndSource.FromHwnd(_windowHandle);
        source?.AddHook(WndProc);

        Reposition();
        _alignTimer.Start();
    }

    public void Detach() => _alignTimer.Stop();

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;

        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }

        return IntPtr.Zero;
    }

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

        if (_taskbarHandle != taskbar)
        {
            _taskbarHandle = taskbar;
            // 将 taskbar 设为 _windowHandle 的 Owner（GWLP_HWNDPARENT），而非 Child（WS_CHILD）
            // 优势：
            // 1. 窗口仍为顶级 WS_POPUP，不进入 Win11 XAML 隔离层，鼠标点击、悬浮、滚轮 100% 原生响应！
            // 2. Win+D 显示桌面时，属于 Taskbar 的 Owned 窗口绝不会被误最小化！
            NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWLP_HWNDPARENT, taskbar);
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

        // 如果任务栏隐藏、自动收起或不可见，同步隐藏胶囊
        if (!NativeMethods.IsWindowVisible(taskbar) ||
            Math.Min(bar.Bottom, monitor.rcMonitor.Bottom) - Math.Max(bar.Top, monitor.rcMonitor.Top) <= 2 ||
            Math.Min(bar.Right, monitor.rcMonitor.Right) - Math.Max(bar.Left, monitor.rcMonitor.Left) <= 2)
        {
            NativeMethods.ShowWindow(_windowHandle, 0);
            return;
        }

        // 检查前台窗口是否处于全屏状态（例如全屏观看视频、全屏游戏），若是则自动避让隐藏
        if (IsForegroundFullScreen(monitor.rcMonitor, taskbar))
        {
            NativeMethods.ShowWindow(_windowHandle, 0);
            return;
        }

        // 胶囊作为任务栏上方的顶级置顶悬浮窗（Topmost WS_POPUP），绝对物理坐标对齐任务栏托盘左侧
        NativeMethods.SetWindowPos(_windowHandle, NativeMethods.HWND_TOPMOST,
            target.Left, target.Top, target.Width, target.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        ApplyRegion(target.Width, target.Height, (int)Math.Round(7 * scale));
    }

    private bool IsForegroundFullScreen(NativeMethods.RECT monitorRect, IntPtr taskbar)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _windowHandle || fg == taskbar) return false;

        var shellWindow = NativeMethods.GetShellWindow();
        if (fg == shellWindow) return false;

        var className = new System.Text.StringBuilder(256);
        NativeMethods.GetClassName(fg, className, 256);
        var cls = className.ToString();
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;

        if (NativeMethods.GetWindowRect(fg, out var fgRect))
        {
            if (fgRect.Left <= monitorRect.Left && fgRect.Top <= monitorRect.Top &&
                fgRect.Right >= monitorRect.Right && fgRect.Bottom >= monitorRect.Bottom)
            {
                return true;
            }
        }

        return false;
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

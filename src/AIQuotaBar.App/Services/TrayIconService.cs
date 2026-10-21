using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AIQuotaBar.App.Interop;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Scheduling;

namespace AIQuotaBar.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const int TrayIconId = 2026;
    private readonly uint _taskbarCreatedMsg;
    private readonly Action _onToggleFlyout;
    private readonly Action _onOpenSettings;
    private readonly Action _onReattachCapsule;
    private readonly RefreshCoordinator _coordinator;

    private HwndSource? _hwndSource;
    private IntPtr _iconHandle;
    private bool _isRegistered;
    private ContextMenu? _trayMenu;

    public TrayIconService(
        RefreshCoordinator coordinator,
        Action onToggleFlyout,
        Action onOpenSettings,
        Action onReattachCapsule)
    {
        _coordinator = coordinator;
        _onToggleFlyout = onToggleFlyout;
        _onOpenSettings = onOpenSettings;
        _onReattachCapsule = onReattachCapsule;

        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        CreateMessageWindow();
        LoadTrayIcon();
        RegisterTrayIcon();
        BuildContextMenu();

        _coordinator.SnapshotUpdated += OnSnapshotUpdated;
        _coordinator.ActiveProviderChanged += _ => UpdateTooltipFromCurrent();
        UpdateTooltipFromCurrent();
    }

    private void CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters("AIQuotaBar_TrayMessageHost")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    private void LoadTrayIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                NativeMethods.ExtractIconEx(exePath, 0, out var large, out var small, 1);
                if (small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero) NativeMethods.DestroyIcon(large);
                    _iconHandle = small;
                    return;
                }
                if (large != IntPtr.Zero)
                {
                    _iconHandle = large;
                    return;
                }
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var icoPath = Path.Combine(baseDir, "Assets", "app.ico");
            if (File.Exists(icoPath))
            {
                NativeMethods.ExtractIconEx(icoPath, 0, out var large, out var small, 1);
                if (small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero) NativeMethods.DestroyIcon(large);
                    _iconHandle = small;
                    return;
                }
                if (large != IntPtr.Zero)
                {
                    _iconHandle = large;
                    return;
                }
            }
        }
        catch
        {
            _iconHandle = IntPtr.Zero;
        }
    }

    private void RegisterTrayIcon()
    {
        if (_hwndSource == null || _hwndSource.Handle == IntPtr.Zero) return;

        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwndSource.Handle,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYCALLBACK,
            hIcon = _iconHandle,
            szTip = "AI Quota Bar · 任务栏配额监控",
        };

        _isRegistered = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
    }

    public void UpdateTooltip(string text)
    {
        if (!_isRegistered || _hwndSource == null) return;

        var safeText = text.Length > 125 ? text.Substring(0, 125) : text;
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwndSource.Handle,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_TIP,
            szTip = safeText,
        };

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private void UpdateTooltipFromCurrent()
    {
        var snapshot = _coordinator.CurrentSnapshot;
        if (snapshot == null)
        {
            UpdateTooltip("AI Quota Bar · 正在拉取配额...");
            return;
        }

        if (snapshot.Error != UsageErrorKind.None)
        {
            UpdateTooltip($"AI Quota Bar · {snapshot.DisplayName}\n状态: {snapshot.ErrorMessage ?? snapshot.Error.ToString()}");
            return;
        }

        var p = snapshot.PrimaryWindow;
        var s = snapshot.SecondaryWindow;
        var text = $"AI Quota Bar · {snapshot.DisplayName}";
        if (p != null)
        {
            text += $"\n{p.Label}: {(int)Math.Round(p.RemainingPercent)}% 剩余";
        }
        if (s != null)
        {
            text += $" | {s.Label}: {(int)Math.Round(s.RemainingPercent)}%";
        }

        UpdateTooltip(text);
    }

    private void OnSnapshotUpdated(UsageSnapshot snapshot)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() => UpdateTooltipFromCurrent());
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var openFlyoutItem = new MenuItem { Header = "📊 打开配额详情" };
        openFlyoutItem.Click += (_, _) => _onToggleFlyout();
        menu.Items.Add(openFlyoutItem);

        var refreshItem = new MenuItem { Header = "⚡ 立即刷新" };
        refreshItem.Click += async (_, _) => await _coordinator.RefreshNowAsync();
        menu.Items.Add(refreshItem);

        menu.Items.Add(new Separator());

        var reattachItem = new MenuItem { Header = "🔄 重新吸附任务栏胶囊" };
        reattachItem.Click += (_, _) => _onReattachCapsule();
        menu.Items.Add(reattachItem);

        var settingsItem = new MenuItem { Header = "⚙️ 打开设置中心..." };
        settingsItem.Click += (_, _) => _onOpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "✕ 退出程序" };
        exitItem.Click += (_, _) => Application.Current?.Shutdown();
        menu.Items.Add(exitItem);

        _trayMenu = menu;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            // Windows Explorer 重启时，重新注册托盘图标并通知胶囊重新吸附到任务栏
            RegisterTrayIcon();
            _onReattachCapsule();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_TRAYCALLBACK)
        {
            var eventType = lParam.ToInt32();
            switch (eventType)
            {
                case NativeMethods.WM_LBUTTONUP:
                case NativeMethods.WM_LBUTTONDBLCLK:
                    _onToggleFlyout();
                    handled = true;
                    break;

                case NativeMethods.WM_RBUTTONUP:
                    ShowContextMenu();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_trayMenu == null || _hwndSource == null) return;

        NativeMethods.SetForegroundWindow(_hwndSource.Handle);
        _trayMenu.Placement = PlacementMode.MousePoint;
        _trayMenu.IsOpen = true;
    }

    public void Dispose()
    {
        _coordinator.SnapshotUpdated -= OnSnapshotUpdated;

        if (_isRegistered && _hwndSource != null && _hwndSource.Handle != IntPtr.Zero)
        {
            var data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwndSource.Handle,
                uID = TrayIconId,
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _isRegistered = false;
        }

        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}

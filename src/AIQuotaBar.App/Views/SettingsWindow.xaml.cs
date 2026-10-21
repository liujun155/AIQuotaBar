using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AIQuotaBar.App.Converters;
using AIQuotaBar.App.Interop;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Providers;
using AIQuotaBar.Core.Scheduling;
using AIQuotaBar.Core.Settings;

namespace AIQuotaBar.App.Views;

public partial class SettingsWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly AppSettings _settings;
    private readonly RefreshCoordinator _coordinator;

    public SettingsWindow(AppSettings settings, RefreshCoordinator coordinator)
    {
        InitializeComponent();
        _settings = settings;
        _coordinator = coordinator;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
        };

        Loaded += (_, _) => LoadSettingsToUI();
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (PanelPolling == null) return;

        PanelPolling.Visibility = NavBtnPolling.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelProviders.Visibility = NavBtnProviders.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelNetwork.Visibility = NavBtnNetwork.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAppearance.Visibility = NavBtnAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelGeneral.Visibility = NavBtnGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadSettingsToUI()
    {
        SelectComboByTag(CmbNormalInterval, _settings.NormalIntervalSeconds);
        ChkLowQuotaAccelerate.IsChecked = _settings.LowQuotaAccelerateEnabled;
        SelectComboByTag(CmbTriggerThreshold, (int)_settings.LowQuotaThresholdPercent);
        SelectComboByTag(CmbAcceleratedInterval, _settings.AcceleratedIntervalSeconds);
        ChkHonorRetryAfter.IsChecked = _settings.HonorRetryAfter;

        SelectComboByTag(CmbGreenThreshold, (int)_settings.GreenThresholdPercent);
        SelectComboByTag(CmbYellowThreshold, (int)_settings.YellowThresholdPercent);

        ChkEnableCodex.IsChecked = _settings.EnabledProviders.Contains(ProviderKind.Codex);
        ChkEnableClaude.IsChecked = _settings.EnabledProviders.Contains(ProviderKind.Claude);
        ChkEnableGemini.IsChecked = _settings.EnabledProviders.Contains(ProviderKind.Gemini);

        // 网络代理通道设置
        SelectComboByStringTag(CmbProxyMode, _settings.ProxyMode.ToString());
        TxtCustomProxyUrl.Text = string.IsNullOrWhiteSpace(_settings.CustomProxyUrl)
            ? NetworkProxyHelper.DefaultProxyUrl
            : _settings.CustomProxyUrl;
        UpdateCustomProxyCardVisibility();

        ChkStartWithWindows.IsChecked = AutoStartup.IsEnabled();
    }

    private void OnProxyModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomProxyCardVisibility();
    }

    internal void ShowNetworkTabForExport()
    {
        NavBtnNetwork.IsChecked = true;
        OnNavChanged(this, new RoutedEventArgs());
        CmbProxyMode.SelectedIndex = 1;
        UpdateCustomProxyCardVisibility();
        UpdateLayout();
    }

    private void UpdateCustomProxyCardVisibility()
    {
        if (CardCustomProxy == null || CmbProxyMode == null) return;
        var tag = (CmbProxyMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        CardCustomProxy.Visibility = tag == "Custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnTestProxyClicked(object sender, RoutedEventArgs e)
    {
        BtnTestProxy.IsEnabled = false;
        TxtProxyTestStatus.Text = "⏳ 正在通过所选代理连接 OpenAI / ChatGPT 接口...";
        TxtProxyTestStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));

        var tag = (CmbProxyMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";
        var mode = Enum.TryParse<AppProxyMode>(tag, out var parsedMode) ? parsedMode : AppProxyMode.Auto;
        var customUrl = TxtCustomProxyUrl.Text?.Trim();

        var (success, message, elapsedMs) = await NetworkProxyHelper.TestConnectivityAsync(mode, customUrl);

        BtnTestProxy.IsEnabled = true;
        if (success)
        {
            TxtProxyTestStatus.Text = $"✓ {message}，握手耗时: {elapsedMs}ms";
            TxtProxyTestStatus.Foreground = PercentToColorConverter.GreenBrush;
        }
        else
        {
            TxtProxyTestStatus.Text = $"✗ {message} ({elapsedMs}ms)";
            TxtProxyTestStatus.Foreground = PercentToColorConverter.RedBrush;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        _settings.NormalIntervalSeconds = GetSelectedComboTag(CmbNormalInterval, 120);
        _settings.LowQuotaAccelerateEnabled = ChkLowQuotaAccelerate.IsChecked == true;
        _settings.LowQuotaThresholdPercent = GetSelectedComboTag(CmbTriggerThreshold, 20);
        _settings.AcceleratedIntervalSeconds = GetSelectedComboTag(CmbAcceleratedInterval, 60);
        _settings.HonorRetryAfter = ChkHonorRetryAfter.IsChecked == true;

        _settings.GreenThresholdPercent = GetSelectedComboTag(CmbGreenThreshold, 50);
        _settings.YellowThresholdPercent = GetSelectedComboTag(CmbYellowThreshold, 20);

        _settings.EnabledProviders.Clear();
        if (ChkEnableCodex.IsChecked == true) _settings.EnabledProviders.Add(ProviderKind.Codex);
        if (ChkEnableClaude.IsChecked == true) _settings.EnabledProviders.Add(ProviderKind.Claude);
        if (ChkEnableGemini.IsChecked == true) _settings.EnabledProviders.Add(ProviderKind.Gemini);

        // 保存网络代理配置
        var proxyTag = (CmbProxyMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";
        _settings.ProxyMode = Enum.TryParse<AppProxyMode>(proxyTag, out var parsed) ? parsed : AppProxyMode.Auto;
        _settings.CustomProxyUrl = string.IsNullOrWhiteSpace(TxtCustomProxyUrl.Text)
            ? NetworkProxyHelper.DefaultProxyUrl
            : TxtCustomProxyUrl.Text.Trim();

        var startWithWin = ChkStartWithWindows.IsChecked == true;
        _settings.StartWithWindows = startWithWin;
        AutoStartup.SetEnabled(startWithWin);

        _settings.Save();

        TxtSaveFeedback.Text = "✓ 配置已成功保存并立即生效！";
        TxtSaveFeedback.Foreground = Brushes.LightGreen;

        _ = _coordinator.RefreshNowAsync();
    }

    private void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        var def = new AppSettings();
        _settings.NormalIntervalSeconds = def.NormalIntervalSeconds;
        _settings.LowQuotaAccelerateEnabled = def.LowQuotaAccelerateEnabled;
        _settings.LowQuotaThresholdPercent = def.LowQuotaThresholdPercent;
        _settings.AcceleratedIntervalSeconds = def.AcceleratedIntervalSeconds;
        _settings.HonorRetryAfter = def.HonorRetryAfter;
        _settings.GreenThresholdPercent = def.GreenThresholdPercent;
        _settings.YellowThresholdPercent = def.YellowThresholdPercent;
        _settings.ProxyMode = def.ProxyMode;
        _settings.CustomProxyUrl = def.CustomProxyUrl;

        LoadSettingsToUI();
        TxtSaveFeedback.Text = "已重置为默认值，点击“保存并应用”后生效。";
    }

    private static void SelectComboByTag(ComboBox box, int target)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag != null && int.TryParse(item.Tag.ToString(), out var v) && v == target)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectComboByStringTag(ComboBox box, string target)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag != null && string.Equals(item.Tag.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static int GetSelectedComboTag(ComboBox box, int fallback)
    {
        if (box.SelectedItem is ComboBoxItem item && item.Tag != null && int.TryParse(item.Tag.ToString(), out var v))
        {
            return v;
        }
        return fallback;
    }
}

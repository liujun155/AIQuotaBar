using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Diagnostics;
using AIQuotaBar.App.Converters;
using AIQuotaBar.App.Interop;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Scheduling;
using AIQuotaBar.Core.Settings;

namespace AIQuotaBar.App.Views;

public partial class CapsuleWindow : Window
{
    private readonly RefreshCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly TaskbarHost _host;
    private FlyoutWindow? _flyoutWindow;
    private SettingsWindow? _settingsWindow;
    private TaskbarMouseInput? _mouseInput;
    private QuotaToolTipWindow? _quotaToolTip;
    private readonly Stopwatch _hoverDuration = new();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private static readonly BitmapImage CodexIcon = new(new Uri("pack://application:,,,/AIQuotaBar.App;component/Assets/codex.png"));
    private static readonly BitmapImage ClaudeIcon = new(new Uri("pack://application:,,,/AIQuotaBar.App;component/Assets/claude.png"));
    private static readonly BitmapImage GeminiIcon = new(new Uri("pack://application:,,,/AIQuotaBar.App;component/Assets/gemini.png"));

    public CapsuleWindow(RefreshCoordinator coordinator, AppSettings settings)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _settings = settings;

        _host = new TaskbarHost(this);

        BuildContextMenu();
        _hoverTimer.Tick += (_, _) => CheckToolTipHover();

        Loaded += (_, _) =>
        {
            _host.Attach();
            _hoverTimer.Start();
            _mouseInput = new TaskbarMouseInput(this, ToggleFlyout, () =>
            {
                if (ContextMenu == null) return;
                HideQuotaToolTip();
                ContextMenu.PlacementTarget = this;
                ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                ContextMenu.IsOpen = true;
            }, SwitchProvider);
            _coordinator.SnapshotUpdated += OnSnapshotUpdated;
            _coordinator.ActiveProviderChanged += OnActiveProviderChanged;

            var initial = _coordinator.CurrentSnapshot;
            if (initial != null)
            {
                UpdateUI(initial);
            }
        };

        Closing += (_, _) =>
        {
            _host.Detach();
            _hoverTimer.Stop();
            _quotaToolTip?.Close();
            _mouseInput?.Dispose();
            _flyoutWindow?.Close();
            _settingsWindow?.Close();
            _coordinator.SnapshotUpdated -= OnSnapshotUpdated;
            _coordinator.ActiveProviderChanged -= OnActiveProviderChanged;
        };
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var refreshItem = new MenuItem { Header = "⚡ 立即刷新" };
        refreshItem.Click += async (_, _) => await _coordinator.RefreshNowAsync();
        menu.Items.Add(refreshItem);

        menu.Items.Add(new Separator());

        var switchMenu = new MenuItem { Header = "🌐 切换 AI 模型平台" };

        var codexHeader = new StackPanel { Orientation = Orientation.Horizontal };
        codexHeader.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/AIQuotaBar.App;component/Assets/codex.png")),
            Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0),
        });
        codexHeader.Children.Add(new TextBlock { Text = "OpenAI Codex", VerticalAlignment = VerticalAlignment.Center });
        var codexItem = new MenuItem { Header = codexHeader, IsCheckable = true };
        codexItem.Click += (_, _) => _coordinator.ActiveProviderKind = ProviderKind.Codex;

        var claudeHeader = new StackPanel { Orientation = Orientation.Horizontal };
        var claudeImg = new Image { Source = ClaudeIcon, Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0) };
        RenderOptions.SetBitmapScalingMode(claudeImg, BitmapScalingMode.HighQuality);
        claudeHeader.Children.Add(claudeImg);
        claudeHeader.Children.Add(new TextBlock { Text = "Claude Code", VerticalAlignment = VerticalAlignment.Center });
        var claudeItem = new MenuItem { Header = claudeHeader, IsCheckable = true };
        claudeItem.Click += (_, _) => _coordinator.ActiveProviderKind = ProviderKind.Claude;

        var geminiHeader = new StackPanel { Orientation = Orientation.Horizontal };
        var geminiImg = new Image { Source = GeminiIcon, Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0) };
        RenderOptions.SetBitmapScalingMode(geminiImg, BitmapScalingMode.HighQuality);
        geminiHeader.Children.Add(geminiImg);
        geminiHeader.Children.Add(new TextBlock { Text = "Google Gemini", VerticalAlignment = VerticalAlignment.Center });
        var geminiItem = new MenuItem { Header = geminiHeader, IsCheckable = true };
        geminiItem.Click += (_, _) => _coordinator.ActiveProviderKind = ProviderKind.Gemini;

        switchMenu.Items.Add(codexItem);
        switchMenu.Items.Add(claudeItem);
        switchMenu.Items.Add(geminiItem);

        switchMenu.SubmenuOpened += (_, _) =>
        {
            codexItem.IsChecked = _coordinator.ActiveProviderKind == ProviderKind.Codex;
            claudeItem.IsChecked = _coordinator.ActiveProviderKind == ProviderKind.Claude;
            geminiItem.IsChecked = _coordinator.ActiveProviderKind == ProviderKind.Gemini;
        };

        menu.Items.Add(switchMenu);

        var settingsItem = new MenuItem { Header = "⚙️ 打开设置中心..." };
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "✕ 退出程序" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        ContextMenu = menu;
    }

    private void OnActiveProviderChanged(ProviderKind kind)
    {
        Dispatcher.Invoke(() =>
        {
            SetProviderIcon(kind);

            var cached = _coordinator.GetSnapshot(kind);
            if (cached != null)
            {
                UpdateUI(cached);
            }
        });
    }

    private void SetProviderIcon(ProviderKind kind)
    {
        ImgProviderIcon.Source = kind switch
        {
            ProviderKind.Codex => CodexIcon,
            ProviderKind.Claude => ClaudeIcon,
            ProviderKind.Gemini => GeminiIcon,
            _ => CodexIcon,
        };
    }

    private void OnSnapshotUpdated(UsageSnapshot snapshot)
    {
        Dispatcher.Invoke(() => UpdateUI(snapshot));
    }

    private void UpdateUI(UsageSnapshot snapshot)
    {
        if (snapshot.ProviderKind != _coordinator.ActiveProviderKind) return;
        SetProviderIcon(snapshot.ProviderKind);

        _quotaToolTip?.UpdateSnapshot(snapshot, _coordinator.ActiveProviderKind, _settings);

        // A failed request or a provider with only one window must not retain another account's values.
        TxtTag1.Text = snapshot.PrimaryWindow?.Label ?? "--";
        TxtTag2.Text = snapshot.SecondaryWindow?.Label ?? "--";
        TxtTag1.Width = snapshot.PrimaryWindow?.Kind == QuotaWindowKind.ModelQuota ? 26 : 14;
        TxtTag2.Width = snapshot.SecondaryWindow?.Kind == QuotaWindowKind.ModelQuota ? 26 : 14;
        CapsuleSecondaryRow.Visibility = snapshot.SecondaryWindow == null ? Visibility.Collapsed : Visibility.Visible;
        TxtVal1.Text = TxtVal2.Text = "--";
        Prog1.Value = Prog2.Value = 0;
        TxtVal1.Foreground = TxtVal2.Foreground = Brushes.Gray;

        var w1 = snapshot.PrimaryWindow;
        if (w1 != null)
        {
            TxtTag1.Text = w1.Label;
            Prog1.Value = w1.RemainingPercent;
            TxtVal1.Text = $"{(int)Math.Round(w1.RemainingPercent)}%";

            var brush1 = GetBrushForPercent(w1.RemainingPercent);
            Prog1.Foreground = brush1;
            TxtVal1.Foreground = brush1;
        }

        var w2 = snapshot.SecondaryWindow;
        if (w2 != null)
        {
            TxtTag2.Text = w2.Label;
            Prog2.Value = w2.RemainingPercent;
            TxtVal2.Text = $"{(int)Math.Round(w2.RemainingPercent)}%";

            var brush2 = GetBrushForPercent(w2.RemainingPercent);
            Prog2.Foreground = brush2;
            TxtVal2.Foreground = brush2;
        }

    }

    private void CheckToolTipHover()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_flyoutWindow?.IsVisible == true || _settingsWindow?.IsVisible == true || ContextMenu?.IsOpen == true ||
            !NativeMethods.IsWindowVisible(handle) || !NativeMethods.GetCursorPos(out var cursor) ||
            !NativeMethods.GetWindowRect(handle, out var rect) ||
            cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom ||
            NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(cursor), 2) != NativeMethods.GetAncestor(handle, 2))
        {
            HideQuotaToolTip();
            return;
        }

        if (!_hoverDuration.IsRunning) _hoverDuration.Start();
        if (_hoverDuration.ElapsedMilliseconds < 350) return;
        _quotaToolTip ??= new QuotaToolTipWindow();
        _quotaToolTip.UpdateSnapshot(_coordinator.CurrentSnapshot, _coordinator.ActiveProviderKind, _settings);
        if (!_quotaToolTip.IsVisible)
        {
            _host.PositionFlyout(_quotaToolTip);
            _quotaToolTip.Show();
        }
        _host.PositionFlyout(_quotaToolTip);
    }

    private void HideQuotaToolTip()
    {
        _hoverDuration.Reset();
        _quotaToolTip?.Hide();
    }

    private SolidColorBrush GetBrushForPercent(double percent)
    {
        if (percent >= _settings.GreenThresholdPercent)
        {
            return PercentToColorConverter.GreenBrush;
        }

        if (percent >= _settings.YellowThresholdPercent)
        {
            return PercentToColorConverter.YellowBrush;
        }

        return PercentToColorConverter.RedBrush;
    }

    private void OnCapsuleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            ToggleFlyout();
        }
    }

    private void OnMouseWheelSwitched(object sender, MouseWheelEventArgs e)
    {
        SwitchProvider(e.Delta);
        e.Handled = true;
    }

    private void SwitchProvider(int delta)
    {
        var providers = _settings.EnabledProviders.Distinct().ToArray();
        if (providers.Length == 0 || delta == 0) return;
        var index = Array.IndexOf(providers, _coordinator.ActiveProviderKind);
        if (index < 0) index = 0;

        if (delta < 0)
        {
            index = (index + 1) % providers.Length;
        }
        else if (delta > 0)
        {
            index = (index - 1 + providers.Length) % providers.Length;
        }

        _coordinator.ActiveProviderKind = providers[index];
    }

    private void ToggleFlyout()
    {
        HideQuotaToolTip();
        if (_flyoutWindow == null || !_flyoutWindow.IsLoaded)
        {
            _flyoutWindow = new FlyoutWindow(_coordinator, _settings, OpenSettings);
        }

        if (_flyoutWindow.IsVisible)
        {
            _flyoutWindow.Hide();
        }
        else
        {
            _host.PositionFlyout(_flyoutWindow);
            _flyoutWindow.Show();
            _host.PositionFlyout(_flyoutWindow);
            _flyoutWindow.Activate();
        }
    }

    public void OpenSettings()
    {
        HideQuotaToolTip();
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_settings, _coordinator);
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }
}

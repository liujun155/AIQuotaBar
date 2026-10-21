using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIQuotaBar.App.Converters;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Scheduling;
using AIQuotaBar.Core.Settings;

namespace AIQuotaBar.App.Views;

public partial class FlyoutWindow : Window
{
    private readonly RefreshCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly Action _openSettingsAction;
    private readonly System.Windows.Threading.DispatcherTimer _countdownTimer;
    private Storyboard? _spinStoryboard;

    public FlyoutWindow(RefreshCoordinator coordinator, AppSettings settings, Action openSettingsAction)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _settings = settings;
        _openSettingsAction = openSettingsAction;

        _countdownTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _countdownTimer.Tick += (_, _) => UpdateCountdowns();

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                UpdateCountdowns();
                _countdownTimer.Start();
            }
            else
            {
                _countdownTimer.Stop();
            }
        };

        Deactivated += (_, _) => Hide();

        Loaded += (_, _) =>
        {
            _coordinator.SnapshotUpdated += OnSnapshotUpdated;
            _coordinator.RefreshStateChanged += OnRefreshStateChanged;
            _coordinator.ActiveProviderChanged += OnActiveProviderChanged;
            RefreshDisplay();
            if (IsVisible)
            {
                _countdownTimer.Start();
            }
        };

        Closing += (_, _) =>
        {
            _countdownTimer.Stop();
            _coordinator.SnapshotUpdated -= OnSnapshotUpdated;
            _coordinator.RefreshStateChanged -= OnRefreshStateChanged;
            _coordinator.ActiveProviderChanged -= OnActiveProviderChanged;
            StopSpinAnimation();
        };
    }

    private void UpdateCountdowns()
    {
        var snapshot = _coordinator.CurrentSnapshot;
        if (snapshot == null) return;

        var w1 = snapshot.PrimaryWindow;
        if (w1 != null)
        {
            TxtCard1Reset.Text = w1.FormatResetCountdown();
        }

        var w2 = snapshot.SecondaryWindow;
        if (w2 != null)
        {
            TxtCard2Reset.Text = w2.FormatResetCountdown();
        }

        // 当倒计时刚好归零到达“即将重置”时，自动触发一次网络刷新，及时拉取恢复后的额度
        if (w1?.ResetAt.HasValue == true && w1.ResetAt.Value <= DateTimeOffset.Now && w1.ResetAt.Value > DateTimeOffset.Now.AddSeconds(-2))
        {
            _ = _coordinator.RefreshNowAsync();
        }
    }

    private void OnActiveProviderChanged(ProviderKind kind)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateTabStyles(kind);
            var snapshot = _coordinator.GetSnapshot(kind);
            if (snapshot != null)
            {
                ApplySnapshotToUI(snapshot);
            }
        });
    }

    private void OnRefreshStateChanged(ProviderKind kind, bool isRefreshing)
    {
        Dispatcher.Invoke(() =>
        {
            if (isRefreshing && _coordinator.ActiveProviderKind == kind)
            {
                StartSpinAnimation();
                TxtStatusBadge.Text = "正在更新...";
                TxtStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                BdStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x38, 0xBD, 0xF8));
                BdStatusBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x38, 0xBD, 0xF8));
            }
            else if (!isRefreshing)
            {
                StopSpinAnimation();
            }
        });
    }

    private void OnSnapshotUpdated(UsageSnapshot snapshot)
    {
        Dispatcher.Invoke(() =>
        {
            StopSpinAnimation();
            ApplySnapshotToUI(snapshot);
        });
    }

    public void RefreshDisplay()
    {
        var kind = _coordinator.ActiveProviderKind;
        UpdateTabStyles(kind);

        var snapshot = _coordinator.CurrentSnapshot;
        if (snapshot != null)
        {
            ApplySnapshotToUI(snapshot);
        }
        else
        {
            SwitchTo(kind);
        }
    }

    private void SwitchTo(ProviderKind kind)
    {
        // 1. 0 毫秒即时切换高亮
        UpdateTabStyles(kind);

        // 2. 检查是否有历史快照 (Cache-First 零白屏体验)
        var cached = _coordinator.GetSnapshot(kind);
        if (cached != null)
        {
            ApplySnapshotToUI(cached);
            TxtStatusBadge.Text = "正在同步最新...";
            TxtStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
            BdStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x38, 0xBD, 0xF8));
            BdStatusBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x38, 0xBD, 0xF8));
        }
        else
        {
            var displayName = kind switch
            {
                ProviderKind.Codex => "OpenAI Codex",
                ProviderKind.Claude => "Claude Code",
                ProviderKind.Gemini => "Google Gemini",
                _ => kind.ToString(),
            };

            TxtAccountName.Text = $"{displayName} · 正在连接...";
            TxtStatusBadge.Text = "正在获取...";
            TxtStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
            BdStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x38, 0xBD, 0xF8));
            BdStatusBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x38, 0xBD, 0xF8));

            TxtCard1Title.Text = $"{displayName} 额度查询中";
            TxtCard1Val.Text = "加载中...";
            CardProg1.Value = 0;
            TxtCard1Used.Text = "";
            TxtCard1Reset.Text = "请稍候...";

            SecondaryQuotaCard.Visibility = Visibility.Collapsed;
        }

        // 3. 立即启动转动动画
        StartSpinAnimation();

        // 4. 触发底层切换并启动异步刷新
        _coordinator.ActiveProviderKind = kind;
    }

    internal void ApplySnapshotToUIForExport(UsageSnapshot snapshot)
    {
        ApplySnapshotToUI(snapshot);
    }

    private void ApplySnapshotToUI(UsageSnapshot snapshot)
    {
        UpdateTabStyles(snapshot.ProviderKind);

        TxtAccountName.Text = $"{snapshot.AccountName ?? snapshot.DisplayName} · {(snapshot.IsMock ? "本地演示" : snapshot.PlanType ?? "Active")}";

        if (snapshot.Error == UsageErrorKind.None)
        {
            TxtStatusBadge.Text = snapshot.IsMock ? "演示模式" : "连接正常";
            TxtStatusBadge.Foreground = PercentToColorConverter.GreenBrush;
            BdStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x10, 0xB9, 0x81));
            BdStatusBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x10, 0xB9, 0x81));
        }
        else
        {
            TxtStatusBadge.Text = snapshot.ErrorMessage ?? snapshot.Error.ToString();
            TxtStatusBadge.Foreground = PercentToColorConverter.RedBrush;
            BdStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xEF, 0x44, 0x44));
            BdStatusBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xEF, 0x44, 0x44));
        }

        var w1 = snapshot.PrimaryWindow;
        TxtCard1Title.Text = "暂无额度数据";
        TxtCard1Val.Text = "--";
        CardProg1.Value = 0;
        TxtCard1Used.Text = "";
        TxtCard1Reset.Text = "--";
        if (w1 != null)
        {
            TxtCard1Title.Text = w1.Description ?? $"{w1.Label} 滚动配额";
            TxtCard1Val.Text = $"{(int)Math.Round(w1.RemainingPercent)}% 剩余";
            CardProg1.Value = w1.RemainingPercent;
            TxtCard1Used.Text = $"已用 {(int)Math.Round(w1.UsedPercent)}%";
            TxtCard1Reset.Text = w1.FormatResetCountdown();

            var b1 = GetBrushForPercent(w1.RemainingPercent);
            TxtCard1Val.Foreground = b1;
            CardProg1.Foreground = b1;
        }

        var w2 = snapshot.SecondaryWindow;
        SecondaryQuotaCard.Visibility = w2 == null ? Visibility.Collapsed : Visibility.Visible;
        if (w2 != null)
        {
            TxtCard2Title.Text = w2.Description ?? $"{w2.Label} 周期配额";
            TxtCard2Val.Text = $"{(int)Math.Round(w2.RemainingPercent)}% 剩余";
            CardProg2.Value = w2.RemainingPercent;
            TxtCard2Used.Text = $"已用 {(int)Math.Round(w2.UsedPercent)}%";
            TxtCard2Reset.Text = w2.FormatResetCountdown();

            var b2 = GetBrushForPercent(w2.RemainingPercent);
            TxtCard2Val.Foreground = b2;
            CardProg2.Foreground = b2;
        }

        if (_settings.LowQuotaAccelerateEnabled)
        {
            TxtSmartPolicyHint.Text = $"⚡ 额度 < {_settings.LowQuotaThresholdPercent:0}% 时加速为 {_settings.AcceleratedIntervalSeconds}s 刷新";
        }
        else
        {
            TxtSmartPolicyHint.Text = $"⏱️ 常规刷新频率: {_settings.NormalIntervalSeconds} 秒";
        }
    }

    private void StartSpinAnimation()
    {
        if (_spinStoryboard != null) return;

        var da = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(da, RefreshIconPath);
        Storyboard.SetTargetProperty(da, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));

        _spinStoryboard = new Storyboard();
        _spinStoryboard.Children.Add(da);
        _spinStoryboard.Begin();
    }

    private void StopSpinAnimation()
    {
        if (_spinStoryboard != null)
        {
            _spinStoryboard.Stop();
            _spinStoryboard = null;
            RefreshRotate.Angle = 0;
        }
    }

    private void UpdateTabStyles(ProviderKind active)
    {
        var activeBg = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var inactiveBg = Brushes.Transparent;

        TabCodex.Background = active == ProviderKind.Codex ? activeBg : inactiveBg;
        TabCodex.Foreground = active == ProviderKind.Codex ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        TabClaude.Background = active == ProviderKind.Claude ? activeBg : inactiveBg;
        TabClaude.Foreground = active == ProviderKind.Claude ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        TabGemini.Background = active == ProviderKind.Gemini ? activeBg : inactiveBg;
        TabGemini.Foreground = active == ProviderKind.Gemini ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    }

    private SolidColorBrush GetBrushForPercent(double percent)
    {
        if (percent >= _settings.GreenThresholdPercent) return PercentToColorConverter.GreenBrush;
        if (percent >= _settings.YellowThresholdPercent) return PercentToColorConverter.YellowBrush;
        return PercentToColorConverter.RedBrush;
    }

    private void OnTabCodexClicked(object sender, RoutedEventArgs e) => SwitchTo(ProviderKind.Codex);
    private void OnTabClaudeClicked(object sender, RoutedEventArgs e) => SwitchTo(ProviderKind.Claude);
    private void OnTabGeminiClicked(object sender, RoutedEventArgs e) => SwitchTo(ProviderKind.Gemini);

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        StartSpinAnimation();
        await _coordinator.RefreshNowAsync();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        _openSettingsAction();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}

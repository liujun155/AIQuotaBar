using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AIQuotaBar.App.Converters;
using AIQuotaBar.App.Interop;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Settings;

namespace AIQuotaBar.App.Views;

public partial class QuotaToolTipWindow : Window
{
    private static readonly Brush HealthyBrush = new SolidColorBrush(Color.FromRgb(0x27, 0xE4, 0xB0));

    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private DateTimeOffset? _lastResetTime;

    public QuotaToolTipWindow()
    {
        InitializeComponent();

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) =>
        {
            if (_lastResetTime.HasValue)
            {
                TxtReset.Text = FormatReset(_lastResetTime.Value);
            }
        };

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _timer.Start();
            else _timer.Stop();
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE,
                new IntPtr(style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
        };
    }

    public void UpdateSnapshot(UsageSnapshot? snapshot, ProviderKind provider, AppSettings settings)
    {
        var name = provider switch { ProviderKind.Codex => "Codex", ProviderKind.Claude => "Claude", _ => "Gemini" };
        var plan = snapshot?.IsMock == true ? "演示" : snapshot?.PlanType;
        TxtTitle.Text = string.IsNullOrWhiteSpace(plan) ? name : $"{name} · {plan}";
        TxtUpdated.Text = snapshot == null ? "等待更新" : $"{snapshot.FetchedAt.ToLocalTime():HH:mm} 更新";
        UpdateQuota(snapshot?.PrimaryWindow, TxtPrimaryLabel, TxtPrimary, "短期窗口", settings);
        UpdateQuota(snapshot?.SecondaryWindow, TxtSecondaryLabel, TxtSecondary, "周期窗口", settings);
        var reset = snapshot?.Windows.Where(w => w.ResetAt.HasValue)
            .Select(w => w.ResetAt!.Value).OrderBy(t => t).FirstOrDefault();
        _lastResetTime = reset;
        TxtReset.Text = reset.HasValue && reset.Value != default ? FormatReset(reset.Value) : "--";
        var hasError = snapshot != null && !snapshot.IsOk;
        TxtError.Text = hasError ? snapshot!.ErrorMessage ?? snapshot.Error.ToString() : "";
        TxtError.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        var singleQuota = snapshot != null && snapshot.SecondaryWindow == null;
        SecondaryQuotaRow.Height = new GridLength(singleQuota ? 0 : 21);
        TxtSecondaryLabel.Visibility = TxtSecondary.Visibility = singleQuota ? Visibility.Collapsed : Visibility.Visible;
        Height = 112 + (hasError ? 20 : 0) - (singleQuota ? 21 : 0);
    }

    private static void UpdateQuota(QuotaWindow? quota, System.Windows.Controls.TextBlock label,
        System.Windows.Controls.TextBlock value, string fallback, AppSettings settings)
    {
        label.Text = quota?.Label switch
        {
            "5h" => "5 小时窗口", "7d" => "7 天窗口", "1d" => "1 天窗口",
            null => fallback, _ => $"{quota.Label} 窗口",
        };
        if (quota?.Kind == QuotaWindowKind.ModelQuota)
            label.Text = quota.Description?.Replace(" · 模型额度", "") ?? $"Gemini {quota.Label}";
        value.Text = quota == null ? "--" : $"剩余 {quota.RemainingPercent:0}%";
        value.Foreground = quota == null ? Brushes.Gray :
            quota.RemainingPercent >= settings.GreenThresholdPercent ? HealthyBrush :
            quota.RemainingPercent >= settings.YellowThresholdPercent ? PercentToColorConverter.YellowBrush :
            PercentToColorConverter.RedBrush;
    }

    private static string FormatReset(DateTimeOffset reset)
    {
        if (reset <= DateTimeOffset.Now) return "即将重置";
        var diff = reset - DateTimeOffset.Now;
        if (diff.TotalHours < 1)
        {
            return $"{diff.Minutes:D2}m {diff.Seconds:D2}s 后";
        }
        var local = reset.ToLocalTime();
        var days = (local.Date - DateTime.Today).Days;
        return days switch
        {
            0 => $"今天 {local:HH:mm}",
            1 => $"明天 {local:HH:mm}",
            _ => $"{local:M/d HH:mm}",
        };
    }
}

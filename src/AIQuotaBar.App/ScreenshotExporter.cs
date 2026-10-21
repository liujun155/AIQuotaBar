using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIQuotaBar.App.Views;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Scheduling;
using AIQuotaBar.Core.Settings;

namespace AIQuotaBar.App;

public static class ScreenshotExporter
{
    public static void Run()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AIQuotaBar.slnx")))
        {
            dir = dir.Parent;
        }
        var projectRoot = dir?.FullName ?? Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        var outputDir = Path.Combine(projectRoot, "docs", "images");
        Directory.CreateDirectory(outputDir);

        var settings = new AppSettings();
        var dummyProviders = Array.Empty<AIQuotaBar.Core.Providers.IUsageProvider>();
        var coordinator = new RefreshCoordinator(settings, dummyProviders);

        // 1. 设置中心截图 (Settings Preview)
        ExportSettingsWindow(settings, coordinator, Path.Combine(outputDir, "settings-preview.png"));

        // 1.1 设置中心网络与代理截图 (Settings Network Preview)
        ExportSettingsNetworkWindow(settings, coordinator, Path.Combine(outputDir, "settings-network-preview.png"));

        // 2. 详情弹窗截图 (Flyout Preview)
        ExportFlyoutWindow(coordinator, settings, Path.Combine(outputDir, "flyout-preview.png"));

        // 3. 任务栏胶囊微件截图 (Capsule Preview)
        ExportCapsulePreview(Path.Combine(outputDir, "capsule-preview.png"));

        Console.WriteLine($"[ScreenshotExporter] Screenshots generated successfully at: {outputDir}");
    }

    private static void ExportSettingsWindow(AppSettings settings, RefreshCoordinator coordinator, string filePath)
    {
        var win = new SettingsWindow(settings, coordinator)
        {
            Width = 680,
            Height = 580,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        win.Show();
        win.UpdateLayout();

        SaveVisualToPng(win, 680, 580, filePath);
        win.Close();
    }

    private static void ExportSettingsNetworkWindow(AppSettings settings, RefreshCoordinator coordinator, string filePath)
    {
        var win = new SettingsWindow(settings, coordinator)
        {
            Width = 680,
            Height = 580,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        win.Show();
        win.ShowNetworkTabForExport();
        win.UpdateLayout();

        SaveVisualToPng(win, 680, 580, filePath);
        win.Close();
    }

    private static void ExportFlyoutWindow(RefreshCoordinator coordinator, AppSettings settings, string filePath)
    {
        var win = new FlyoutWindow(coordinator, settings, () => { })
        {
            Width = 330,
            Height = 370,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        // 注入饱满的演示数据
        var snapshot = new UsageSnapshot(
            providerId: "codex",
            providerKind: ProviderKind.Codex,
            displayName: "OpenAI Codex",
            accountName: "user@openai.com",
            planType: "ChatGPT Plus",
            windows: new[]
            {
                new QuotaWindow(QuotaWindowKind.SessionShortTerm, "5h 滚动周期", 22.0, DateTimeOffset.Now.AddHours(3).AddMinutes(15)),
                new QuotaWindow(QuotaWindowKind.PeriodLongTerm, "7d 周度周期", 8.0, DateTimeOffset.Now.AddDays(4).AddHours(8))
            },
            fetchedAt: DateTimeOffset.Now
        );

        win.Show();
        win.ApplySnapshotToUIForExport(snapshot);
        win.UpdateLayout();

        SaveVisualToPng(win, 330, 370, filePath);
        win.Close();
    }

    private static void ExportCapsulePreview(string filePath)
    {
        // 构造一个高质感深色展示底板，模拟 Windows 11 任务栏环境
        var container = new Border
        {
            Width = 260,
            Height = 72,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)), // 暗黑质感任务栏背景
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10)
        };

        var rootGrid = new Grid();
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左侧：Capsule 胶囊
        var capsuleBorder = new Border
        {
            Width = 108,
            Height = 34,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x17, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        var capGrid = new Grid();
        capGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        capGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var img = new Image
        {
            Width = 15,
            Height = 15,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = new BitmapImage(new Uri("pack://application:,,,/AIQuotaBar.App;component/Assets/codex.png"))
        };
        Grid.SetColumn(img, 0);
        capGrid.Children.Add(img);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(stack, 1);

        // 上行 5h
        var row1 = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pb1 = new ProgressBar
        {
            Height = 5,
            Value = 78,
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
            Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(pb1, 0);
        row1.Children.Add(pb1);

        var txt1 = new TextBlock
        {
            Text = "78%",
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(txt1, 1);
        row1.Children.Add(txt1);
        stack.Children.Add(row1);

        // 下行 7d
        var row2 = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pb2 = new ProgressBar
        {
            Height = 5,
            Value = 92,
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
            Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(pb2, 0);
        row2.Children.Add(pb2);

        var txt2 = new TextBlock
        {
            Text = "92%",
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(txt2, 1);
        row2.Children.Add(txt2);
        stack.Children.Add(row2);

        capGrid.Children.Add(stack);
        capsuleBorder.Child = capGrid;
        Grid.SetColumn(capsuleBorder, 0);
        rootGrid.Children.Add(capsuleBorder);

        // 右侧：模拟 Windows 任务栏时钟对比
        var clockStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(clockStack, 1);

        var txtTime = new TextBlock
        {
            Text = "14:15",
            FontSize = 11,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var txtDate = new TextBlock
        {
            Text = "2026/10/21",
            FontSize = 10,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        clockStack.Children.Add(txtTime);
        clockStack.Children.Add(txtDate);
        rootGrid.Children.Add(clockStack);

        container.Child = rootGrid;

        // 离屏渲染
        container.Measure(new Size(260, 72));
        container.Arrange(new Rect(0, 0, 260, 72));
        container.UpdateLayout();

        var rtb = new RenderTargetBitmap(260, 72, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(container);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    private static void SaveVisualToPng(Visual visual, int width, int height, string filePath)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }
}

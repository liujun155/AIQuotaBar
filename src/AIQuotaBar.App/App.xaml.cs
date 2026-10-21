using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AIQuotaBar.App.Interop;
using AIQuotaBar.App.Views;
using AIQuotaBar.Core.Providers;
using AIQuotaBar.Core.Scheduling;
using AIQuotaBar.Core.Settings;

namespace AIQuotaBar.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private RefreshCoordinator? _coordinator;
    private CapsuleWindow? _capsule;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args != null && e.Args.Any(a => a.Equals("--export-screenshots", StringComparison.OrdinalIgnoreCase)))
        {
            ScreenshotExporter.Run();
            Shutdown();
            return;
        }

        if (e.Args != null && e.Args.Any(a => a.Equals("--generate-icon", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AIQuotaBar.slnx")))
            {
                dir = dir.Parent;
            }
            var root = dir?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
            IconGenerator.GenerateIcons(System.IO.Path.Combine(root, "src", "AIQuotaBar.App", "Assets"));
            IconGenerator.GenerateIcons(System.IO.Path.Combine(root, "docs", "images"));
            Shutdown();
            return;
        }

        // 1. 禁用 WPF 沉重的 DirectX 3D 显卡驱动渲染上下文，微型任务栏窗口采用纯净软件渲染
        // 此项优化可瞬间节省 60MB ~ 90MB 的显存映射与显卡驱动运行时内存！
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);

        const string mutexName = "AIQuotaBar_SingleInstance_Mutex_2026";
        _singleInstanceMutex = new Mutex(true, mutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("AI Quota Bar 已在后台运行中，请在任务栏右侧通知区查看！", "AI Quota Bar", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();

        var providers = new IUsageProvider[]
        {
            new CodexUsageProvider(),
            new ClaudeUsageProvider(),
            new GeminiUsageProvider(),
        };

        _coordinator = new RefreshCoordinator(settings, providers);
        _coordinator.SnapshotUpdated += _ =>
        {
            // 每次轮询完成后，在后台轻量压缩工作集
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                NativeMethods.TrimWorkingSet();
            });
        };

        _capsule = new CapsuleWindow(_coordinator, settings);
        _capsule.Show();

        _coordinator.Start();

        // 启动 1.5 秒完成 JIT 预热后，自动深度清理并紧缩 Working Set
        var startupTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        startupTrimTimer.Tick += (_, _) =>
        {
            startupTrimTimer.Stop();
            NativeMethods.TrimWorkingSet();
        };
        startupTrimTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _coordinator?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

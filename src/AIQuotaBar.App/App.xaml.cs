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

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app-crash.log"),
                    $"[{DateTime.Now}] AppDomain: {args.ExceptionObject}\n");
            }
            catch { }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app-crash.log"),
                    $"[{DateTime.Now}] Dispatcher: {args.Exception}\n");
            }
            catch { }
        };

        // 1. 禁用 WPF 沉重的 DirectX 3D 显卡驱动渲染上下文，微型任务栏窗口采用纯净软件渲染
        // 此项优化可瞬间节省 60MB ~ 90MB 的显存映射与显卡驱动运行时内存！
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);

        const string mutexName = "AIQuotaBar_SingleInstance_Mutex_2026";
        _singleInstanceMutex = new Mutex(true, mutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("AI Quota Bar 已在运行中，请在任务栏右侧胶囊直接查看或右键退出。", "AI Quota Bar", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();

        var providers = new IUsageProvider[]
        {
            new CodexUsageProvider(proxyConfigProvider: () => (settings.ProxyMode, settings.CustomProxyUrl)),
            new ClaudeUsageProvider(proxyConfigProvider: () => (settings.ProxyMode, settings.CustomProxyUrl)),
            new GeminiUsageProvider(),
        };

        _coordinator = new RefreshCoordinator(settings, providers);
        _capsule = new CapsuleWindow(_coordinator, settings);
        _capsule.Show();

        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _coordinator?.Dispose();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
        Environment.Exit(0);
    }
}

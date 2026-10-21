using System.Text.Json;
using AIQuotaBar.Core.Models;

namespace AIQuotaBar.Core.Settings;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public ProviderKind ActiveProvider { get; set; } = ProviderKind.Codex;

    /// <summary>
    /// 常规刷新轮询间隔（秒），默认 120 秒
    /// </summary>
    public int NormalIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// 是否开启“低额度智能加速刷新”
    /// </summary>
    public bool LowQuotaAccelerateEnabled { get; set; } = true;

    /// <summary>
    /// 触发加速刷新的剩余额度百分比阈值（当任一额度低于此值时加速），默认 20%
    /// </summary>
    public double LowQuotaThresholdPercent { get; set; } = 20.0;

    /// <summary>
    /// 低额度加速后的刷新间隔（秒），默认 60 秒（1 分钟）
    /// </summary>
    public int AcceleratedIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 是否在遇到 HTTP 429 限流时严格按照 Retry-After 倒计时冷却
    /// </summary>
    public bool HonorRetryAfter { get; set; } = true;

    /// <summary>
    /// 绿色充足阈值（>= 此值为绿），默认 50%
    /// </summary>
    public double GreenThresholdPercent { get; set; } = 50.0;

    /// <summary>
    /// 黄色预警阈值（低于此值为红，介于与绿之间为黄），默认 20%
    /// </summary>
    public double YellowThresholdPercent { get; set; } = 20.0;

    /// <summary>
    /// Windows 开机自启
    /// </summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>
    /// 自定义网络代理地址（如 http://127.0.0.1:7897）。若为空，则自动智能探测本机代理
    /// </summary>
    public string? CustomProxyUrl { get; set; } = null;

    /// <summary>
    /// 启用的提供商列表
    /// </summary>
    public List<ProviderKind> EnabledProviders { get; set; } = new()
    {
        ProviderKind.Codex,
        ProviderKind.Claude,
        ProviderKind.Gemini,
    };

    public static string GetSettingsFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "AIQuotaBar");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load()
    {
        var file = GetSettingsFilePath();
        if (File.Exists(file))
        {
            try
            {
                var text = File.ReadAllText(file);
                var loaded = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions);
                if (loaded != null)
                {
                    return loaded;
                }
            }
            catch
            {
            }
        }

        var defaults = new AppSettings();
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        try
        {
            var file = GetSettingsFilePath();
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(file, json);
        }
        catch
        {
        }
    }
}

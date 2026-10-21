using System.Text.Json;

namespace AIQuotaBar.Core.Credentials;

public sealed class GeminiCredentialStore
{
    private static readonly string[] PossiblePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config.json"),
    };

    public string? TryReadToken()
    {
        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return envKey.Trim();
        }

        foreach (var path in PossiblePaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("token", out var tokEl) && tokEl.ValueKind == JsonValueKind.String)
                {
                    return tokEl.GetString();
                }
                if (root.TryGetProperty("api_key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                {
                    return keyEl.GetString();
                }
            }
            catch
            {
            }
        }

        return null;
    }
}

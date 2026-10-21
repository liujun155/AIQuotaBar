using System.Text.Json;

namespace AIQuotaBar.Core.Credentials;

public sealed class ClaudeCredentialStore
{
    private static readonly string[] PossiblePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "auth.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "auth.json"),
    };

    public string? TryReadToken()
    {
        var envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
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
                if (root.TryGetProperty("apiKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                {
                    return keyEl.GetString();
                }
                if (root.TryGetProperty("token", out var tokEl) && tokEl.ValueKind == JsonValueKind.String)
                {
                    return tokEl.GetString();
                }
            }
            catch
            {
            }
        }

        return null;
    }
}

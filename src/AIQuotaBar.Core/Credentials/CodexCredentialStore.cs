using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIQuotaBar.Core.Credentials;

public sealed class CodexCredential
{
    public CodexCredential(string accessToken, string? accountId = null, string? email = null)
    {
        AccessToken = accessToken;
        AccountId = accountId;
        Email = email;
    }

    public string AccessToken { get; }
    public string? AccountId { get; }
    public string? Email { get; }
}

public sealed class CodexCredentialStore
{
    private static readonly string[] PossiblePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "credentials.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "codex", "credentials.json"),
    };

    public CodexCredential? TryRead()
    {
        foreach (var path in PossiblePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? token = null;
                string? accountId = null;
                string? email = null;

                if (root.TryGetProperty("access_token", out var tokEl) && tokEl.ValueKind == JsonValueKind.String)
                {
                    token = tokEl.GetString();
                }
                else if (root.TryGetProperty("token", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                {
                    token = tEl.GetString();
                }
                else if (root.TryGetProperty("tokens", out var tokensEl) && tokensEl.TryGetProperty("access_token", out var subTok))
                {
                    token = subTok.GetString();
                }

                if (root.TryGetProperty("account_id", out var accEl) && accEl.ValueKind == JsonValueKind.String)
                {
                    accountId = accEl.GetString();
                }

                if (root.TryGetProperty("email", out var emEl) && emEl.ValueKind == JsonValueKind.String)
                {
                    email = emEl.GetString();
                }

                if (!string.IsNullOrWhiteSpace(token))
                {
                    return new CodexCredential(token, accountId, email);
                }
            }
            catch
            {
                // 忽略解析失败，尝试下一路径
            }
        }

        return null;
    }
}

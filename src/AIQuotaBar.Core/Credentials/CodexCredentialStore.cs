using System;
using System.IO;
using System.Text.Json;

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
                string? idToken = null;

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
                else if (root.TryGetProperty("tokens", out var tAccEl) && tAccEl.TryGetProperty("account_id", out var subAcc) && subAcc.ValueKind == JsonValueKind.String)
                {
                    accountId = subAcc.GetString();
                }

                if (root.TryGetProperty("email", out var emEl) && emEl.ValueKind == JsonValueKind.String)
                {
                    email = emEl.GetString();
                }

                // 尝试从 id_token (JWT Payload) 中提取真实登录账号 Email
                if (root.TryGetProperty("id_token", out var idTokEl) && idTokEl.ValueKind == JsonValueKind.String)
                {
                    idToken = idTokEl.GetString();
                }
                else if (root.TryGetProperty("tokens", out var tks) && tks.TryGetProperty("id_token", out var subIdTok) && subIdTok.ValueKind == JsonValueKind.String)
                {
                    idToken = subIdTok.GetString();
                }

                if (string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(idToken))
                {
                    email = TryExtractEmailFromJwt(idToken);
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

    private static string? TryExtractEmailFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("email", out var el) && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }
        catch
        {
            // 忽略 JWT 解析错误
        }
        return null;
    }
}

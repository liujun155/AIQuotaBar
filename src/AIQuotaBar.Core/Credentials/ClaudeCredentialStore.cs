using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AIQuotaBar.Core.Credentials;

public sealed class ClaudeCredential
{
    public ClaudeCredential(string token, bool isOAuth = true, string? accountName = null, string? source = null)
    {
        Token = token;
        IsOAuth = isOAuth;
        AccountName = accountName;
        Source = source;
    }

    public string Token { get; }
    public bool IsOAuth { get; }
    public string? AccountName { get; }
    public string? Source { get; }
}

public sealed class ClaudeCredentialStore
{
    private static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string[] PossibleCandidatePaths =
    {
        Path.Combine(UserHome, ".claude", ".credentials.json"),
        Path.Combine(UserHome, ".claude", "auth.json"),
        Path.Combine(UserHome, ".claude", "credentials.json"),
        Path.Combine(UserHome, ".claude.json"),
        Path.Combine(AppData, "Claude", "auth.json"),
        Path.Combine(AppData, "Claude", "credentials.json"),
        Path.Combine(LocalAppData, "Claude", "credentials.json"),
    };

    public ClaudeCredential? TryRead()
    {
        // 1. 优先读取标准环境变量 (支持直接由用户或容器注入)
        var envOauth = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN")
                       ?? Environment.GetEnvironmentVariable("CLAUDE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envOauth))
        {
            return new ClaudeCredential(envOauth.Trim(), isOAuth: true, source: "Environment: CLAUDE_CODE_OAUTH_TOKEN");
        }

        var envApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            return new ClaudeCredential(envApiKey.Trim(), isOAuth: false, source: "Environment: ANTHROPIC_API_KEY");
        }

        // 2. 扫描本机的各种 Claude CLI / App 配置文件
        foreach (var path in PossibleCandidatePaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var content = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(content)) continue;

                var cred = ParseCredentialFromJson(content, path);
                if (cred != null)
                {
                    return cred;
                }
            }
            catch
            {
                // 忽略单个文件访问或解析异常，继续尝试其它探测路径
            }
        }

        // 3. 在 Windows 平台上探测 Windows 凭据管理器 (Credential Manager)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var winCred = TryReadFromWindowsCredentialManager();
            if (winCred != null)
            {
                return winCred;
            }
        }

        return null;
    }

    public string? TryReadToken() => TryRead()?.Token;

    private static ClaudeCredential? ParseCredentialFromJson(string json, string sourcePath)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 尝试提取用户账号/邮箱信息
        string? email = null;
        if (root.TryGetProperty("email", out var emailProp) && emailProp.ValueKind == JsonValueKind.String)
        {
            email = emailProp.GetString();
        }
        else if (root.TryGetProperty("user", out var userProp) && userProp.ValueKind == JsonValueKind.Object
                 && userProp.TryGetProperty("email", out var uEmail) && uEmail.ValueKind == JsonValueKind.String)
        {
            email = uEmail.GetString();
        }

        // 递归或多键查找 OAuth Token / Access Token
        string[] oauthKeys = { "oauth_token", "oauthToken", "accessToken", "access_token", "token", "sessionKey" };
        foreach (var key in oauthKeys)
        {
            if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return new ClaudeCredential(val.Trim(), isOAuth: true, accountName: email, source: sourcePath);
                }
            }
        }

        // 嵌套 oauth 对象探测: { "oauth": { "accessToken": "..." } } 或 { "claude": { "token": "..." } }
        string[] containerKeys = { "oauth", "claude", "auth", "credentials" };
        foreach (var cKey in containerKeys)
        {
            if (root.TryGetProperty(cKey, out var cProp) && cProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in oauthKeys)
                {
                    if (cProp.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return new ClaudeCredential(val.Trim(), isOAuth: true, accountName: email, source: $"{sourcePath} ({cKey})");
                        }
                    }
                }
            }
        }

        // 查找标准 API Key
        string[] apiKeyKeys = { "apiKey", "api_key", "anthropic_api_key", "key" };
        foreach (var key in apiKeyKeys)
        {
            if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (!string.IsNullOrWhiteSpace(val) && val.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase))
                {
                    return new ClaudeCredential(val.Trim(), isOAuth: false, accountName: email, source: sourcePath);
                }
            }
        }

        return null;
    }

    private static ClaudeCredential? TryReadFromWindowsCredentialManager()
    {
        string[] targetNames = { "Claude Code", "claude-code", "Anthropic", "Claude" };
        foreach (var target in targetNames)
        {
            if (NativeCredentialHelper.ReadCredential(target, out var secret, out var user))
            {
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    var isOAuth = !secret.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase);
                    return new ClaudeCredential(secret.Trim(), isOAuth, accountName: user, source: $"Windows Credential: {target}");
                }
            }
        }
        return null;
    }

    private static class NativeCredentialHelper
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        public static bool ReadCredential(string target, out string? password, out string? username)
        {
            password = null;
            username = null;

            // 1 = CRED_TYPE_GENERIC
            if (!CredRead(target, 1, 0, out var credPtr) || credPtr == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                username = cred.UserName;
                if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
                {
                    password = Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
                    // 兼容 UTF-8 / ASCII 保存的情况
                    if (string.IsNullOrEmpty(password) || password.IndexOf('\0') >= 0)
                    {
                        var bytes = new byte[cred.CredentialBlobSize];
                        Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
                        password = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                    }
                }
                return !string.IsNullOrEmpty(password);
            }
            catch
            {
                return false;
            }
            finally
            {
                CredFree(credPtr);
            }
        }
    }
}

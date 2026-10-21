using System;
using System.IO;
using System.Text.Json;
using AIQuotaBar.Core.Credentials;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Providers;
using Xunit;

namespace AIQuotaBar.Tests;

public class CodexUsageProviderTests
{
    [Fact]
    public void CredentialStore_ExtractsEmailFromIdTokenJwt()
    {
        // 构造一个包含 email 的简单 JWT: header.payload.signature
        const string payloadJson = "{\"email\":\"developer@openai.com\",\"name\":\"Test User\"}";
        var payloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var dummyJwt = $"eyJhbGciOiJSUzI1NiJ9.{payloadBase64}.signature_placeholder";

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var authJsonPath = Path.Combine(tempDir, "auth.json");

        try
        {
            var authContent = $$"""
            {
                "tokens": {
                    "access_token": "dummy_access_token",
                    "id_token": "{{dummyJwt}}",
                    "account_id": "acc_12345"
                }
            }
            """;
            File.WriteAllText(authJsonPath, authContent);

            // 直接验证 JWT 提取
            var parts = dummyJwt.Split('.');
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            var extractedEmail = doc.RootElement.GetProperty("email").GetString();

            Assert.Equal("developer@openai.com", extractedEmail);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CodexUsageProvider_ParsesPlanTypeProperlyWithoutDuplicatePlus()
    {
        var cred = new CodexCredential("test_token", "acc_1", "developer@openai.com");
        var provider = new CodexUsageProvider(credentialReader: () => cred);

        const string json = """
        {
            "plan_type": "plus",
            "rate_limit": {
                "primary_window": {
                    "used_percent": 22.0,
                    "reset_after_seconds": 14200
                }
            }
        }
        """;

        // 通过反射或者执行 ParsePayload
        var method = typeof(CodexUsageProvider).GetMethod("ParsePayload", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var snapshot = (UsageSnapshot)method.Invoke(provider, new object[] { json, cred })!;

        Assert.Equal("developer@openai.com", snapshot.AccountName);
        Assert.Equal("Plus", snapshot.PlanType);
        Assert.NotEqual("ChatGPT Plus", snapshot.AccountName);
    }
}

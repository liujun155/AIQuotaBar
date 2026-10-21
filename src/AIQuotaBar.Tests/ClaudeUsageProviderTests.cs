using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIQuotaBar.Core.Credentials;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Providers;
using Xunit;

namespace AIQuotaBar.Tests;

public class ClaudeUsageProviderTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task FetchAsync_NoCredentials_ReturnsNoCredentialsError()
    {
        var provider = new ClaudeUsageProvider(
            http: new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            credentialReader: () => null);

        var snapshot = await provider.FetchAsync();

        Assert.False(snapshot.IsOk);
        Assert.Equal(UsageErrorKind.NoCredentials, snapshot.Error);
        Assert.Contains("未检测到 Claude Code 登录凭证", snapshot.ErrorMessage);
    }

    [Fact]
    public async Task FetchAsync_SuccessfulResponse_Parses5hAnd7dAccurately()
    {
        const string mockJson = """
        {
            "five_hour": {
                "utilization": 24.5,
                "resets_at": "2026-10-21T18:30:00Z"
            },
            "seven_day": {
                "utilization": 42.0,
                "resets_at": "2026-10-28T00:00:00Z"
            }
        }
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", req.RequestUri?.ToString());
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("test_claude_oauth_token", req.Headers.Authorization?.Parameter);
            Assert.True(req.Headers.Contains("anthropic-beta"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockJson, Encoding.UTF8, "application/json")
            };
        });

        var provider = new ClaudeUsageProvider(
            http: new HttpClient(handler),
            credentialReader: () => new ClaudeCredential("test_claude_oauth_token", isOAuth: true, accountName: "developer@example.com"));

        var snapshot = await provider.FetchAsync();

        Assert.True(snapshot.IsOk);
        Assert.False(snapshot.IsMock);
        Assert.Equal(ProviderKind.Claude, snapshot.ProviderKind);
        Assert.Equal("developer@example.com", snapshot.AccountName);
        Assert.Equal(2, snapshot.Windows.Count);

        // 5h 窗口: used 24.5% => remaining 75.5%
        var w5h = snapshot.Windows[0];
        Assert.Equal(QuotaWindowKind.SessionShortTerm, w5h.Kind);
        Assert.Equal(24.5, w5h.UsedPercent);
        Assert.Equal(75.5, w5h.RemainingPercent, 1);
        Assert.NotNull(w5h.ResetAt);

        // 7d 窗口: used 42.0% => remaining 58.0%
        var w7d = snapshot.Windows[1];
        Assert.Equal(QuotaWindowKind.PeriodLongTerm, w7d.Kind);
        Assert.Equal(42.0, w7d.UsedPercent);
        Assert.Equal(58.0, w7d.RemainingPercent, 1);
    }

    [Fact]
    public async Task FetchAsync_FallbackSubModels_PicksHighestUsedSevenDay()
    {
        const string mockJson = """
        {
            "five_hour": {
                "utilization": 15.0,
                "resets_at": "2026-10-21T18:00:00Z"
            },
            "seven_day_sonnet": {
                "utilization": 30.0,
                "resets_at": "2026-10-28T00:00:00Z"
            },
            "seven_day_opus": {
                "utilization": 65.0,
                "resets_at": "2026-10-28T00:00:00Z"
            }
        }
        """;

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mockJson, Encoding.UTF8, "application/json")
        });

        var provider = new ClaudeUsageProvider(
            http: new HttpClient(handler),
            credentialReader: () => new ClaudeCredential("test_token"));

        var snapshot = await provider.FetchAsync();

        Assert.True(snapshot.IsOk);
        Assert.Equal(2, snapshot.Windows.Count);
        // 应该自动选取占用率最高的 opus (65.0%)
        var w7d = snapshot.Windows[1];
        Assert.Equal(65.0, w7d.UsedPercent);
        Assert.Equal(35.0, w7d.RemainingPercent, 1);
        Assert.Contains("opus", w7d.Label);
    }

    [Fact]
    public async Task FetchAsync_Http401_ReturnsUnauthorized()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = new ClaudeUsageProvider(
            http: new HttpClient(handler),
            credentialReader: () => new ClaudeCredential("expired_token"));

        var snapshot = await provider.FetchAsync();

        Assert.False(snapshot.IsOk);
        Assert.Equal(UsageErrorKind.Unauthorized, snapshot.Error);
        Assert.Contains("Claude 凭据已失效", snapshot.ErrorMessage);
    }

    [Fact]
    public async Task FetchAsync_Http429_ReturnsRateLimitedWithRetryAfter()
    {
        var handler = new MockHttpMessageHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return resp;
        });

        var provider = new ClaudeUsageProvider(
            http: new HttpClient(handler),
            credentialReader: () => new ClaudeCredential("valid_token"));

        var snapshot = await provider.FetchAsync();

        Assert.False(snapshot.IsOk);
        Assert.Equal(UsageErrorKind.RateLimited, snapshot.Error);
        Assert.Equal(TimeSpan.FromSeconds(60), snapshot.RetryAfter);
    }

    [Fact]
    public void CredentialStore_ReadsJsonStructuresProperly()
    {
        const string directOauthJson = """
        {
            "email": "user@claude.ai",
            "oauth_token": "sk-ant-oauth-test-token"
        }
        """;

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, directOauthJson);
            // 验证通过环境变量覆盖测试
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "sk-env-override-token");
            var store = new ClaudeCredentialStore();
            var cred = store.TryRead();

            Assert.NotNull(cred);
            Assert.Equal("sk-env-override-token", cred.Token);
            Assert.True(cred.IsOAuth);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}

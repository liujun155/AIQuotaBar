using System.Net;
using System.Text;
using AIQuotaBar.Core.Credentials;
using AIQuotaBar.Core.Models;
using AIQuotaBar.Core.Providers;

namespace AIQuotaBar.Tests;

public class GeminiUsageProviderTests
{
    private const string ModelStatus = """{"userStatus":{"planStatus":{"planInfo":{"planName":"Pro"}},"cascadeModelConfigData":{"clientModelConfigs":[{"label":"Gemini Flash","quotaInfo":{"remainingFraction":0.9}}]}}}""";

    private static HttpClient WithSummary(Func<HttpResponseMessage> summary) => new(new Handler(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/RetrieveUserQuotaSummary") ? summary() : Json(ModelStatus)));

    [Fact]
    public async Task SummaryShowsSessionThenWeeklyInsteadOfModelFamilies()
    {
        using var http = WithSummary(() => Json("""
            {"response":{"groups":[
              {"displayName":"Claude and GPT models","buckets":[{"window":"weekly","remainingFraction":0.1}]},
              {"displayName":"Gemini Models","buckets":[
                {"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.895,"resetTime":"2026-10-26T05:41:45Z"},
                {"bucketId":"gemini-5h","window":"5h","remainingFraction":0.901,"resetTime":"2026-10-21T06:38:26Z"}]}]}}
            """));
        var snapshot = await new GeminiUsageProvider(http, _ => [new(12345, "test")]).FetchAsync();
        Assert.True(snapshot.IsOk);
        Assert.False(snapshot.IsMock);
        Assert.Equal("Pro", snapshot.PlanType);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("5h", snapshot.PrimaryWindow!.Label);
        Assert.Equal("7d", snapshot.SecondaryWindow!.Label);
        Assert.Equal(90.1, snapshot.PrimaryWindow.RemainingPercent, 5);
        Assert.Equal(89.5, snapshot.SecondaryWindow.RemainingPercent, 5);
        Assert.Equal(QuotaWindowKind.SessionShortTerm, snapshot.PrimaryWindow.Kind);
        Assert.Equal(QuotaWindowKind.PeriodLongTerm, snapshot.SecondaryWindow.Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T05:41:45Z"), snapshot.SecondaryWindow.ResetAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OlderServerKeepsKnownModelQuotaWithoutInventingWeekly(HttpStatusCode status)
    {
        using var http = WithSummary(() => new HttpResponseMessage(status));
        var snapshot = await new GeminiUsageProvider(http, _ => [new(12345, "test")]).FetchAsync();
        Assert.True(snapshot.IsOk);
        Assert.Single(snapshot.Windows);
        Assert.Equal(QuotaWindowKind.ModelQuota, snapshot.PrimaryWindow!.Kind);
    }

    [Fact]
    public async Task SummarySkipsDisabledUnknownAndUnsupportedBuckets()
    {
        using var http = WithSummary(() => Json("""
            {"response":{"groups":[{"displayName":"Gemini Models","buckets":[
              {"window":"weekly","remainingFraction":0.5,"disabled":true},
              {"window":"weekly"},
              {"window":"monthly","remainingFraction":1},
              {"bucketId":"gemini-5h","resetTime":"2026-10-21T06:38:26Z"}]}]}}
            """));
        var snapshot = await new GeminiUsageProvider(http, _ => [new(12345, "test")]).FetchAsync();
        Assert.True(snapshot.IsOk);
        Assert.Single(snapshot.Windows);
        Assert.Equal("5h", snapshot.PrimaryWindow!.Label);
        Assert.Equal(0, snapshot.PrimaryWindow.RemainingPercent);
    }

    [Fact]
    public async Task SummaryRateLimitPreservesRetryAfter()
    {
        using var http = WithSummary(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return response;
        });
        var snapshot = await new GeminiUsageProvider(http, _ => [new(12345, "test")]).FetchAsync();
        Assert.Equal(UsageErrorKind.RateLimited, snapshot.Error);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.RetryAfter);
        Assert.Single(snapshot.Windows);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task ReadsRealQuotaGroupsWithoutIncludingOtherProvidersOrUnknownModels()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("test-csrf", request.Headers.GetValues("X-Codeium-Csrf-Token").Single());
            Assert.Equal("1", request.Headers.GetValues("Connect-Protocol-Version").Single());
            return Json("""
                {"userStatus":{"email":"test@example.invalid","planStatus":{"planInfo":{"planName":"Pro"}},
                "cascadeModelConfigData":{"clientModelConfigs":[
                {"label":"Gemini 3.8 Flash (High)","quotaInfo":{"remainingFraction":0.9,"resetTime":"2026-10-21T06:38:26Z"}},
                {"label":"Gemini 3.8 Flash (Low)","quotaInfo":{"remainingFraction":0.9,"resetTime":"2026-10-21T06:38:26Z"}},
                {"label":"Gemini 3.1 Pro (High)","quotaInfo":{"remainingFraction":0.5}},
                {"label":"Claude Sonnet","quotaInfo":{"remainingFraction":1}},
                {"label":"Gemini 4 Argon","quotaInfo":{}}]}}}
                """);
        }));
        var provider = new GeminiUsageProvider(http, _ => [new(12345, "test-csrf")]);
        var result = await provider.FetchAsync();
        Assert.True(result.IsOk);
        Assert.False(result.IsMock);
        Assert.Equal("Pro", result.PlanType);
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal("Pro", result.PrimaryWindow!.Label);
        Assert.Equal(50, result.PrimaryWindow.RemainingPercent);
        Assert.Equal("Flash", result.SecondaryWindow!.Label);
        Assert.Equal(90, result.SecondaryWindow.RemainingPercent, 6);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T06:38:26Z"), result.SecondaryWindow.ResetAt);
        Assert.All(result.Windows, w => Assert.Equal(QuotaWindowKind.ModelQuota, w.Kind));
    }

    [Fact]
    public async Task DoesNotInventQuotaWhenAntigravityIsNotRunning()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Must not send a request")));
        var result = await new GeminiUsageProvider(http, _ => []).FetchAsync();
        Assert.Equal(UsageErrorKind.NoCredentials, result.Error);
        Assert.False(result.IsMock);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public async Task DistinguishesUnknownQuotaFromProtobufOmittedZero()
    {
        using var http = new HttpClient(new Handler(_ => Json("""
            {"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[
            {"label":"Gemini Pro","quotaInfo":{}},
            {"label":"Gemini Flash","quotaInfo":{"resetTime":"2026-10-21T06:38:26Z"}}]}}}
            """)));
        var result = await new GeminiUsageProvider(http, _ => [new(12345, "test")]).FetchAsync();
        Assert.True(result.IsOk);
        Assert.Single(result.Windows);
        Assert.Equal(0, result.PrimaryWindow!.RemainingPercent);
        Assert.Equal("Flash", result.PrimaryWindow.Label);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"userStatus\":{}}")]
    [InlineData("invalid json")]
    public async Task EmptyOrInvalidStatusDoesNotBecomeSuccessfulDemo(string body)
    {
        using var http = new HttpClient(new Handler(_ => Json(body)));
        var result = await new GeminiUsageProvider(http, _ => [new(12345, "test")]).FetchAsync();
        Assert.Equal(UsageErrorKind.Server, result.Error);
        Assert.Empty(result.Windows);
        Assert.False(result.IsMock);
    }

    [Fact]
    public async Task RediscoversServerAfterPortChanges()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.Port == 12345
            ? throw new HttpRequestException()
            : Json("""{"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[{"label":"Gemini Flash","quotaInfo":{"remainingFraction":0.25}}]}}}""")));
        var result = await new GeminiUsageProvider(http, force => [new(force ? 23456 : 12345, "test")]).FetchAsync();
        Assert.True(result.IsOk);
        Assert.Equal(25, result.PrimaryWindow!.RemainingPercent);
    }

    [Fact]
    public async Task HonorsLocalRateLimitInsteadOfReturningDemoNumbers()
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        }));
        var result = await new GeminiUsageProvider(http, _ => [new(12345, "test")]).FetchAsync();
        Assert.Equal(UsageErrorKind.RateLimited, result.Error);
        Assert.Equal(TimeSpan.FromSeconds(60), result.RetryAfter);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToAnErrorSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GeminiUsageProvider(discover: _ => []).FetchAsync(cancellation.Token));
    }

    [Fact]
    public async Task UnrelatedGrpcPortDoesNotOverwriteRateLimitFromStatusPort()
    {
        using var http = new HttpClient(new Handler(request => new HttpResponseMessage(
            request.RequestUri!.Port == 12345 ? HttpStatusCode.TooManyRequests : HttpStatusCode.BadRequest)));
        var result = await new GeminiUsageProvider(http, _ => [new(12345, "test"), new(23456, "test")]).FetchAsync();
        Assert.Equal(UsageErrorKind.RateLimited, result.Error);
    }
}

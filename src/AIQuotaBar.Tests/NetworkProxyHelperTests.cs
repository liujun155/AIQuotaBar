using System;
using System.Net;
using AIQuotaBar.Core.Providers;
using Xunit;

namespace AIQuotaBar.Tests;

public class NetworkProxyHelperTests
{
    [Fact]
    public void ResolveProxy_CustomUrl_TakesPrecedence()
    {
        var proxy = NetworkProxyHelper.ResolveProxy("http://127.0.0.1:9999");
        Assert.NotNull(proxy);
        Assert.True(proxy is WebProxy);
        var webProxy = (WebProxy)proxy;
        Assert.Equal("http://127.0.0.1:9999/", webProxy.Address?.ToString());
    }

    [Fact]
    public void ResolveProxy_DetectsLocalProxyOrFallback()
    {
        // 验证自动探测不会抛出任何异常，并能返回有效的代理或默认系统代理
        var proxy = NetworkProxyHelper.ResolveProxy();
        Assert.NotNull(proxy);
    }
}

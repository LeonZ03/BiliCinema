using System.Net;
using DownKyi.Application.Bilibili;
using DownKyi.Infrastructure.Bilibili;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Infrastructure.Tests;

public sealed class BilibiliServiceCollectionExtensionsTests
{
    [Fact]
    public void RegistrationResolvesInjectedApiAndBuvidPorts()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBilibiliCookieProvider, EmptyCookieProvider>();
        services.AddDownKyiBilibiliInfrastructure(_ => new BilibiliNetworkOptions(
            "DownKyi-Test-Agent",
            UseProxy: false,
            ProxyAddress: null));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<BilibiliApiClient>(provider.GetRequiredService<IBilibiliApiClient>());
        Assert.IsType<BilibiliBuvidProvider>(provider.GetRequiredService<IBuvidProvider>());
        Assert.IsType<BilibiliLoginSessionFactory>(
            provider.GetRequiredService<IBilibiliLoginSessionFactory>());
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(BilibiliServiceCollectionExtensions.HttpClientName);
        Assert.Equal(BilibiliServiceCollectionExtensions.RequestTimeout, client.Timeout);
    }

    [Fact]
    public void CustomProxyCreatesExpectedSocketsHandler()
    {
        using var handler = BilibiliServiceCollectionExtensions.CreateHandler(
            new BilibiliNetworkOptions(
                "DownKyi-Test-Agent",
                UseProxy: true,
                ProxyAddress: "http://127.0.0.1:18080"));

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://127.0.0.1:18080"), proxy.Address);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("ftp://127.0.0.1:18080")]
    public void InvalidCustomProxyReturnsVisibleTypedFailure(string address)
    {
        var exception = Assert.Throws<BilibiliHttpRequestException>(() =>
            BilibiliServiceCollectionExtensions.CreateHandler(
            new BilibiliNetworkOptions(
                "DownKyi-Test-Agent",
                UseProxy: true,
                ProxyAddress: address)));

        Assert.Equal(BilibiliHttpFailureKind.Proxy, exception.FailureKind);
    }
}

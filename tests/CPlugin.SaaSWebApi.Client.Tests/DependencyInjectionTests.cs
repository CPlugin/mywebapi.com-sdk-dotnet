using System;
using CPlugin.SaaSWebApi.Client;
using CPlugin.SaaSWebApi.Client.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void Resolves_singleton_client_with_valid_options()
    {
        var services = new ServiceCollection();
        services.AddCPluginWebApiSdk(_ => new CPluginWebApiClientOptions
        {
            Environment = CPluginEnvironment.Staging,
            ClientId = "cid",
            ClientSecret = "csec",
        });
        using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<CPluginWebApiClient>();
        Assert.Equal("https://pre.mywebapi.com", client.ApiBaseUrl);
        Assert.Same(client, sp.GetRequiredService<CPluginWebApiClient>());
    }

    [Fact]
    public void Invalid_options_throw_OptionsValidationException_on_first_resolution()
    {
        var services = new ServiceCollection();
        // * Neither token nor client credentials — must fail at resolution, not at registration.
        services.AddCPluginWebApiSdk(_ => new CPluginWebApiClientOptions
        {
            Environment = CPluginEnvironment.Staging,
        });
        using var sp = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => { sp.GetRequiredService<CPluginWebApiClient>(); });
    }

    [Fact]
    public void Static_token_mode_resolves()
    {
        var services = new ServiceCollection();
        services.AddCPluginWebApiSdk(_ => new CPluginWebApiClientOptions
        {
            Environment = CPluginEnvironment.Custom,
            ApiBaseUrl = "https://api.local",
            Authority = "https://auth.local",
            Token = "static-token",
        });
        using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<CPluginWebApiClient>();
        Assert.Equal("https://api.local", client.ApiBaseUrl);
        Assert.NotNull(client.Realtime.MT4(Guid.NewGuid()));
    }
}

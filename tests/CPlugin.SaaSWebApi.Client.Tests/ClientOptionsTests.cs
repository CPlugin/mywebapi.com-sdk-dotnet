using System;
using CPlugin.SaaSWebApi.Client;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

public class ClientOptionsTests
{
    [Fact]
    public void Options_require_exactly_one_auth_mode()
    {
        var both = new CPluginWebApiClientOptions
        {
            Environment = CPluginEnvironment.Staging,
            Token = "t",
            ClientId = "i",
            ClientSecret = "s",
        };
        Assert.Throws<InvalidOperationException>(() => { both.Validate(); });

        var neither = new CPluginWebApiClientOptions { Environment = CPluginEnvironment.Staging };
        Assert.Throws<InvalidOperationException>(() => { neither.Validate(); });
    }

    [Fact]
    public void Custom_environment_requires_urls()
    {
        var opts = new CPluginWebApiClientOptions
        {
            Environment = CPluginEnvironment.Custom,
            Token = "t",
        };
        Assert.Throws<InvalidOperationException>(() => { opts.Validate(); });
    }

    [Fact]
    public void Client_exposes_bound_platform_namespaces()
    {
        using var client = new CPluginWebApiClient(new CPluginWebApiClientOptions
        {
            Environment = CPluginEnvironment.Staging,
            Token = "static-test-token",
        });
        var tp = Guid.NewGuid();
        Assert.NotNull(client.MT4(tp));
        Assert.NotNull(client.MT5(tp));
        Assert.Equal("https://pre.mywebapi.com", client.ApiBaseUrl);
    }

    [Fact]
    public void Convenience_ctor_wires_client_credentials()
    {
        // * No network happens at construction — discovery + token exchange are lazy.
        using var client = new CPluginWebApiClient(CPluginEnvironment.Prod, "id", "secret");
        Assert.Equal("https://cloud.mywebapi.com", client.ApiBaseUrl);
        Assert.Equal("https://auth.cplugin.net", client.Authority);
    }

    [Fact]
    public void MT4_namespace_rejects_empty_platform()
    {
        using var client = new CPluginWebApiClient(CPluginEnvironment.Staging, "id", "secret");
        Assert.Throws<ArgumentException>(() => { client.MT4(Guid.Empty); });
        Assert.Throws<ArgumentException>(() => { client.MT5(Guid.Empty); });
    }
}

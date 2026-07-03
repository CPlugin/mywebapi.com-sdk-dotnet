using System;
using CPlugin.SaaSWebApi.Client;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

public class EnvironmentsTests
{
    [Fact]
    public void Prod_preset_resolves_to_cloud_mywebapi()
    {
        var (api, auth) = CPluginEnvironments.Resolve(CPluginEnvironment.Prod, null, null);
        Assert.Equal("https://cloud.mywebapi.com", api);
        Assert.Equal("https://auth.cplugin.net", auth);
    }

    [Fact]
    public void Staging_preset_resolves_to_pre_hosts()
    {
        var (api, auth) = CPluginEnvironments.Resolve(CPluginEnvironment.Staging, null, null);
        Assert.Equal("https://pre.mywebapi.com", api);
        Assert.Equal("https://pre.auth.cplugin.net", auth);
    }

    [Fact]
    public void Custom_requires_both_urls()
    {
        Assert.Throws<InvalidOperationException>(
            () => CPluginEnvironments.Resolve(CPluginEnvironment.Custom, "https://x", null));
        Assert.Throws<InvalidOperationException>(
            () => CPluginEnvironments.Resolve(CPluginEnvironment.Custom, null, "https://y"));
    }

    [Fact]
    public void Custom_strips_trailing_slashes()
    {
        var (api, auth) = CPluginEnvironments.Resolve(CPluginEnvironment.Custom, "https://x/", "https://y/");
        Assert.Equal("https://x", api);
        Assert.Equal("https://y", auth);
    }

    [Fact]
    public void Presets_ignore_explicit_urls()
    {
        // * Preset wins; explicit URLs are only meaningful for Custom.
        var (api, _) = CPluginEnvironments.Resolve(CPluginEnvironment.Staging, "https://override", null);
        Assert.Equal("https://pre.mywebapi.com", api);
    }
}

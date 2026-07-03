using System;
using CPlugin.SaaSWebApi.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

// * Unit tests for MT4V2SignalRClient. We do NOT attempt to connect to a real hub —
// *   HubConnection is sealed and Microsoft.AspNetCore.SignalR.Client has no
// *   public mocking story. We assert the parts we control: URL construction,
// *   options validation, lifecycle pre-start invariants.

public class SignalRTests
{
    private static readonly Guid TestPlatform = new("11111111-2222-3333-4444-555555555555");

    private static MT4V2ClientOptions StaticOpts(string? baseUrl = "http://localhost:5002") => new()
    {
        BaseUrl       = baseUrl!,
        TradePlatform = TestPlatform,
        Token         = "eyJ.fake.token",
    };

    private static MT4V2ClientOptions CcOpts() => new()
    {
        BaseUrl       = "http://localhost:5002",
        TradePlatform = TestPlatform,
        ClientId      = "cid",
        ClientSecret  = "csec",
        IdentityUrl   = "https://identity.example/",
    };

    [Fact]
    public void Ctor_StaticToken_DoesNotThrow()
    {
        using var c = (DisposableSignalRClient)new MT4V2SignalRClient(StaticOpts());
        Assert.Equal(TestPlatform, c.TradePlatform);
    }

    [Fact]
    public void Ctor_ClientCredentials_DoesNotThrow()
    {
        // ! client_credentials path owns a token HttpClient — make sure it disposes cleanly.
        var c = new MT4V2SignalRClient(CcOpts());
        Assert.Equal(TestPlatform, c.TradePlatform);
    }

    [Fact]
    public async System.Threading.Tasks.Task DisposeAsync_CleansUpOwnedDisposables()
    {
        var c = new MT4V2SignalRClient(CcOpts());
        await c.DisposeAsync();
        // No assertion on the HttpClient itself — we trust DisposeAsync ran the catch-all.
        // The test mainly exists to prove DisposeAsync does not throw.
    }

    [Fact]
    public void Ctor_InvalidOptions_StaticTokenEmpty_Throws()
    {
        var opts = new MT4V2ClientOptions
        {
            BaseUrl       = "http://localhost:5002",
            TradePlatform = TestPlatform,
            Token         = "", // empty static token — Validate() rejects
        };
        Assert.Throws<InvalidOperationException>(() => new MT4V2SignalRClient(opts));
    }

    [Fact]
    public void Ctor_InvalidOptions_BaseUrlMissing_Throws()
    {
        var opts = new MT4V2ClientOptions
        {
            BaseUrl       = "",
            TradePlatform = TestPlatform,
            Token         = "eyJ.fake.token",
        };
        Assert.Throws<InvalidOperationException>(() => new MT4V2SignalRClient(opts));
    }

    [Fact]
    public void Ctor_InvalidOptions_TradePlatformEmpty_Throws()
    {
        var opts = new MT4V2ClientOptions
        {
            BaseUrl       = "http://localhost:5002",
            TradePlatform = Guid.Empty,
            Token         = "eyJ.fake.token",
        };
        Assert.Throws<InvalidOperationException>(() => new MT4V2SignalRClient(opts));
    }

    [Fact]
    public void GetConnection_BeforeStart_Throws()
    {
        using var c = (DisposableSignalRClient)new MT4V2SignalRClient(StaticOpts());
        Assert.Throws<InvalidOperationException>(() => c.GetConnection());
    }

    [Fact]
    public void HubUrl_Default_And_Override()
    {
        var def = new MT4V2SignalRClient(StaticOpts());
        Assert.Equal(
            $"http://localhost:5002/hubs/mt4/v2?tradePlatform={TestPlatform}",
            def.HubUrl);

        // * Override with an existing query string appends with '&'.
        var over = new MT4V2SignalRClient(StaticOpts(), hubUrlOverride: "https://api.example/mt4hub?x=1");
        Assert.Equal(
            $"https://api.example/mt4hub?x=1&tradePlatform={TestPlatform}",
            over.HubUrl);
    }

    [Fact]
    public void MT5_HubUrl_TargetsMt5V2()
    {
        var c = new MT5V2SignalRClient(StaticOpts());
        Assert.Equal(
            $"http://localhost:5002/hubs/mt5/v2?tradePlatform={TestPlatform}",
            c.HubUrl);
    }

    [Fact]
    public void Handlers_RegistrableBeforeStart()
    {
        // ! Regression guard for the callback-before-start race (post-handshake
        //   OnConnectionStatus push must not require a started connection to attach).
        var mt4 = new MT4V2SignalRClient(StaticOpts());
        using var r1 = mt4.OnConnectionStatus(_ => { });
        using var r2 = mt4.OnTick(_ => { });

        var mt5 = new MT5V2SignalRClient(StaticOpts());
        using var r3 = mt5.OnConnectionStatus(_ => { });
        using var r4 = mt5.OnMarginCall(_ => { });
    }

    [Fact]
    public void DeferredRegistration_DisposedBeforeMaterialize_NeverAttaches()
    {
        // ! Regression guard: disposing a pre-start handler handle must guarantee the
        //   handler is never attached when the connection later materialises.
        var attached = false;
        var reg = new DeferredRegistration(_ => { attached = true; return new DummyDisposable(); });
        reg.Dispose();

        var conn = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
            .WithUrl("http://localhost:5002/hubs/mt4/v2")
            .Build();
        reg.Materialize(conn);

        Assert.False(attached);
    }

    private sealed class DummyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public void RealtimeAccessor_SharesBaseUrl_And_BuildsBothHubs()
    {
        using var client = new CPluginWebApiClient(new CPluginWebApiClientOptions
        {
            Environment = CPluginEnvironment.Staging,
            Token = "static-test-token",
        });
        var mt4 = client.Realtime.MT4(TestPlatform);
        var mt5 = client.Realtime.MT5(TestPlatform);
        Assert.StartsWith("https://pre.mywebapi.com/hubs/mt4/v2?", mt4.HubUrl);
        Assert.StartsWith("https://pre.mywebapi.com/hubs/mt5/v2?", mt5.HubUrl);
    }

    // * Tiny IDisposable adapter so we can use `using var` against IAsyncDisposable
    // *   inside non-async tests. Sync Dispose just kicks the async path.
    private struct DisposableSignalRClient : IDisposable
    {
        private MT4V2SignalRClient _c;
        public DisposableSignalRClient(MT4V2SignalRClient c) => _c = c;
        public Guid TradePlatform => _c.TradePlatform;
        public Microsoft.AspNetCore.SignalR.Client.HubConnection GetConnection() => _c.GetConnection();
        public void Dispose() => _c.DisposeAsync().AsTask().GetAwaiter().GetResult();
        public static implicit operator DisposableSignalRClient(MT4V2SignalRClient c) => new(c);
    }
}

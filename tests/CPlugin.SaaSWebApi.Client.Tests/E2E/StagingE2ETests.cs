using System;
using System.Threading.Tasks;
using CPlugin.SaaSWebApi.Client;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests.E2E;

// * Gated end-to-end tests against STAGING. REST checks only — SignalR e2e is
//   covered by the TypeScript suite (parity decision with the Python SDK).
//
// * Skipped (early return) unless the gating env vars are present, so the
//   hermetic suite stays clean — e2e never fails on missing credentials.
//
// Run:
//   WEBAPI_E2E=1 WEBAPI_CLIENT_ID=... WEBAPI_CLIENT_SECRET=... \
//     WEBAPI_TRADE_PLATFORM=... dotnet test --filter StagingE2ETests
[Trait("Category", "E2E")]
public class StagingE2ETests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("WEBAPI_E2E") == "1"
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBAPI_CLIENT_ID"))
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBAPI_CLIENT_SECRET"));

    private static CPluginWebApiClient Client() =>
        // * Always targets staging — no production data is touched in e2e runs.
        new(CPluginEnvironment.Staging,
            Environment.GetEnvironmentVariable("WEBAPI_CLIENT_ID")!,
            Environment.GetEnvironmentVariable("WEBAPI_CLIENT_SECRET")!);

    private static Guid? TradePlatform()
    {
        var raw = Environment.GetEnvironmentVariable("WEBAPI_TRADE_PLATFORM");
        return Guid.TryParse(raw, out var g) ? g : null;
    }

    [Fact]
    public async Task ListTradePlatforms_live()
    {
        if (!Enabled) return;
        using var client = Client();
        // * Proves the OAuth2 handshake succeeds and the platform list returns.
        var platforms = await client.ListTradePlatformsAsync();
        Assert.NotNull(platforms);
    }

    [Fact]
    public async Task MT4_server_time_live()
    {
        if (!Enabled || TradePlatform() is not { } tp) return;
        using var client = Client();
        var time = await client.MT4(tp).ServerTimeAsync();
        Assert.NotEqual(default, time);
    }

    [Fact]
    public async Task MT4_users_paged_walk_live()
    {
        if (!Enabled || TradePlatform() is not { } tp) return;
        using var client = Client();
        var mt4 = client.MT4(tp);

        // * Walk at most two pages — enough to prove the cursor round-trips.
        var first = await mt4.UsersRequestAsync(limit: 5);
        Assert.NotNull(first.Items);
        if (first.HasMore && first.NextCursor is not null)
        {
            var second = await mt4.UsersRequestAsync(limit: 5, cursor: first.NextCursor);
            Assert.NotNull(second.Items);
        }
    }

    [Fact]
    public async Task Unknown_login_surfaces_ApiError()
    {
        if (!Enabled || TradePlatform() is not { } tp) return;
        using var client = Client();
        var ex = await Assert.ThrowsAsync<ApiError>(
            () => client.MT4(tp).UserRecordGetAsync(int.MaxValue));
        Assert.False(string.IsNullOrEmpty(ex.Code));
    }
}

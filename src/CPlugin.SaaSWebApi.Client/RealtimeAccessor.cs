using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Factory for real-time SignalR clients wired to a parent
/// <see cref="CPluginWebApiClient"/> — REST and SignalR share one cached OAuth token,
/// so bringing a hub up costs no extra token round-trip.</summary>
public sealed class RealtimeAccessor
{
    private readonly CPluginWebApiClient _client;

    internal RealtimeAccessor(CPluginWebApiClient client) => _client = client;

    // * Static-token clients get a constant factory; client_credentials clients delegate
    //   to the parent's ClientAccessTokenProvider (single-flight TokenCache underneath).
    private Func<bool, CancellationToken, Task<string>> TokenFactory()
    {
        if (_client.TokenProvider is { } provider) return provider.GetAccessTokenAsync;
        var token = _client.StaticToken!;
        return (_, _) => Task.FromResult(token);
    }

    /// <summary>Create an <see cref="MT4V2SignalRClient"/> for <paramref name="tradePlatform"/>.
    /// Call <see cref="MT4V2SignalRClient.StartAsync"/> to open the connection; attach
    /// callback handlers before starting to catch the initial server pushes.</summary>
    public MT4V2SignalRClient MT4(Guid tradePlatform, Action<IHubConnectionBuilder>? configureConnection = null)
    {
        if (tradePlatform == Guid.Empty)
            throw new ArgumentException("tradePlatform must be a non-empty GUID.", nameof(tradePlatform));
        return new MT4V2SignalRClient(TokenFactory(), _client.ApiBaseUrl, tradePlatform, configureConnection);
    }

    /// <summary>Create an <see cref="MT5V2SignalRClient"/> for <paramref name="tradePlatform"/>.</summary>
    public MT5V2SignalRClient MT5(Guid tradePlatform, Action<IHubConnectionBuilder>? configureConnection = null)
    {
        if (tradePlatform == Guid.Empty)
            throw new ArgumentException("tradePlatform must be a non-empty GUID.", nameof(tradePlatform));
        return new MT5V2SignalRClient(TokenFactory(), _client.ApiBaseUrl, tradePlatform, configureConnection);
    }
}

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CPlugin.SaaSWebApi.Client.Auth;
using Microsoft.AspNetCore.SignalR.Client;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>
/// Thin SignalR wrapper around <c>/hubs/mt4/v2</c>. REST siblings live on
/// <c>CPluginWebApiClient</c>; obtain instances via <c>client.Realtime.MT4(tradePlatform)</c>
/// to share the REST client's cached OAuth token, or construct standalone from
/// <see cref="MT4V2ClientOptions"/>.
/// </summary>
/// <remarks>
/// <para>Lifecycle:</para>
/// <list type="number">
///   <item><description>Construct — synchronous; resolves the auth path and pre-computes
///   the hub URL but does NOT open the connection.</description></item>
///   <item><description>Attach callback handlers (<see cref="OnTick"/> /
///   <see cref="OnConnectionStatus"/>) — allowed BEFORE <see cref="StartAsync"/>; queued
///   handlers are drained onto the connection before the handshake, so the server's
///   immediate post-handshake <c>OnConnectionStatus</c> push is never missed.</description></item>
///   <item><description><see cref="StartAsync"/> — opens the connection. The access token
///   is fetched via the provider on every (re)connect.</description></item>
///   <item><description>Consume streams with <c>await foreach</c>.</description></item>
///   <item><description><see cref="StopAsync"/> / <see cref="DisposeAsync"/>.</description></item>
/// </list>
/// <para>For static-token mode the client owns nothing extra. For client_credentials,
/// it owns the dedicated token-endpoint <see cref="HttpClient"/> (created on construction)
/// and disposes it on <see cref="DisposeAsync"/>. When created through the Realtime
/// accessor the token machinery belongs to the parent client and nothing extra is owned.</para>
/// </remarks>
public sealed class MT4V2SignalRClient : IAsyncDisposable
{
    private readonly string _hubUrl;
    private readonly Func<bool, CancellationToken, Task<string>> _accessTokenFactory;
    private readonly IDisposable[] _ownedDisposables;
    private readonly Action<IHubConnectionBuilder>? _configure;

    // * Handlers attached before StartAsync are queued here and drained onto the
    //   HubConnection BEFORE the handshake (see DeferredRegistration).
    private readonly object _gate = new();
    private readonly List<DeferredRegistration> _pending = new();

    // ! Serialises StartAsync: without it two concurrent starts would both build a
    // ! HubConnection, and the loser's connection would leak (started, unreferenced).
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private HubConnection? _connection;

    /// <summary>The trade platform GUID bound to this client.</summary>
    public Guid TradePlatform { get; }

    /// <summary>Fully-qualified hub URL, including the <c>tradePlatform</c> query parameter.</summary>
    internal string HubUrl => _hubUrl;

    /// <summary>Construct from <see cref="MT4V2ClientOptions"/>. Same discriminator as
    /// the REST client — set <see cref="MT4V2ClientOptions.Token"/> for static-token
    /// mode, or <c>ClientId/ClientSecret/IdentityUrl</c> for OAuth2 client_credentials.</summary>
    /// <param name="opts">Options.</param>
    /// <param name="configureConnection">Optional callback to mutate the
    /// <see cref="IHubConnectionBuilder"/> before <c>Build()</c> — pass a logging
    /// configurator, custom protocol, etc.</param>
    /// <param name="hubUrlOverride">Override the default <c>{BaseUrl}/hubs/mt4/v2</c>.</param>
    public MT4V2SignalRClient(
        MT4V2ClientOptions opts,
        Action<IHubConnectionBuilder>? configureConnection = null,
        string? hubUrlOverride = null)
    {
        if (opts is null) throw new ArgumentNullException(nameof(opts));
        opts.Validate();
        TradePlatform = opts.TradePlatform;
        _configure = configureConnection;
        _hubUrl = BuildHubUrl(hubUrlOverride ?? (opts.BaseUrl!.TrimEnd('/') + "/hubs/mt4/v2"), opts.TradePlatform);

        if (opts.UsesStaticToken)
        {
            // ! Token is captured by value at construction. Refresh-on-401 is not
            // ! supported in static-token mode.
            var staticToken = opts.Token!;
            _accessTokenFactory = (_, _) => Task.FromResult(staticToken);
            _ownedDisposables = Array.Empty<IDisposable>();
        }
        else
        {
            // * client_credentials path. We compose our own TokenCache + Discovery
            // *   + provider — same machinery the HTTP DelegatingHandler uses,
            // *   but exposed as a Func for SignalR's AccessTokenProvider.
            var tokenHttp = new HttpClient();
            var discovery = new OidcDiscoveryClient(tokenHttp, opts.IdentityUrl!);
            var tokenCache = new TokenCache(skew: TimeSpan.FromSeconds(60));
            var provider = new ClientAccessTokenProvider(
                tokenCache, discovery, tokenHttp,
                opts.ClientId!, opts.ClientSecret!, opts.Scopes);
            _accessTokenFactory = provider.GetAccessTokenAsync;
            _ownedDisposables = new IDisposable[] { tokenHttp };
        }
    }

    /// <summary>Injected-provider constructor used by <c>CPluginWebApiClient.Realtime</c> —
    /// shares the parent client's token cache, so REST and SignalR use one bearer.</summary>
    internal MT4V2SignalRClient(
        Func<bool, CancellationToken, Task<string>> accessTokenFactory,
        string baseUrl,
        Guid tradePlatform,
        Action<IHubConnectionBuilder>? configureConnection = null,
        string? hubUrlOverride = null)
    {
        TradePlatform = tradePlatform;
        _configure = configureConnection;
        _hubUrl = BuildHubUrl(hubUrlOverride ?? (baseUrl.TrimEnd('/') + "/hubs/mt4/v2"), tradePlatform);
        _accessTokenFactory = accessTokenFactory;
        _ownedDisposables = Array.Empty<IDisposable>(); // parent owns the token machinery
    }

    private static string BuildHubUrl(string baseHub, Guid tradePlatform)
    {
        // * URL: baseUrl/hubs/mt4/v2?tradePlatform={guid}. Append `&tradePlatform=…`
        // *   if the override already has a query string.
        var sep = baseHub.Contains("?") ? '&' : '?';
        return $"{baseHub}{sep}tradePlatform={Uri.EscapeDataString(tradePlatform.ToString())}";
    }

    /// <summary>Open the SignalR connection. Idempotent — returns immediately if
    /// already connected; restarts the existing connection if it was stopped.
    /// Safe to call concurrently — starts are serialised internally.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            if (_connection.State == HubConnectionState.Connected) return;
            await _connection.StartAsync(ct).ConfigureAwait(false);
            return;
        }

        var builder = new HubConnectionBuilder()
            .WithUrl(_hubUrl, opt =>
            {
                // ! AccessTokenProvider is a Func<Task<string?>> — SignalR calls it on
                // ! every (re)connect. Negotiate-time HTTP and the WebSocket transport
                // ! both receive the same token via this hook. Our provider memoises
                // ! via TokenCache so concurrent reconnect storms don't N-times the IdP.
                opt.AccessTokenProvider = async () =>
                {
                    var t = await _accessTokenFactory(false, default).ConfigureAwait(false);
                    return t;
                };
            })
            .WithAutomaticReconnect();

        _configure?.Invoke(builder);

        var conn = builder.Build();

        // * Default sink so stream-only consumers don't trigger the "no handler for
        //   OnConnectionStatus" warning (the server always pushes one after handshake).
        conn.On("OnConnectionStatus", (ConnectionStatusPayload _) => { });

        // ! Drain queued handlers BEFORE conn.StartAsync() so the post-handshake
        // ! OnConnectionStatus push cannot race past them.
        lock (_gate)
        {
            _connection = conn;
            foreach (var d in _pending) d.Materialize(conn);
            _pending.Clear();
        }

        await conn.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Close the SignalR connection. Safe to call on a connection that was
    /// never started or has already been closed.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_connection is null) return;
        await _connection.StopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns the underlying <see cref="HubConnection"/> for advanced use
    /// (manual <c>.On()</c>/<c>.InvokeAsync()</c> calls not covered by the typed
    /// helpers below). Throws if not started yet.</summary>
    public HubConnection GetConnection() =>
        _connection ?? throw new InvalidOperationException("SignalR client not started — call StartAsync() first.");

    // * Attach now when the connection exists; otherwise queue for StartAsync to drain.
    private IDisposable Register(Func<HubConnection, IDisposable> attach)
    {
        lock (_gate)
        {
            if (_connection is not null) return attach(_connection);
            var deferred = new DeferredRegistration(attach);
            _pending.Add(deferred);
            return deferred;
        }
    }

    // ===== Callback-style handlers (registrable before StartAsync) =====

    /// <summary>Register a handler for server-pushed <c>OnTick</c> callbacks. Pair
    /// with <see cref="SubscribeToTicksAsync"/>. May be called before <see cref="StartAsync"/>.</summary>
    public IDisposable OnTick(Action<TickPayload> handler) =>
        Register(c => c.On("OnTick", handler));

    /// <summary>Register a handler for the <c>OnConnectionStatus</c> callback the server
    /// pushes right after the handshake completes. Attach BEFORE <see cref="StartAsync"/>
    /// to be guaranteed the initial push.</summary>
    public IDisposable OnConnectionStatus(Action<ConnectionStatusPayload> handler) =>
        Register(c => c.On("OnConnectionStatus", handler));

    public Task SubscribeToTicksAsync(string symbol, CancellationToken ct = default) =>
        GetConnection().InvokeAsync("SubscribeToTicks", symbol, ct);

    public Task UnsubscribeFromTicksAsync(string symbol, CancellationToken ct = default) =>
        GetConnection().InvokeAsync("UnsubscribeFromTicks", symbol, ct);

    // ===== Stream-style helpers =====

    /// <summary>Returns an <see cref="IAsyncEnumerable{T}"/> of ticks. When
    /// <paramref name="symbol"/> is <c>null</c>, every symbol's ticks flow through.</summary>
    public IAsyncEnumerable<TickPayload> StreamTicksAsync(string? symbol = null, CancellationToken ct = default) =>
        symbol is null
            ? GetConnection().StreamAsync<TickPayload>("StreamTicks", ct)
            : GetConnection().StreamAsync<TickPayload>("StreamTicks", symbol, ct);

    public IAsyncEnumerable<MarginCallPayload> StreamMarginCallUpdatesAsync(CancellationToken ct = default) =>
        GetConnection().StreamAsync<MarginCallPayload>("StreamMarginCallUpdates", ct);

    public IAsyncEnumerable<TradeUpdatePayload> StreamTradesAsync(CancellationToken ct = default) =>
        GetConnection().StreamAsync<TradeUpdatePayload>("StreamTrades", ct);

    public IAsyncEnumerable<UserUpdatePayload> StreamUserUpdatesAsync(CancellationToken ct = default) =>
        GetConnection().StreamAsync<UserUpdatePayload>("StreamUserUpdates", ct);

    public IAsyncEnumerable<SymbolUpdatePayload> StreamSymbolUpdatesAsync(CancellationToken ct = default) =>
        GetConnection().StreamAsync<SymbolUpdatePayload>("StreamSymbolUpdates", ct);

    // ===== Disposal =====

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var d in _ownedDisposables)
        {
            try { d.Dispose(); } catch { /* best-effort */ }
        }
    }
}

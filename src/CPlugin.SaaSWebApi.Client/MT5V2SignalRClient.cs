using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CPlugin.SaaSWebApi.Client.Auth;
using Microsoft.AspNetCore.SignalR.Client;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>
/// Thin SignalR wrapper around <c>/hubs/mt5/v2</c> (hub <c>MT5V2</c>). Obtain instances via
/// <c>client.Realtime.MT5(tradePlatform)</c> to share the REST client's cached OAuth token,
/// or construct standalone from <see cref="MT4V2ClientOptions"/> (same credential shapes).
/// </summary>
/// <remarks>
/// <para>Same lifecycle as <see cref="MT4V2SignalRClient"/>: construct → attach handlers
/// (allowed before <see cref="StartAsync"/>; queued handlers are drained onto the connection
/// before the handshake) → <see cref="StartAsync"/> → consume → <see cref="StopAsync"/> /
/// <see cref="DisposeAsync"/>.</para>
/// <para>Server contract (<c>WebAPI/Hubs/MT5/v2/MT5V2Hub.cs</c>): callbacks
/// <c>OnConnectionStatus</c> and <c>OnMarginCall</c>; stream <c>StreamMarginCallUpdates</c>.
/// Further streams (ticks, trades, user/symbol updates) are deferred server-side.</para>
/// </remarks>
public sealed class MT5V2SignalRClient : IAsyncDisposable
{
    private readonly string _hubUrl;
    private readonly Func<bool, CancellationToken, Task<string>> _accessTokenFactory;
    private readonly IDisposable[] _ownedDisposables;
    private readonly Action<IHubConnectionBuilder>? _configure;

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

    /// <summary>Construct from <see cref="MT4V2ClientOptions"/> (same credential shapes as
    /// the sibling hub client — static token or client_credentials).</summary>
    /// <param name="opts">Options.</param>
    /// <param name="configureConnection">Optional callback to mutate the
    /// <see cref="IHubConnectionBuilder"/> before <c>Build()</c>.</param>
    /// <param name="hubUrlOverride">Override the default <c>{BaseUrl}/hubs/mt5/v2</c>.</param>
    public MT5V2SignalRClient(
        MT4V2ClientOptions opts,
        Action<IHubConnectionBuilder>? configureConnection = null,
        string? hubUrlOverride = null)
    {
        if (opts is null) throw new ArgumentNullException(nameof(opts));
        opts.Validate();
        TradePlatform = opts.TradePlatform;
        _configure = configureConnection;
        _hubUrl = BuildHubUrl(hubUrlOverride ?? (opts.BaseUrl!.TrimEnd('/') + "/hubs/mt5/v2"), opts.TradePlatform);

        if (opts.UsesStaticToken)
        {
            // ! Token captured by value; no refresh in static-token mode.
            var staticToken = opts.Token!;
            _accessTokenFactory = (_, _) => Task.FromResult(staticToken);
            _ownedDisposables = Array.Empty<IDisposable>();
        }
        else
        {
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
    internal MT5V2SignalRClient(
        Func<bool, CancellationToken, Task<string>> accessTokenFactory,
        string baseUrl,
        Guid tradePlatform,
        Action<IHubConnectionBuilder>? configureConnection = null,
        string? hubUrlOverride = null)
    {
        TradePlatform = tradePlatform;
        _configure = configureConnection;
        _hubUrl = BuildHubUrl(hubUrlOverride ?? (baseUrl.TrimEnd('/') + "/hubs/mt5/v2"), tradePlatform);
        _accessTokenFactory = accessTokenFactory;
        _ownedDisposables = Array.Empty<IDisposable>(); // parent owns the token machinery
    }

    private static string BuildHubUrl(string baseHub, Guid tradePlatform)
    {
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
        conn.On("OnConnectionStatus", (MT5ConnectionStatusPayload _) => { });

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

    /// <summary>Returns the underlying <see cref="HubConnection"/> for advanced use.
    /// Throws if not started yet.</summary>
    public HubConnection GetConnection() =>
        _connection ?? throw new InvalidOperationException("SignalR client not started — call StartAsync() first.");

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

    /// <summary>Register a handler for the <c>OnConnectionStatus</c> callback — the server
    /// pushes one right after the handshake and on every platform connection state change.
    /// Attach BEFORE <see cref="StartAsync"/> to be guaranteed the initial push.</summary>
    public IDisposable OnConnectionStatus(Action<MT5ConnectionStatusPayload> handler) =>
        Register(c => c.On("OnConnectionStatus", handler));

    /// <summary>Register a handler for server-pushed <c>OnMarginCall</c> callbacks.</summary>
    public IDisposable OnMarginCall(Action<MT5MarginCallPayload> handler) =>
        Register(c => c.On("OnMarginCall", handler));

    // ===== Stream-style helpers =====

    /// <summary>Margin-call updates for the bound trade platform as an
    /// <see cref="IAsyncEnumerable{T}"/>.</summary>
    public IAsyncEnumerable<MT5MarginCallPayload> StreamMarginCallUpdatesAsync(CancellationToken ct = default) =>
        GetConnection().StreamAsync<MT5MarginCallPayload>("StreamMarginCallUpdates", ct);

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

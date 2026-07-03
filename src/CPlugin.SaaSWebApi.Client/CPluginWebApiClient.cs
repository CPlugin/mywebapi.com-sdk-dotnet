using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CPlugin.SaaSWebApi.Client.Auth;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Unified entry point for the CPlugin SaaS WebAPI v2 — trading
/// platform management for .NET.</summary>
/// <remarks>
/// <para>Pick an environment, supply client credentials once — token acquisition
/// (OAuth2 client_credentials), caching, refresh-before-expiry and retry-on-401 are
/// handled transparently. Discover platforms via <see cref="ListTradePlatformsAsync"/>,
/// then access every v2 endpoint through <see cref="MT4"/> / <see cref="MT5"/>.</para>
/// <code>
/// using var client = new CPluginWebApiClient(CPluginEnvironment.Prod, clientId, clientSecret);
/// var mt4  = client.MT4(tradePlatform);
/// var time = await mt4.ServerTimeAsync();
/// </code>
/// <para>The client owns one shared <see cref="HttpClient"/>; create it once per process
/// and reuse (standard HttpClient guidance applies).</para>
/// </remarks>
public sealed class CPluginWebApiClient : IDisposable
{
    private readonly HttpClient _apiHttp;
    private readonly IDisposable[] _owned;
    private readonly ApiConnection _connection;

    /// <summary>Resolved API base URL (no trailing slash).</summary>
    public string ApiBaseUrl { get; }

    /// <summary>Resolved OIDC authority URL (no trailing slash).</summary>
    public string Authority { get; }

    // * Shared token machinery for non-HTTP transports (SignalR). Null in static-token
    //   mode — then StaticToken carries the bearer instead. Both internal: consumed by
    //   the Realtime accessor so REST and SignalR share one cached token.
    internal ClientAccessTokenProvider? TokenProvider { get; }
    internal string? StaticToken { get; }

    /// <summary>Construct from full options. Validates eagerly.</summary>
    public CPluginWebApiClient(CPluginWebApiClientOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var (apiBase, authority) = options.Validate();
        ApiBaseUrl = apiBase;
        Authority = authority;

        if (options.UsesStaticToken)
        {
            StaticToken = options.Token;
            _apiHttp = new HttpClient
            {
                BaseAddress = new Uri(apiBase + "/"),
                Timeout = options.Timeout,
            };
            _apiHttp.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.Token);
            _owned = Array.Empty<IDisposable>();
        }
        else
        {
            // * client_credentials chain: a dedicated bare HttpClient serves OIDC discovery
            //   and token exchange so token requests never recurse through the auth handler.
            //   TokenCache gives single-flight + 60s clock skew; ClientCredentialsHandler
            //   attaches the bearer and retries once on 401 with a forced refresh.
            var tokenHttp = new HttpClient();
            var discovery = new OidcDiscoveryClient(tokenHttp, authority);
            var tokenCache = new TokenCache(skew: TimeSpan.FromSeconds(60));
            var handler = new ClientCredentialsHandler(
                tokenCache, discovery, tokenHttp,
                options.ClientId!, options.ClientSecret!, options.Scopes)
            {
                InnerHandler = new HttpClientHandler(),
            };

            // * Same cache + discovery feed the SignalR token provider — one cached token
            //   across REST and realtime, no second OAuth round-trip.
            TokenProvider = new ClientAccessTokenProvider(
                tokenCache, discovery, tokenHttp,
                options.ClientId!, options.ClientSecret!, options.Scopes);

            _apiHttp = new HttpClient(handler)
            {
                BaseAddress = new Uri(apiBase + "/"),
                Timeout = options.Timeout,
            };
            _owned = new IDisposable[] { tokenHttp };
        }

        _connection = new ApiConnection(_apiHttp);
        Realtime = new RealtimeAccessor(this);
    }

    /// <summary>DI constructor — the api-facing <see cref="HttpClient"/> comes pre-wired from
    /// <c>IHttpClientFactory</c> (auth handler + resilience pipeline already in the chain).
    /// The factory owns handler lifetimes; this instance only disposes the HttpClient itself.</summary>
    internal CPluginWebApiClient(
        HttpClient apiHttp,
        string apiBaseUrl,
        string authority,
        ClientAccessTokenProvider? tokenProvider,
        string? staticToken)
    {
        _apiHttp = apiHttp ?? throw new ArgumentNullException(nameof(apiHttp));
        ApiBaseUrl = apiBaseUrl;
        Authority = authority;
        TokenProvider = tokenProvider;
        StaticToken = staticToken;
        _owned = Array.Empty<IDisposable>();
        _connection = new ApiConnection(_apiHttp);
        Realtime = new RealtimeAccessor(this);
    }

    /// <summary>Convenience constructor: environment preset + OAuth2 client credentials.</summary>
    public CPluginWebApiClient(CPluginEnvironment environment, string clientId, string clientSecret)
        : this(new CPluginWebApiClientOptions
        {
            Environment = environment,
            ClientId = clientId,
            ClientSecret = clientSecret,
        })
    {
    }

    /// <summary>All v2 endpoints of the first platform family, bound to <paramref name="tradePlatform"/>.
    /// Cheap to call — bind once per platform and reuse, or call inline.</summary>
    public MT4Endpoints MT4(Guid tradePlatform)
    {
        if (tradePlatform == Guid.Empty)
            throw new ArgumentException("tradePlatform must be a non-empty GUID.", nameof(tradePlatform));
        return new MT4Endpoints(_connection, tradePlatform);
    }

    /// <summary>All v2 endpoints of the second platform family, bound to <paramref name="tradePlatform"/>.</summary>
    public MT5Endpoints MT5(Guid tradePlatform)
    {
        if (tradePlatform == Guid.Empty)
            throw new ArgumentException("tradePlatform must be a non-empty GUID.", nameof(tradePlatform));
        return new MT5Endpoints(_connection, tradePlatform);
    }

    /// <summary>Real-time SignalR clients (both v2 hubs) sharing this client's
    /// credentials and cached token.</summary>
    public RealtimeAccessor Realtime { get; }

    /// <summary>List the trade platforms available to the authenticated client.</summary>
    /// <remarks>
    /// <!-- ! v1, unversioned path — NOT covered by the v2 spec / generated layer.
    ///      It pre-dates the v2 envelope and returns a plain JSON array, so this method
    ///      raises on HTTP errors directly instead of unwrapping an envelope. -->
    /// Returns the raw JSON array; each element carries at least <c>id</c> and <c>name</c>.
    /// Use the <c>id</c> GUID as the <c>tradePlatform</c> argument of <see cref="MT4"/> /
    /// <see cref="MT5"/>.
    /// </remarks>
    public async Task<JsonArray> ListTradePlatformsAsync(CancellationToken ct = default)
    {
        using var resp = await _apiHttp.GetAsync("api/TradePlatforms", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(text) as JsonArray
            ?? throw new HttpRequestException("Expected a JSON array from /api/TradePlatforms.");
    }

    /// <summary>Dispose the underlying HTTP resources (API client and, in
    /// client_credentials mode, the token-endpoint client).</summary>
    /// <remarks>
    /// // ! When resolved from DI (<c>AddCPluginWebApiSdk</c>) the instance is a shared
    /// //   singleton owned by the container — never dispose it yourself; the container
    /// //   disposes it on shutdown. Dispose manually only for instances you constructed.
    /// </remarks>
    public void Dispose()
    {
        _apiHttp.Dispose();
        foreach (var d in _owned) d.Dispose();
    }
}

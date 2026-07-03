using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using IdentityModel.Client;

namespace CPlugin.SaaSWebApi.Client.Auth;

/// <summary>Acquires OAuth2 client_credentials access tokens for non-HTTP transports
/// (SignalR, gRPC, anything that needs a <see cref="System.Func{TResult}"/>-style
/// token provider). For HTTP, the equivalent path is <see cref="ClientCredentialsHandler"/>
/// — that one is wired as a <see cref="DelegatingHandler"/> and attaches the bearer
/// to every outgoing request automatically.</summary>
/// <remarks>
/// <para>Reuses the same <see cref="TokenCache"/> + <see cref="OidcDiscoveryClient"/>
/// machinery as the HTTP handler — single-flight semantics, 60s default clock skew.</para>
/// <para>This class does NOT own the supplied <c>tokenHttp</c> — caller is responsible
/// for its lifetime. Typical use: the SignalR client passes its caller-supplied
/// token-endpoint HttpClient through unchanged.</para>
/// </remarks>
public sealed class ClientAccessTokenProvider
{
    private readonly TokenCache _tokenCache;
    private readonly OidcDiscoveryClient _discovery;
    private readonly HttpClient _tokenHttp;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string[]? _scopes;

    public ClientAccessTokenProvider(
        TokenCache tokenCache,
        OidcDiscoveryClient discovery,
        HttpClient tokenHttp,
        string clientId,
        string clientSecret,
        string[]? scopes = null)
    {
        _tokenCache = tokenCache;
        _discovery = discovery;
        _tokenHttp = tokenHttp;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scopes = scopes;
    }

    /// <summary>Returns a current access token. Uses cache when fresh enough; otherwise
    /// performs OIDC discovery + token exchange. <paramref name="forceRefresh"/> bypasses
    /// the cache (used by SignalR's reconnect path to defeat stale tokens).</summary>
    public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default) =>
        _tokenCache.GetAsync(AcquireTokenAsync, forceRefresh, ct);

    private async Task<TokenCache.CachedToken> AcquireTokenAsync(CancellationToken ct)
    {
        var disco = await _discovery.GetAsync(ct).ConfigureAwait(false);
        var request = new ClientCredentialsTokenRequest
        {
            Address = disco.TokenEndpoint,
            ClientId = _clientId,
            ClientSecret = _clientSecret,
            Scope = _scopes is { Length: > 0 } s ? string.Join(" ", s) : null,
        };
        var response = await _tokenHttp.RequestClientCredentialsTokenAsync(request, ct)
                                       .ConfigureAwait(false);
        if (response.IsError)
        {
            throw new OAuth2TokenException(
                error: response.Error,
                errorDescription: response.ErrorDescription,
                statusCode: response.HttpStatusCode);
        }
        return new TokenCache.CachedToken(
            response.AccessToken!,
            DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn));
    }
}

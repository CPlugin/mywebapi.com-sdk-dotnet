using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using IdentityModel.Client;

namespace CPlugin.SaaSWebApi.Client.Auth;

/// <summary>DelegatingHandler that acquires an OAuth2 client_credentials token
/// from a discovery-derived token endpoint, attaches it to outgoing requests
/// as <c>Authorization: Bearer</c>, and on a 401 invalidates the cache and
/// retries the request exactly once with a fresh token.</summary>
public sealed class ClientCredentialsHandler : DelegatingHandler
{
    private readonly TokenCache _tokenCache;
    private readonly OidcDiscoveryClient _discovery;
    // * Dedicated client for token endpoint exchange so the token request
    // *   never recurses through this handler (which would deadlock).
    private readonly HttpClient _tokenHttp;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string[]? _scopes;

    public ClientCredentialsHandler(
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

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _tokenCache.GetAsync(AcquireTokenAsync, forceRefresh: false, ct)
                                     .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        _tokenCache.Invalidate();
        var fresh = await _tokenCache.GetAsync(AcquireTokenAsync, forceRefresh: true, ct)
                                     .ConfigureAwait(false);

        // ! HttpRequestMessage cannot be sent twice — must clone before retry.
        // *   We buffer the body bytes once; for streaming bodies that's the
        // *   only safe way (the original stream may be at EOF after the first send).
        var clone = await CloneRequestAsync(request, ct).ConfigureAwait(false);
        clone.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(clone, ct).ConfigureAwait(false);
    }

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

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var h in request.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        if (request.Content is not null)
        {
            // * ReadAsByteArrayAsync(CancellationToken) overload exists only on .NET 5+.
            // *   netstandard2.1 falls back to the parameterless overload — callers can
            // *   still cancel via the outer task chain.
#if NET8_0_OR_GREATER
            var bytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
#else
            var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
            var copy = new ByteArrayContent(bytes);
            foreach (var h in request.Content.Headers)
                copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            clone.Content = copy;
        }
        return clone;
    }
}

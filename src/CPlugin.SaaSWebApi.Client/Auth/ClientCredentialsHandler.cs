using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using IdentityModel.Client;

namespace CPlugin.SaaSWebApi.Client.Auth;

/// <summary>DelegatingHandler that acquires an OAuth2 client_credentials token
/// from a discovery-derived token endpoint, attaches it to outgoing requests
/// as <c>Authorization: Bearer</c>; a 401 invalidates the cached token, but only
/// safe GET/HEAD/OPTIONS requests are replayed once with a fresh token. Unsafe
/// requests are returned to the caller without replay because the server may have
/// applied their side effect before returning the 401.</summary>
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

        // Invalidate a stale token for the next request, but never replay a write after
        // a 401: the server may have applied the mutation before returning the response.
        _tokenCache.Invalidate();
        if (!IsSafeMethod(request.Method)) return response;

        response.Dispose();
        var fresh = await _tokenCache.GetAsync(AcquireTokenAsync, forceRefresh: true, ct)
                                     .ConfigureAwait(false);

        // HttpRequestMessage cannot be sent twice — clone before the safe-method retry.
        var clone = await CloneRequestAsync(request, ct).ConfigureAwait(false);
        clone.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(clone, ct).ConfigureAwait(false);
    }

    private async Task<TokenCache.CachedToken> AcquireTokenAsync(CancellationToken ct)
    {
        try
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
        catch
        {
            _discovery.Invalidate();
            throw;
        }
    }

    private static bool IsSafeMethod(HttpMethod method) =>
        string.Equals(method.Method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method.Method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

#if NETSTANDARD2_0
    private static async Task DisposeWhenReadyAsync(Task<Stream> readTask)
    {
        try
        {
            using var stream = await readTask.ConfigureAwait(false);
        }
        catch
        {
            // The caller already observed cancellation; consume late acquisition faults.
        }
    }
#endif

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var h in request.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        if (request.Content is not null)
        {
#if NETSTANDARD2_0
            // netstandard2.0 has no cancellable ReadAsStreamAsync overload. Race the
            // non-cancellable stream acquisition against caller cancellation. The
            // registration is disposed when this method exits; a late stream is
            // disposed by DisposeWhenReadyAsync instead of being leaked.
            ct.ThrowIfCancellationRequested();
            var readTask = request.Content.ReadAsStreamAsync();
            var cancellation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellation);
            if (await Task.WhenAny(readTask, cancellation.Task).ConfigureAwait(false) != readTask)
            {
                _ = DisposeWhenReadyAsync(readTask);
                ct.ThrowIfCancellationRequested();
            }
            using var source = await readTask.ConfigureAwait(false);
            using var destination = new MemoryStream();
            await source.CopyToAsync(destination, 81920, ct).ConfigureAwait(false);
            var bytes = destination.ToArray();
#else
            var bytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
#endif
            var copy = new ByteArrayContent(bytes);
            foreach (var h in request.Content.Headers)
                copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            clone.Content = copy;
        }
        return clone;
    }
}

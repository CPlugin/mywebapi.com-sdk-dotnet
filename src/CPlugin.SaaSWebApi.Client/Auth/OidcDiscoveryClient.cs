using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using IdentityModel.Client;

namespace CPlugin.SaaSWebApi.Client.Auth;

// * Caches the OIDC discovery document for the lifetime of the instance.
// *   One SemaphoreSlim serialises the first fetch; subsequent calls return
// *   the cached document without any lock contention (Volatile.Read).
public sealed class OidcDiscoveryClient
{
    private readonly HttpClient _http;
    private readonly string _identityUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiscoveryDocumentResponse? _cached;

    public OidcDiscoveryClient(HttpClient http, string identityUrl)
    {
        _http = http;
        _identityUrl = identityUrl.TrimEnd('/');
    }

    public async Task<DiscoveryDocumentResponse> GetAsync(CancellationToken ct)
    {
        var snapshot = Volatile.Read(ref _cached);
        if (snapshot is not null) return snapshot;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            // * IdentityModel by default validates the discovery document by also
            // *   fetching the JWKS endpoint. We don't need keys for client_credentials
            // *   (we never validate ID tokens here) — disable RequireKeySet so the
            // *   discovery call succeeds against any compliant IdP without an extra
            // *   network round-trip and avoids hard-failing in tests where JWKS isn't stubbed.
            var request = new DiscoveryDocumentRequest
            {
                Address = _identityUrl,
                Policy = new DiscoveryPolicy { RequireKeySet = false },
            };
            var disco = await _http.GetDiscoveryDocumentAsync(request, ct).ConfigureAwait(false);
            if (disco.IsError)
            {
                throw new OAuth2TokenException(
                    error: "discovery_failed",
                    errorDescription: disco.Error ?? $"Discovery at {_identityUrl} failed",
                    statusCode: disco.HttpStatusCode);
            }
            Volatile.Write(ref _cached, disco);
            return disco;
        }
        finally
        {
            _gate.Release();
        }
    }
}

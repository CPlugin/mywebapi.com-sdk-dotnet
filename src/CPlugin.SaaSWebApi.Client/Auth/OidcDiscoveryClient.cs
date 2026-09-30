using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using IdentityModel.Client;

namespace CPlugin.SaaSWebApi.Client.Auth;

/// <summary>Caches the validated OIDC discovery document for a bounded period and
/// coalesces concurrent refreshes into one request.</summary>
public sealed class OidcDiscoveryClient
{
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(5);
    private readonly HttpClient _http;
    private readonly string _identityUrl;
    private readonly TimeSpan _cacheDuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiscoveryDocumentResponse? _cached;
    private long _cachedAtTicksUtc;

    public OidcDiscoveryClient(HttpClient http, string identityUrl, TimeSpan? cacheDuration = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _identityUrl = identityUrl.TrimEnd('/');
        _cacheDuration = cacheDuration ?? DefaultCacheDuration;
        if (_cacheDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheDuration), "Discovery cache duration must be positive.");
    }

    public async Task<DiscoveryDocumentResponse> GetAsync(CancellationToken ct)
    {
        var snapshot = ReadFresh();
        if (snapshot is not null) return snapshot;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = ReadFresh();
            if (snapshot is not null) return snapshot;

            try
            {
                // IdentityModel retains its issuer, endpoint and HTTPS validation. The
                // client_credentials flow does not consume signing keys, so only the
                // key-set requirement is disabled; no trust policy is weakened here.
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
                Volatile.Write(ref _cachedAtTicksUtc, DateTimeOffset.UtcNow.Ticks);
                return disco;
            }
            catch
            {
                Invalidate();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Discard the cached document so the next acquisition revalidates discovery.</summary>
    public void Invalidate()
    {
        Volatile.Write(ref _cachedAtTicksUtc, 0);
        Volatile.Write(ref _cached, null);
    }

    private DiscoveryDocumentResponse? ReadFresh()
    {
        var snapshot = Volatile.Read(ref _cached);
        var cachedAtTicks = Volatile.Read(ref _cachedAtTicksUtc);
        if (snapshot is null || cachedAtTicks == 0) return null;
        return DateTimeOffset.UtcNow - new DateTimeOffset(cachedAtTicks, TimeSpan.Zero) < _cacheDuration
            ? snapshot
            : null;
    }
}

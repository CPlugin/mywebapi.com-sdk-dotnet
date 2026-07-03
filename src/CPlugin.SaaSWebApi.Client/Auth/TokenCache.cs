using System;
using System.Threading;
using System.Threading.Tasks;

namespace CPlugin.SaaSWebApi.Client.Auth;

// * Thread-safe single-flight cache for OAuth2 access tokens.
// *   Volatile read fast-paths cache hits without entering the semaphore;
// *   the lock only serialises acquisition. Double-check inside the lock
// *   handles the case where a previous waiter just populated the cache.
public sealed class TokenCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedToken? _current;
    private readonly TimeSpan _skew;

    public TokenCache(TimeSpan? skew = null)
    {
        _skew = skew ?? TimeSpan.FromSeconds(60);
    }

    public async Task<string> GetAsync(
        Func<CancellationToken, Task<CachedToken>> acquire,
        bool forceRefresh,
        CancellationToken ct)
    {
        var snapshot = Volatile.Read(ref _current);
        if (!forceRefresh && snapshot is not null
            && snapshot.ExpiresAt - DateTimeOffset.UtcNow > _skew)
        {
            return snapshot.AccessToken;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = _current;
            if (!forceRefresh && snapshot is not null
                && snapshot.ExpiresAt - DateTimeOffset.UtcNow > _skew)
            {
                return snapshot.AccessToken;
            }
            var fresh = await acquire(ct).ConfigureAwait(false);
            Volatile.Write(ref _current, fresh);
            return fresh.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => Volatile.Write(ref _current, null);

    public sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CPlugin.SaaSWebApi.Client.Auth;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

// * Unit tests for the auth handler chain. No live HTTP — all transports are
// *   stubbed via a queueing HttpMessageHandler that returns canned responses
// *   per (host, path) match. Discovery responses are loaded from the shared
// *   fixture and rewritten to the test host.

public class AuthTests
{
    // ===== TokenCache =====

    [Fact]
    public async Task TokenCache_CacheHit_ReturnsExisting()
    {
        var cache = new TokenCache(skew: TimeSpan.FromSeconds(60));
        int calls = 0;
        Task<TokenCache.CachedToken> Acquire(CancellationToken _) {
            calls++;
            return Task.FromResult(new TokenCache.CachedToken("tok-1",
                DateTimeOffset.UtcNow.AddMinutes(10)));
        }

        var t1 = await cache.GetAsync(Acquire, forceRefresh: false, CancellationToken.None);
        var t2 = await cache.GetAsync(Acquire, forceRefresh: false, CancellationToken.None);

        Assert.Equal("tok-1", t1);
        Assert.Equal("tok-1", t2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TokenCache_ExpiryViaSkew_TriggersAcquire()
    {
        var cache = new TokenCache(skew: TimeSpan.FromSeconds(60));
        int calls = 0;
        Task<TokenCache.CachedToken> Acquire(CancellationToken _) {
            calls++;
            // * Token expires in 30s; skew of 60s means it's treated as expired immediately.
            return Task.FromResult(new TokenCache.CachedToken($"tok-{calls}",
                DateTimeOffset.UtcNow.AddSeconds(30)));
        }

        var t1 = await cache.GetAsync(Acquire, forceRefresh: false, CancellationToken.None);
        var t2 = await cache.GetAsync(Acquire, forceRefresh: false, CancellationToken.None);

        Assert.Equal("tok-1", t1);
        Assert.Equal("tok-2", t2);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TokenCache_ForceRefresh_BypassesCache()
    {
        var cache = new TokenCache(skew: TimeSpan.FromSeconds(60));
        int calls = 0;
        Task<TokenCache.CachedToken> Acquire(CancellationToken _) {
            calls++;
            return Task.FromResult(new TokenCache.CachedToken($"tok-{calls}",
                DateTimeOffset.UtcNow.AddMinutes(10)));
        }

        var t1 = await cache.GetAsync(Acquire, forceRefresh: false, CancellationToken.None);
        var t2 = await cache.GetAsync(Acquire, forceRefresh: true,  CancellationToken.None);

        Assert.Equal("tok-1", t1);
        Assert.Equal("tok-2", t2);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TokenCache_SingleFlight()
    {
        var cache = new TokenCache(skew: TimeSpan.FromSeconds(60));
        int calls = 0;
        async Task<TokenCache.CachedToken> Acquire(CancellationToken ct) {
            Interlocked.Increment(ref calls);
            // * Short delay to widen the race window — multiple waiters should pile up.
            await Task.Delay(50, ct).ConfigureAwait(false);
            return new TokenCache.CachedToken("shared", DateTimeOffset.UtcNow.AddMinutes(10));
        }

        const int N = 100;
        var tasks = new Task<string>[N];
        for (int i = 0; i < N; i++)
            tasks[i] = cache.GetAsync(Acquire, forceRefresh: false, CancellationToken.None);
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("shared", r));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    // ===== OidcDiscoveryClient =====

    [Fact]
    public async Task OidcDiscoveryClient_FirstFetch_RoundTrips()
    {
        var stub = new StubHandler();
        int discoCalls = 0;
        stub.OnGet("/.well-known/openid-configuration", _ => {
            discoCalls++;
            return DiscoveryResponse("https://test.local");
        });
        stub.OnGet("/.well-known/openid-configuration/jwks", _ => JwksResponse());

        var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.local") };
        var disco = new OidcDiscoveryClient(http, "https://test.local");

        var doc = await disco.GetAsync(CancellationToken.None);

        Assert.False(doc.IsError);
        Assert.Equal("https://test.local/connect/token", doc.TokenEndpoint);
        Assert.Equal(1, discoCalls);
    }

    [Fact]
    public async Task OidcDiscoveryClient_CachedAfter_FirstSucceeds()
    {
        var stub = new StubHandler();
        int discoCalls = 0;
        stub.OnGet("/.well-known/openid-configuration", _ => {
            discoCalls++;
            return DiscoveryResponse("https://test.local");
        });
        stub.OnGet("/.well-known/openid-configuration/jwks", _ => JwksResponse());

        var http = new HttpClient(stub);
        var disco = new OidcDiscoveryClient(http, "https://test.local");

        await disco.GetAsync(CancellationToken.None);
        await disco.GetAsync(CancellationToken.None);
        await disco.GetAsync(CancellationToken.None);

        Assert.Equal(1, discoCalls);
    }

    [Fact]
    public async Task OidcDiscoveryClient_DiscoveryFailure_Throws_OAuth2TokenException()
    {
        var stub = new StubHandler();
        stub.OnGet("/.well-known/openid-configuration", _ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var http = new HttpClient(stub);
        var disco = new OidcDiscoveryClient(http, "https://test.local");

        var ex = await Assert.ThrowsAsync<OAuth2TokenException>(
            () => disco.GetAsync(CancellationToken.None));
        Assert.Equal("discovery_failed", ex.Error);
    }

    // ===== ClientCredentialsHandler =====

    [Fact]
    public async Task ClientCredentialsHandler_AddsBearer()
    {
        var (apiStub, tokenStub, handler) = BuildHandler(token: "abc", tokenExpiresIn: 600);
        apiStub.OnGet("/v2/ping", req => {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("abc", req.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.local") };
        var resp = await client.GetAsync("/v2/ping");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ClientCredentialsHandler_401_TriggersRefresh()
    {
        var tokenStub = new StubHandler();
        int tokenCalls = 0;
        tokenStub.OnGet("/.well-known/openid-configuration", _ => DiscoveryResponse("https://idp.local"));
        tokenStub.OnGet("/.well-known/openid-configuration/jwks", _ => JwksResponse());
        tokenStub.OnPost("/connect/token", _ => {
            tokenCalls++;
            return TokenResponse($"tok-{tokenCalls}", expiresIn: 600);
        });

        var apiStub = new StubHandler();
        int apiCalls = 0;
        apiStub.OnGet("/v2/ping", _ => {
            apiCalls++;
            return apiCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var tokenHttp = new HttpClient(tokenStub);
        var discovery = new OidcDiscoveryClient(tokenHttp, "https://idp.local");
        var cache = new TokenCache();
        var handler = new ClientCredentialsHandler(cache, discovery, tokenHttp,
            "client", "secret", new[] { "webapi" })
        {
            InnerHandler = apiStub,
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.local") };
        var resp = await client.GetAsync("/v2/ping");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, apiCalls);
        Assert.Equal(2, tokenCalls);
    }

    [Fact]
    public async Task ClientCredentialsHandler_TwoConsecutive401s_ReturnsSecondResponse()
    {
        var (apiStub, tokenStub, handler) = BuildHandler(token: "abc", tokenExpiresIn: 600);
        int apiCalls = 0;
        apiStub.OnGet("/v2/ping", _ => {
            apiCalls++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.local") };
        var resp = await client.GetAsync("/v2/ping");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(2, apiCalls);
    }

    [Fact]
    public async Task ClientCredentialsHandler_PostBody_IsClonedOnRetry()
    {
        var tokenStub = new StubHandler();
        int tokenCalls = 0;
        tokenStub.OnGet("/.well-known/openid-configuration", _ => DiscoveryResponse("https://idp.local"));
        tokenStub.OnGet("/.well-known/openid-configuration/jwks", _ => JwksResponse());
        tokenStub.OnPost("/connect/token", _ => {
            tokenCalls++;
            return TokenResponse($"tok-{tokenCalls}", expiresIn: 600);
        });

        var apiStub = new StubHandler();
        int apiCalls = 0;
        var bodies = new List<string>();
        apiStub.OnPost("/v2/echo", async req => {
            apiCalls++;
            bodies.Add(await req.Content!.ReadAsStringAsync().ConfigureAwait(false));
            return apiCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var tokenHttp = new HttpClient(tokenStub);
        var discovery = new OidcDiscoveryClient(tokenHttp, "https://idp.local");
        var cache = new TokenCache();
        var handler = new ClientCredentialsHandler(cache, discovery, tokenHttp,
            "c", "s") { InnerHandler = apiStub };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.local") };
        var resp = await client.PostAsync("/v2/echo",
            new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, apiCalls);
        Assert.Equal(new[] { "hello", "hello" }, bodies);
    }

    [Fact]
    public async Task OAuth2TokenException_FromTokenError_PopulatesFields()
    {
        var tokenStub = new StubHandler();
        tokenStub.OnGet("/.well-known/openid-configuration", _ => DiscoveryResponse("https://idp.local"));
        tokenStub.OnGet("/.well-known/openid-configuration/jwks", _ => JwksResponse());
        tokenStub.OnPost("/connect/token", _ => {
            var payload = "{\"error\":\"invalid_client\",\"error_description\":\"bad creds\"}";
            return new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        });

        var apiStub = new StubHandler();
        var tokenHttp = new HttpClient(tokenStub);
        var discovery = new OidcDiscoveryClient(tokenHttp, "https://idp.local");
        var cache = new TokenCache();
        var handler = new ClientCredentialsHandler(cache, discovery, tokenHttp,
            "c", "s") { InnerHandler = apiStub };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.local") };
        var ex = await Assert.ThrowsAsync<OAuth2TokenException>(
            () => client.GetAsync("/v2/ping"));

        Assert.Equal("invalid_client", ex.Error);
        Assert.Equal("bad creds", ex.ErrorDescription);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    // ===== Helpers =====

    /// <summary>Builds a fully-wired handler with single-token, single-discovery stubs.
    /// Returns the API stub (caller registers route handlers), the token stub, and the
    /// `ClientCredentialsHandler` ready to be plugged into an HttpClient.</summary>
    private static (StubHandler apiStub, StubHandler tokenStub, ClientCredentialsHandler handler)
        BuildHandler(string token, int tokenExpiresIn)
    {
        var tokenStub = new StubHandler();
        tokenStub.OnGet("/.well-known/openid-configuration", _ => DiscoveryResponse("https://idp.local"));
        tokenStub.OnGet("/.well-known/openid-configuration/jwks", _ => JwksResponse());
        tokenStub.OnPost("/connect/token", _ => TokenResponse(token, tokenExpiresIn));

        var apiStub = new StubHandler();
        var tokenHttp = new HttpClient(tokenStub);
        var discovery = new OidcDiscoveryClient(tokenHttp, "https://idp.local");
        var cache = new TokenCache();
        var handler = new ClientCredentialsHandler(cache, discovery, tokenHttp,
            "c", "s") { InnerHandler = apiStub };
        return (apiStub, tokenStub, handler);
    }

    private static HttpResponseMessage DiscoveryResponse(string baseUrl)
    {
        // * Load the shared fixture and rewrite all identity.example URLs to the test host.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "openid-configuration.json");
        var json = File.ReadAllText(path).Replace("https://identity.example", baseUrl);
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Empty JWKS document. IdentityModel always fetches the jwks_uri during
    /// discovery (even with `RequireKeySet=false` it still attempts the call and
    /// errors on a non-200). Returning `{"keys":[]}` keeps it happy without
    /// forcing us to maintain a real key fixture.</summary>
    private static HttpResponseMessage JwksResponse() =>
        new(HttpStatusCode.OK) {
            Content = new StringContent("{\"keys\":[]}", Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage TokenResponse(string accessToken, int expiresIn)
    {
        var payload = JsonSerializer.Serialize(new {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = expiresIn,
        });
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Programmable HttpMessageHandler: route on (method, path), return canned response.
    /// Tracks total request count for assertions.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<(HttpMethod, string), Func<HttpRequestMessage, Task<HttpResponseMessage>>> _routes = new();
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public void OnGet(string path, Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _routes[(HttpMethod.Get, path)] = req => Task.FromResult(handler(req));
        public void OnPost(string path, Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _routes[(HttpMethod.Post, path)] = req => Task.FromResult(handler(req));
        public void OnPost(string path, Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
            _routes[(HttpMethod.Post, path)] = handler;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var key = (request.Method, request.RequestUri!.AbsolutePath);
            if (_routes.TryGetValue(key, out var handler))
                return await handler(request).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.NotFound) {
                Content = new StringContent($"no stub for {request.Method} {request.RequestUri}"),
            };
        }
    }
}

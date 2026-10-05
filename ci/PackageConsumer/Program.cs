using System.Net;
using System.Text;
using System.Text.Json;
using CPlugin.SaaSWebApi.Client;
using CPlugin.SaaSWebApi.Client.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

const string StaticToken = "loopback-static-token";
const string ClientId = "synthetic-client-id";
const string ClientSecret = "synthetic-client-secret";
var platform = Guid.Parse("11111111-1111-1111-1111-111111111111");
var artifactVersion = Environment.GetEnvironmentVariable("SDK_VERSION") ?? "0.3.3";
var results = new List<object>();

await using (var server = await LoopbackServer.StartAsync())
    results.Add(await RunSafe("static-rest", () => RunStaticRestScenario(server, platform)));

await using (var server = await LoopbackServer.StartAsync())
    results.Add(await RunSafe("oauth-discovery-single-flight-401-refresh", () => RunOAuthScenario(server, platform)));

await using (var server = await SignalRLoopbackServer.StartAsync())
    results.Add(await RunSafe("signalr-start-handler-stop-dispose", () => RunSignalRScenario(server, platform)));

results.Add(await RunSafe("di-resilience-fault-probes", () => RunResilienceScenario(platform)));

Console.WriteLine(JsonSerializer.Serialize(new
{
    artifact = new { client = "MyWebApi.Sdk", models = "MyWebApi.Sdk.Models", version = artifactVersion },
    scenarios = results,
    note = "Loopback only; no product API calls; SignalR lifecycle exercised"
}));

static async Task<object> RunStaticRestScenario(LoopbackServer server, Guid platform)
{
    using var client = new CPluginWebApiClient(new CPluginWebApiClientOptions
    {
        Environment = CPluginEnvironment.Custom,
        ApiBaseUrl = server.BaseUri.ToString().TrimEnd('/'),
        Authority = server.BaseUri.ToString().TrimEnd('/'),
        Token = StaticToken,
        Timeout = TimeSpan.FromSeconds(5),
    });
    var mt4 = client.MT4(platform);
    var time = await mt4.ServerTimeAsync(new CallOptions
    {
        IdempotencyKey = "synthetic-idempotency-key",
        Fields = new[] { "data", "meta" },
    });

    var pages = new List<Page<CPlugin.SaaSWebApi.Models.MT4User>>();
    await foreach (var page in PageIterator.PagesAsync(cursor => mt4.UsersRequestAsync(10, cursor)))
        pages.Add(page);

    ApiError? apiError = null;
    try { await mt4.GroupRecordGetAsync("error"); }
    catch (ApiError ex) { apiError = ex; }

    ApiError? httpStatusError = null;
    try { await mt4.GroupRecordGetAsync("http-error"); }
    catch (ApiError ex) { httpStatusError = ex; }

    ApiError? encodedError = null;
    try { await mt4.GroupRecordGetAsync("space/slash?&ü"); }
    catch (ApiError ex) { encodedError = ex; }
    var encodedPath = server.LastRawUrl;

    server.DelayApi = true;
    var cancelled = false;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await mt4.UsersRequestAsync(options: new CallOptions { CancellationToken = cts.Token });
    }
    catch (OperationCanceledException) { cancelled = true; }
    finally { server.DelayApi = false; }

    var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => mt4.ServerTimeAsync()));
    return new
    {
        name = "static-rest-envelope-pagination-cancellation-concurrency",
        ok = time == DateTimeOffset.Parse("2026-09-17T10:00:00Z")
            && pages.Count == 2 && pages[0].HasMore && pages[0].NextCursor == "cursor-1"
            && !pages[1].HasMore && pages[1].NextCursor is null
            && apiError?.Code == "NotFound" && apiError.Description == "synthetic missing"
            && apiError.ActivityId == "synthetic-activity" && apiError.Status == 200
            && httpStatusError?.Status == 400 && encodedError is not null
            && encodedPath?.Contains("%2F", StringComparison.OrdinalIgnoreCase) == true
            && encodedPath.Contains("%3F", StringComparison.OrdinalIgnoreCase)
            && encodedPath.Contains("%26", StringComparison.OrdinalIgnoreCase)
            && cancelled && concurrent.Length == 8,
        observed = new
        {
            pageCount = pages.Count,
            firstNextCursor = pages[0].NextCursor,
            error = apiError is null ? null : new { apiError.Code, apiError.Description, apiError.ActivityId, apiError.Status },
            httpError = httpStatusError is null ? null : new { httpStatusError.Code, httpStatusError.Description, httpStatusError.ActivityId, httpStatusError.Status },
            encodedPath,
            cancelled,
            server.ApiRequestCount,
            server.LastIdempotencyKey,
            server.LastFields,
            server.WrongAuthorizationCount,
        }
    };
}

static async Task<object> RunOAuthScenario(LoopbackServer server, Guid platform)
{
    using var client = new CPluginWebApiClient(new CPluginWebApiClientOptions
    {
        Environment = CPluginEnvironment.Custom,
        ApiBaseUrl = server.BaseUri.ToString().TrimEnd('/'),
        Authority = server.BaseUri.ToString().TrimEnd('/'),
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        Timeout = TimeSpan.FromSeconds(5),
    });
    var mt4 = client.MT4(platform);
    var times = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => mt4.ServerTimeAsync()));
    return new
    {
        name = "oauth-discovery-single-flight-401-refresh",
        ok = times.Length == 16 && times.All(t => t == DateTimeOffset.Parse("2026-09-17T10:00:00Z"))
            && server.DiscoveryRequestCount == 1 && server.TokenRequestCount == 2
            && server.UnauthorizedApiCount == 1 && server.WrongAuthorizationCount == 0,
        observed = new
        {
            requests = times.Length,
            server.DiscoveryRequestCount,
            server.TokenRequestCount,
            server.UnauthorizedApiCount,
            server.WrongAuthorizationCount,
        }
    };
}

static async Task<object> RunSignalRScenario(SignalRLoopbackServer server, Guid platform)
{
    var options = new MT4V2ClientOptions
    {
        BaseUrl = server.BaseUri.ToString().TrimEnd('/'),
        TradePlatform = platform,
        Token = StaticToken,
    };
    await using var hub = new MT4V2SignalRClient(options);
    var status = new TaskCompletionSource<ConnectionStatusPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
    var tick = new TaskCompletionSource<TickPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var statusRegistration = hub.OnConnectionStatus(value => status.TrySetResult(value));
    using var tickRegistration = hub.OnTick(value => tick.TrySetResult(value));

    await hub.StartAsync().ConfigureAwait(false);
    await hub.SubscribeToTicksAsync("EURUSD").ConfigureAwait(false);
    var connected = await status.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    var received = await tick.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    await hub.StopAsync().ConfigureAwait(false);

    return new
    {
        name = "signalr-start-handler-stop-dispose",
        ok = connected.Connected && received.Symbol == "EURUSD"
            && hub.GetConnection().State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected,
        observed = new { connected = connected.Connected, symbol = received.Symbol, state = hub.GetConnection().State.ToString() },
    };
}

static async Task<object> RunSafe(string name, Func<Task<object>> action)
{
    try { return await action().ConfigureAwait(false); }
    catch (Exception ex) { return new { name, ok = false, error = ex.GetType().Name, message = ex.Message }; }
}

static async Task<object> RunResilienceScenario(Guid platform)
{
    var probes = new List<object>();
    foreach (var probe in new[]
    {
        (verb: "GET", failure: "503", key: false),
        (verb: "POST", failure: "503", key: false),
        (verb: "POST", failure: "503", key: true),
        (verb: "PATCH", failure: "503", key: false),
        (verb: "PATCH", failure: "503", key: true),
        (verb: "POST", failure: "network", key: false),
        (verb: "POST", failure: "network", key: true),
        (verb: "PATCH", failure: "network", key: false),
        (verb: "PATCH", failure: "network", key: true),
    })
    {
        var name = $"{probe.verb}-{probe.failure}-{(probe.key ? "with-key" : "without-key")}";
        probes.Add(await RunSafe(name, () => RunResilienceProbe(probe.verb, probe.failure, probe.key, platform)));
    }
    return new { name = "di-resilience-fault-probes", probes };
}

static async Task<object> RunResilienceProbe(string verb, string failure, bool withKey, Guid platform)
{
    await using var server = await LoopbackServer.StartAsync();
    server.FaultMode = $"{verb}-{failure}";
    var services = new ServiceCollection();
    services.AddCPluginWebApiSdk(_ => new CPluginWebApiClientOptions
    {
        Environment = CPluginEnvironment.Custom,
        ApiBaseUrl = server.BaseUri.ToString().TrimEnd('/'),
        Authority = server.BaseUri.ToString().TrimEnd('/'),
        Token = StaticToken,
        Timeout = TimeSpan.FromSeconds(2),
    });
    using var provider = services.BuildServiceProvider();
    var client = provider.GetRequiredService<CPluginWebApiClient>();
    var mt4 = client.MT4(platform);
    Exception? failureSeen = null;
    try
    {
        var options = withKey ? new CallOptions { IdempotencyKey = "synthetic-retry-key" } : null;
        if (verb == "GET") await mt4.ServerTimeAsync(options);
        else if (verb == "POST") await mt4.CfgDeleteAccessAsync(7, options);
        else await mt4.GroupRecordAsync("fault-group", new { Leverage = 100 }, options);
    }
    catch (Exception ex) { failureSeen = ex; }
    var expectedRequests = verb == "GET" ? 2 : 1;
    var expectedFailure = verb != "GET";
    return new
    {
        name = $"{verb}-{failure}-{(withKey ? "with-key" : "without-key")}",
        ok = (failureSeen is null) != expectedFailure && server.FaultRequestCount == expectedRequests,
        received = server.FaultRequestCount,
        idempotencyKeySeen = server.LastIdempotencyKey,
        error = failureSeen?.GetType().Name,
    };
}

sealed class LoopbackServer : IAsyncDisposable
{
    private const string StaticToken = "loopback-static-token";
    private readonly HttpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private int tokenRequests;
    private int discoveryRequests;
    private int apiRequests;
    private int unauthorizedApi;
    private int rejectedTokenOne;
    private int wrongAuthorization;
    private int faultRequests;
    private string? lastIdempotencyKey;
    private string? lastFields;
    private string? lastRawUrl;

    private LoopbackServer(HttpListener listener, Uri baseUri)
    {
        this.listener = listener;
        BaseUri = baseUri;
        loop = Task.Run(ServeAsync);
    }

    public Uri BaseUri { get; }
    public bool DelayApi { get; set; }
    public string? FaultMode { get; set; }
    public int TokenRequestCount => Volatile.Read(ref tokenRequests);
    public int DiscoveryRequestCount => Volatile.Read(ref discoveryRequests);
    public int ApiRequestCount => Volatile.Read(ref apiRequests);
    public int UnauthorizedApiCount => Volatile.Read(ref unauthorizedApi);
    public int WrongAuthorizationCount => Volatile.Read(ref wrongAuthorization);
    public int FaultRequestCount => Volatile.Read(ref faultRequests);
    public string? LastIdempotencyKey => Volatile.Read(ref lastIdempotencyKey);
    public string? LastFields => Volatile.Read(ref lastFields);
    public string? LastRawUrl => Volatile.Read(ref lastRawUrl);

    public static Task<LoopbackServer> StartAsync()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var uri = new Uri($"http://127.0.0.1:{port}/");
        var listener = new HttpListener();
        listener.Prefixes.Add(uri.ToString());
        listener.Start();
        return Task.FromResult(new LoopbackServer(listener, uri));
    }

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch when (stop.IsCancellationRequested) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "/";
            Volatile.Write(ref lastRawUrl, request.RawUrl);
            if (path.Contains(".well-known/openid-configuration", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref discoveryRequests);
                var issuer = BaseUri.ToString().TrimEnd('/');
                await JsonAsync(context, Json(new
                {
                    issuer,
                    jwks_uri = $"{issuer}/.well-known/jwks",
                    authorization_endpoint = $"{issuer}/oauth/authorize",
                    token_endpoint = $"{issuer}/oauth/token",
                    userinfo_endpoint = $"{issuer}/oauth/userinfo",
                    grant_types_supported = new[] { "client_credentials" },
                    response_types_supported = new[] { "token" },
                    token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post" },
                }));
                return;
            }
            if (path.Equals("/.well-known/jwks", StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(context, Json(new { keys = Array.Empty<object>() }));
                return;
            }
            if (path.Equals("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref tokenRequests);
                await ReadBodyAsync(request);
                var token = Volatile.Read(ref tokenRequests) == 1 ? "token-1" : "token-2";
                await JsonAsync(context, Json(new { access_token = token, token_type = "Bearer", expires_in = 3600 }));
                return;
            }
            if (!path.Contains("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(context, "{}", HttpStatusCode.NotFound);
                return;
            }

            Interlocked.Increment(ref apiRequests);
            Volatile.Write(ref lastIdempotencyKey, request.Headers["Idempotency-Key"]);
            Volatile.Write(ref lastFields, request.QueryString["fields"]);
            var authorization = request.Headers["Authorization"];
            if (authorization is null)
            {
                Interlocked.Increment(ref wrongAuthorization);
                await JsonAsync(context, "{}", HttpStatusCode.Unauthorized);
                return;
            }
            if (authorization == "Bear" + "er " + StaticToken) { }
            else if (authorization == "Bearer token-1")
            {
                if (Interlocked.Exchange(ref rejectedTokenOne, 1) == 0)
                {
                    Interlocked.Increment(ref unauthorizedApi);
                    await JsonAsync(context, "{}", HttpStatusCode.Unauthorized);
                    return;
                }
            }
            else if (authorization != "Bearer token-2")
            {
                Interlocked.Increment(ref wrongAuthorization);
                await JsonAsync(context, "{}", HttpStatusCode.Unauthorized);
                return;
            }

            var faultTarget = (FaultMode == "GET-503" && request.HttpMethod == "GET" && path.EndsWith("/ServerTime", StringComparison.OrdinalIgnoreCase))
                || (FaultMode == "POST-503" && request.HttpMethod == "POST")
                || (FaultMode == "PATCH-503" && request.HttpMethod == "PATCH")
                || (FaultMode == "POST-network" && request.HttpMethod == "POST")
                || (FaultMode == "PATCH-network" && request.HttpMethod == "PATCH");
            if (faultTarget && Interlocked.Increment(ref faultRequests) == 1)
            {
                if (FaultMode!.EndsWith("-network", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Abort();
                    return;
                }
                await JsonAsync(context, Json(new
                {
                    data = (object?)null,
                    error = new { code = "Internal", message = "synthetic transient" },
                    meta = new { activityId = "synthetic-transient" },
                }), HttpStatusCode.ServiceUnavailable);
                return;
            }
            if (path.EndsWith("/ServerTime", StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(context, Json(new
                {
                    data = "2026-09-17T10:00:00Z",
                    error = (object?)null,
                    meta = new { activityId = "synthetic-time" },
                }));
                return;
            }
            if (path.EndsWith("/UsersRequest", StringComparison.OrdinalIgnoreCase))
            {
                if (DelayApi) await Task.Delay(TimeSpan.FromSeconds(3));
                var hasCursor = request.QueryString["cursor"] is not null;
                var meta = hasCursor
                    ? new { activityId = "synthetic-page-2", paging = new { nextCursor = (string?)null, hasMore = false } }
                    : new { activityId = "synthetic-page-1", paging = new { nextCursor = (string?)"cursor-1", hasMore = true } };
                await JsonAsync(context, Json(new { data = Array.Empty<object>(), error = (object?)null, meta }));
                return;
            }
            if (path.EndsWith("/GroupRecordGet/http-error", StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(context, Json(new
                {
                    data = (object?)null,
                    error = new { code = "Internal", message = "synthetic HTTP error" },
                    meta = new { activityId = "synthetic-http-activity" },
                }), HttpStatusCode.BadRequest);
                return;
            }
            if (path.EndsWith("/GroupRecordGet/error", StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(context, Json(new
                {
                    data = (object?)null,
                    error = new { code = "NotFound", message = "synthetic missing" },
                    meta = new { activityId = "synthetic-activity" },
                }));
                return;
            }
            if (request.HttpMethod == "POST")
            {
                await JsonAsync(context, Json(new { data = true, error = (object?)null, meta = new { activityId = "synthetic-post" } }));
                return;
            }
            if (request.HttpMethod == "PATCH")
            {
                await JsonAsync(context, Json(new { data = (object?)null, error = (object?)null, meta = new { activityId = "synthetic-patch" } }));
                return;
            }
            await JsonAsync(context, Json(new
            {
                data = (object?)null,
                error = new { code = "NotFound", message = "unhandled synthetic route" },
                meta = new { activityId = "synthetic-unhandled" },
            }));
        }
        catch (HttpListenerException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        finally { try { context.Response.Close(); } catch { } }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task JsonAsync(HttpListenerContext context, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        context.Response.ContentEncoding = Encoding.UTF8;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        listener.Close();
        try { await loop.ConfigureAwait(false); } catch { }
        stop.Dispose();
    }
}

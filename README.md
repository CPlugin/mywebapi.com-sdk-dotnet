# MyWebApi.Sdk — .NET SDK for the CPlugin WebAPI

.NET client for the MyWebAPI.com trading platform management API (v2). Usable from any .NET language (C#/F#/VB); targets `netstandard2.0` + `net8.0`.

Two NuGet packages, one version and release cycle (root namespaces in code are `CPlugin.SaaSWebApi.*`):

- **`MyWebApi.Sdk`** — the full SDK: `CPluginWebApiClient` with a generated method for **every** v2 endpoint across both supported platform families, OAuth2 client_credentials with transparent refresh, safe-method-only 401 replay, typed `ApiError`, cursor pagination, SignalR real-time clients with auto-reconnect, optional DI integration.
- **`MyWebApi.Sdk.Models`** — generated POCO DTOs + v2 response envelopes only. Zero dependencies beyond `System.Text.Json`. Use this when you build your own HTTP layer.

The WebAPI works with MetaTrader 4 and MetaTrader 5 servers through their Manager API, so a .NET service — on Linux as well as Windows — gets REST and WebSocket (SignalR) access to a broker's trade server without the native Windows Manager API libraries.

- Product and sign-up: <https://mywebapi.com>
- API reference: <https://cplugin.com/docs/webapi> · interactive: <https://cloud.mywebapi.com/swagger>
- Pricing: <https://cplugin.com/docs/pricing-and-terms>

## What brokers do with it

Typical back-office tasks, each with the SDK call that performs it. `client` is created as in [Quick start](#quick-start), `mt4` is `client.MT4(tradePlatform)` and `mt5` is `client.MT5(tradePlatform)`; DTOs live in `CPlugin.SaaSWebApi.Models`.

**List open positions of a group** (MT4 `AdmTradesRequest`, MT5 `PositionByGroup`):

```csharp
var mt4Trades = await mt4.AdmTradesRequestAsync("real-usd", openOnly: true);
var mt5Positions = await mt5.PositionByGroupAsync(@"real\*");
foreach (var p in mt5Positions.Items)
    Console.WriteLine($"{p.Login} {p.Symbol} {p.Volume} {p.Profit}");
```

**Stream trades in real time** (SignalR hub; the MT4 hub streams trades, ticks, account and symbol changes and margin calls):

```csharp
await using var hub = client.Realtime.MT4(tradePlatform);
await hub.StartAsync();
await foreach (var t in hub.StreamTradesAsync(ct))
    Console.WriteLine($"{t.Kind} {t.Order} {t.Login} {t.Symbol} {t.VolumeLots}");
```

**Open an account from a CRM** (`UserRecordNew`, then `UserPasswordSet`):

```csharp
var user = await mt4.UserRecordNewAsync(
    new MT4UserCreate { Login = 0, Group = "real-usd", Name = "John Smith", Email = "john@example.com", Leverage = 100 },
    new CallOptions { IdempotencyKey = crmRequestId });
await mt4.UserPasswordSetAsync(user!.Login!.Value, newPassword);
```

**Post a deposit or a withdrawal** (`TradeTransaction` balance operation; a negative amount withdraws):

```csharp
await mt4.TradeTransactionAsync(
    new MT4TradeTransaction { TradeTransactionType = "BrBalance", TradeCommand = "Balance", OrderBy = 1001, Price = 500, Comment = "Deposit #8812" },
    new CallOptions { IdempotencyKey = paymentId });
```

**Move an account to another group or change its leverage** (JSON Merge Patch, MT4 and MT5):

```csharp
await mt4.UserRecordAsync(1001, new { group = "real-vip", leverage = 200 });
await mt5.UserRecordAsync(50001, new { leverage = 200 });
```

**Read trade history for reports and statements** (`TradesUserHistory`, MT5 `DealByGroup`):

```csharp
var closed = await mt4.TradesUserHistoryAsync(1001,
    fromTime: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
    toTime: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
var mt5Deals = await mt5.DealByGroupAsync(@"real\*", limit: 1000);
```

**Watch margin levels** (cached snapshot of every account, then the live margin-call stream):

```csharp
var atRisk = (await mt4.MarginsGetAsync()).Where(m => m.Level is > 0 and < 100).ToList();
await foreach (var m in hub.StreamMarginCallUpdatesAsync(ct))
    Console.WriteLine($"margin call {m.Login} {m.Level}");
```

**Change symbol settings, for example swaps** (`SymbolConfig` on MT4, `SymbolRecord` on MT5):

```csharp
await mt4.SymbolConfigAsync("EURUSD", new { swapLong = -6.1, swapShort = 1.2 });
await mt5.SymbolRecordAsync("EURUSD", new { swapLong = -6.1, swapShort = 1.2 });
```

Every other endpoint (trading groups, server configuration, backups, journal, charts, news, plugins) is a method on the `MT4(...)` / `MT5(...)` namespace; see the [API reference](https://cplugin.com/docs/webapi).

## Install

```bash
dotnet add package MyWebApi.Sdk
```

## Environment presets

Pick an environment at construction time — no URL configuration needed:

| Environment | API base URL | Auth URL |
|-------------|--------------|----------|
| `CPluginEnvironment.Prod` | `https://cloud.mywebapi.com` | `https://auth.cplugin.net` |
| `CPluginEnvironment.Staging` | `https://pre.mywebapi.com` | `https://pre.auth.cplugin.net` |
| `CPluginEnvironment.Custom` | supply `ApiBaseUrl` + `Authority` | — |

Client credentials (ID and secret) are managed through the **CPlugin Toolbox**:

- Staging: <https://pre.toolbox.cplugin.com>
- Production: <https://toolbox.cplugin.com>

## Quick start

```csharp
using CPlugin.SaaSWebApi.Client;

using var client = new CPluginWebApiClient(CPluginEnvironment.Staging, clientId, clientSecret);

// * Discover platforms available to this credential.
var platforms = await client.ListTradePlatformsAsync();
var tradePlatform = Guid.Parse(platforms[0]!["id"]!.GetValue<string>());

// * Bind a platform once; every v2 endpoint is a method on the namespace.
var mt4 = client.MT4(tradePlatform);
var time = await mt4.ServerTimeAsync();          // token acquisition + refresh under the hood
var user = await mt4.UserRecordGetAsync(1001);   // typed DTOs with XML-doc from the API spec
```

Token management (OAuth2 client_credentials flow) is fully automatic: lazy acquisition on first call, bounded discovery caching with invalidation after failures, expiry skew, single-flight refresh, and one 401 retry only for GET/HEAD/OPTIONS. Unsafe requests are never replayed after a 401.

### DI (recommended for ASP.NET Core hosts)

```csharp
using CPlugin.SaaSWebApi.Client;
using CPlugin.SaaSWebApi.Client.DependencyInjection;

services.AddCPluginWebApiSdk(sp => new()
{
    Environment  = CPluginEnvironment.Prod,
    ClientId     = "your-client-id",
    ClientSecret = builder.Configuration["CPlugin:ClientSecret"],
});

// In a service:
public sealed class MyService(CPluginWebApiClient client)
{
    public Task<DateTimeOffset> Probe(Guid tp) => client.MT4(tp).ServerTimeAsync();
}
```

The DI extension wires `IHttpClientFactory`-backed HttpClients, the OAuth2 handler chain, and a bounded resilience pipeline that retries only GET/HEAD/OPTIONS on transient HTTP status responses; unsafe methods are never repeated automatically (see [Timeouts and retries](#timeouts-and-retries)). Options are validated on first resolution and surface as `OptionsValidationException`.

### Static token (advanced / testing)

```csharp
using var client = new CPluginWebApiClient(new CPluginWebApiClientOptions
{
    Environment = CPluginEnvironment.Staging,
    Token = pastedJwt, // no refresh-on-expiry in this mode
});
```

## Pagination

Cursor-paginated endpoints return `Page<T>` (`Items`, `NextCursor`, `HasMore`). `PageIterator` walks the cursor for you:

```csharp
var mt4 = client.MT4(tradePlatform);

// * Page by page — no full dataset loaded into memory at once.
await foreach (var page in PageIterator.PagesAsync(cur => mt4.UsersRequestAsync(limit: 100, cursor: cur)))
    foreach (var user in page.Items)
        Console.WriteLine($"{user.Login} {user.Balance}");

// * Or as a flat item stream.
await foreach (var trade in PageIterator.ItemsAsync(cur => mt4.TradesRequestAsync(limit: 200, cursor: cur)))
    Process(trade);
```

## Error handling

Methods return the payload directly. When the v2 envelope carries an error, the SDK throws `ApiError`:

```csharp
try
{
    var user = await mt4.UserRecordGetAsync(login);
}
catch (ApiError err)
{
    Console.WriteLine($"[{err.Code}] {err.Description}");
    // * Quote ActivityId when contacting support — it locates the request in server logs.
    Console.WriteLine($"activity: {err.ActivityId}, manager code: {err.ManagerCode}");
}
```

OAuth2 token-endpoint and OIDC discovery failures throw `CPlugin.SaaSWebApi.Client.Auth.OAuth2TokenException` (a subclass of `HttpRequestException`). Catch `HttpRequestException` broadly to handle auth and transport failures uniformly.

## Timeouts and retries

Almost every call addressed to a trading platform has a server-side deadline. When the trading server does not answer in time, the API answers with an error instead of waiting indefinitely. Defaults per kind of operation (each method's XML documentation names its own):

| Operation kind | Default |
|---|---|
| trade | 5 s |
| read | 10 s |
| change | 15 s |
| history | 30 s |
| maintenance | 60 s |

Choose another deadline, from 1 to 300 seconds, per call or for the whole client. The SDK sends it as the `X-Request-Timeout` header:

```csharp
// * One call.
var trades = await mt4.ReportsRequestAsync(from, to, options: new CallOptions { RequestTimeout = TimeSpan.FromSeconds(90) });

// * Every call that sets none of its own.
using var client = new CPluginWebApiClient(new CPluginWebApiClientOptions
{
    Environment    = CPluginEnvironment.Prod,
    ClientId       = clientId,
    ClientSecret   = clientSecret,
    RequestTimeout = TimeSpan.FromSeconds(20),
});
```

The client waits for the answer 30 s longer than the server-side deadline (the server may extend a deadline by up to 20 s while it connects to the platform), so that normally the server's own answer arrives first; `CPluginWebApiClientOptions.Timeout` (default 30 s) is only the minimum wait. Every operation has a server-side deadline and honours `X-Request-Timeout`, including those served by the x86 sidecar (plugins, mail, news, snapshots, sync, binary external commands). If the client-side wait passes — a slow network, a slow token request — the call throws `TaskCanceledException` wrapping a `TimeoutException`. For a change or a trade that means the outcome is unknown: treat it exactly like `OutcomeUnknown` below.

A request that did not finish in time throws `ApiError` with one of these codes; `Outcome` carries the `X-Request-Outcome` response header:

| `Code` | `Outcome` | Meaning | What to do |
|---|---|---|---|
| `Timeout` | `timeout` | A read did not finish. Nothing was changed. | Repeat, possibly with a longer `RequestTimeout`. |
| `Busy` | `not-started` | Refused before it was sent to the trading platform. | Repeat after a pause. |
| `OutcomeUnknown` | `unknown` | A change or a trade did not finish and **may still be applied**. | Do not repeat blindly — see below. |
| `OutcomeUnknown` | `in-progress` | A request with the same `Idempotency-Key` is still running; this one was not executed. | Repeat later with the same key. |

`ApiError.IsSafeToRetry` is `true` only for the first two rows; `IsTimeout`, `IsBusy`, `IsOutcomeUnknown` and `IsInProgress` name each case, and `ApiErrorCodes` / `RequestOutcomes` hold the values.

Recovery after `OutcomeUnknown`: send every change and trade with an `Idempotency-Key`, and repeat it with **the same key**. The server executes a key once: while the first request still runs, a repeat gets `OutcomeUnknown` with `Outcome = in-progress` and is not executed; once it has finished, a repeat gets the original result.

```csharp
var key = Guid.NewGuid().ToString();
for (var attempt = 1; ; attempt++)
{
    try
    {
        return await mt4.TradeTransactionAsync(trade, new CallOptions { IdempotencyKey = key });
    }
    catch (ApiError err) when (err.IsOutcomeUnknown && attempt < 5)
    {
        await Task.Delay(TimeSpan.FromSeconds(2 * attempt)); // same key: executed at most once
    }
}
```

Without a key, check the resulting state (the order, the balance, the record) before deciding to repeat.

The SDK never repeats a change or a trade on its own: not on `OutcomeUnknown`, not on a lost connection, not on a transient HTTP status, and not after a 401. The DI pipeline retries only GET/HEAD/OPTIONS on transient HTTP statuses and transport faults, and never a response whose outcome is `unknown` or `in-progress`. `Timeout` and `Busy` are not retried automatically either — the decision stays with the caller.

SignalR hub method calls addressed to a platform, and the v2 hub connection itself, fail after 60 s on the server side; subscriptions and streams are not affected.

## Real-time / SignalR

Both v2 hubs are first-class (`Microsoft.AspNetCore.SignalR.Client`, auto-reconnect). The `Realtime` accessor shares the REST client's cached token — no second OAuth round-trip:

```csharp
await using var hub = client.Realtime.MT4(tradePlatform); // or client.Realtime.MT5(...)

// ! Attach handlers BEFORE StartAsync — the server pushes the first
// ! OnConnectionStatus right after the handshake.
hub.OnConnectionStatus(s => Console.WriteLine($"connected: {s.Connected}"));

await hub.StartAsync();
await hub.SubscribeToTicksAsync("EURUSD");

await foreach (var tick in hub.StreamTicksAsync("EURUSD", ct))
    Console.WriteLine($"{tick.Symbol} {tick.Bid}/{tick.Ask}");
```

The `client.Realtime.MT4(...)` hub streams ticks, trades, margin-call events, user updates, and symbol config changes; the `client.Realtime.MT5(...)` hub streams connection status and margin-call updates (additional streams are deferred server-side).

## Examples

Runnable projects under `examples/` (staging, credentials via `WEBAPI_CLIENT_ID` / `WEBAPI_CLIENT_SECRET` env vars):

```bash
dotnet run --project examples/QuickStart   # auth, platform discovery, server time, paging
dotnet run --project examples/Streaming    # live tick stream over SignalR, Ctrl+C to stop
```

## Regenerate

The repository pins the exact .NET SDK used by regeneration and tests in `global.json`; run these commands from the repository root.

The whole endpoint surface is generated from the committed spec snapshot `spec/v2.json`:

```bash
./scripts/fetch-spec.sh          # refresh spec/v2.json (WEBAPI_BASE_URL to pick the host)
./scripts/generate-models.sh     # NSwag → src/CPlugin.SaaSWebApi.Models/Generated/Dto.g.cs
./scripts/generate-endpoints.sh  # bespoke generator → src/.../Generated/MT4Endpoints.g.cs + MT5Endpoints.g.cs
```

`generate-models.sh` requires `dotnet tool install --global NSwag.ConsoleCore`. `generate-endpoints.sh` uses the in-repo tool under `scripts/GenerateEndpoints/` and needs no extra tools. Generated files (`*.g.cs`) are committed and machine-owned — never edit them by hand.

## Test

```bash
dotnet test tests/CPlugin.SaaSWebApi.Client.Tests/CPlugin.SaaSWebApi.Client.Tests.csproj

# Gated staging E2E (REST only):
WEBAPI_E2E=1 WEBAPI_CLIENT_ID=... WEBAPI_CLIENT_SECRET=... WEBAPI_TRADE_PLATFORM=... \
  dotnet test --filter StagingE2ETests
```

## Layout

```
.
├── CPlugin.SaaSWebApi.Client.sln
├── src/
│   ├── CPlugin.SaaSWebApi.Models/
│   │   └── Generated/Dto.g.cs            # NSwag output: DTOs + v2 envelopes (machine-owned)
│   └── CPlugin.SaaSWebApi.Client/
│       ├── Generated/                    # MT4Endpoints.g.cs / MT5Endpoints.g.cs (machine-owned)
│       ├── Auth/                         # TokenCache, OidcDiscoveryClient, ClientCredentialsHandler
│       ├── CPluginWebApiClient.cs        # entry point: MT4()/MT5()/Realtime/ListTradePlatformsAsync
│       ├── Environments.cs               # env presets (prod / staging / custom)
│       ├── ApiError.cs                   # envelope error exception {Code, Description, ActivityId, Outcome}
│       ├── CallOptions.cs                # per-call idempotency key, fields, request timeout, cancellation
│       ├── Page.cs                       # Page<T> + PageIterator cursor helpers
│       ├── MT4V2SignalRClient.cs         # /hubs/mt4/v2
│       ├── MT5V2SignalRClient.cs         # /hubs/mt5/v2
│       └── DependencyInjection/          # AddCPluginWebApiSdk (net8.0 only)
├── tests/CPlugin.SaaSWebApi.Client.Tests/  # hermetic contract tests + gated E2E/
├── examples/                             # QuickStart, Streaming
├── spec/v2.json                          # OpenAPI spec snapshot (source of the generated surface)
└── scripts/                              # fetch-spec, generate-models (NSwag), GenerateEndpoints (bespoke facade)
```

## License

[MIT](LICENSE). Publishing to NuGet.org is a separate, explicit release step — see [PUBLISHING.md](PUBLISHING.md).

## Trademarks

MetaTrader, MT4, MT5, and MetaQuotes are trademarks or registered trademarks of MetaQuotes Ltd.
This project is an independent SDK for the WebAPI service.
It is **not affiliated with, endorsed by, or sponsored by MetaQuotes Ltd.**

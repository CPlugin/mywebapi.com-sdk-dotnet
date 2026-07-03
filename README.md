# MyWebApi.Sdk — .NET SDK for the CPlugin WebAPI

.NET client for the MyWebAPI.com trading platform management API (v2). Usable from any .NET language (C#/F#/VB); targets `netstandard2.0` + `net8.0`.

Two NuGet packages, one version and release cycle (root namespaces in code are `CPlugin.SaaSWebApi.*`):

- **`MyWebApi.Sdk`** — the full SDK: `CPluginWebApiClient` with a generated method for **every** v2 endpoint across both supported platform families, OAuth2 client_credentials with transparent refresh and 401 retry, typed `ApiError`, cursor pagination, SignalR real-time clients with auto-reconnect, optional DI integration.
- **`MyWebApi.Sdk.Models`** — generated POCO DTOs + v2 response envelopes only. Zero dependencies beyond `System.Text.Json`. Use this when you build your own HTTP layer.

## Install

```bash
dotnet add package MyWebApi.Sdk
```

> Until the first release lands on NuGet.org (see [PUBLISHING.md](PUBLISHING.md)), reference the project directly from a checkout instead:
> `<ProjectReference Include="path/to/cplugin-webapi-sdk-dotnet/src/CPlugin.SaaSWebApi.Client/CPlugin.SaaSWebApi.Client.csproj" />`

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

Token management (OAuth2 client_credentials flow) is fully automatic: lazy acquisition on first call, caching with expiry skew, single-flight refresh, and one retry on `401`.

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

The DI extension wires `IHttpClientFactory`-backed HttpClients, the OAuth2 handler chain, and `AddStandardResilienceHandler` (retries with jitter + circuit breaker). Options are validated on first resolution and surface as `OptionsValidationException`.

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
│       ├── ApiError.cs                   # envelope error exception {Code, Description, ActivityId}
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

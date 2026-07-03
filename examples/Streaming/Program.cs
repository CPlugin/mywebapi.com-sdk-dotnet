// Real-time streaming: MT4 ticks over SignalR with auto-reconnect.
//
// Targets STAGING. Set credentials first:
//
//     export WEBAPI_CLIENT_ID=your-client-id
//     export WEBAPI_CLIENT_SECRET=your-client-secret
//     export WEBAPI_SYMBOL=EURUSD          # optional, defaults to EURUSD
//     dotnet run --project examples/Streaming
//
// Press Ctrl+C to stop.

using CPlugin.SaaSWebApi.Client;

var clientId = Require("WEBAPI_CLIENT_ID");
var clientSecret = Require("WEBAPI_CLIENT_SECRET");
var symbol = Environment.GetEnvironmentVariable("WEBAPI_SYMBOL") ?? "EURUSD";

using var client = new CPluginWebApiClient(CPluginEnvironment.Staging, clientId, clientSecret);

// * Auto-select the platform when only one exists.
var platforms = await client.ListTradePlatformsAsync();
if (platforms.Count == 0)
{
    Console.WriteLine("No platforms found — check that the credential has platform access.");
    return;
}
var tradePlatform = Guid.Parse(platforms[0]!["id"]!.GetValue<string>());
Console.WriteLine($"Streaming {symbol} ticks from platform {tradePlatform} — Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// * The realtime client shares the REST client's cached OAuth token —
//   no second token round-trip to bring the hub up.
await using var hub = client.Realtime.MT4(tradePlatform);

// ! Attach the status handler BEFORE StartAsync — the server pushes the first
// ! OnConnectionStatus right after the handshake.
hub.OnConnectionStatus(s => Console.WriteLine($"[status] platform connected: {s.Connected}"));

await hub.StartAsync(cts.Token);
await hub.SubscribeToTicksAsync(symbol, cts.Token);

try
{
    await foreach (var tick in hub.StreamTicksAsync(symbol, cts.Token))
        Console.WriteLine($"{tick.Symbol}  bid={tick.Bid}  ask={tick.Ask}");
}
catch (OperationCanceledException)
{
    // * Ctrl+C — clean shutdown.
}

await hub.StopAsync();
Console.WriteLine("Stopped.");

static string Require(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Set the {name} environment variable first.");

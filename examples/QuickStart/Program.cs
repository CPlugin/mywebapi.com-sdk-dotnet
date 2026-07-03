// Quickstart: authenticate, discover platforms, read the server time, page users.
//
// This example targets the STAGING environment so no production data is touched.
// Set your credentials before running:
//
//     export WEBAPI_CLIENT_ID=your-client-id
//     export WEBAPI_CLIENT_SECRET=your-client-secret
//     dotnet run --project examples/QuickStart
//
// You can obtain credentials from the CPlugin Toolbox:
//     https://pre.toolbox.cplugin.com  (staging)
//     https://toolbox.cplugin.com      (production)

using CPlugin.SaaSWebApi.Client;

var clientId = Require("WEBAPI_CLIENT_ID");
var clientSecret = Require("WEBAPI_CLIENT_SECRET");

// * Staging by default — safe for exploration without touching production data.
using var client = new CPluginWebApiClient(CPluginEnvironment.Staging, clientId, clientSecret);

// -- discover platforms -------------------------------------------------------
var platforms = await client.ListTradePlatformsAsync();
if (platforms.Count == 0)
{
    Console.WriteLine("No platforms found — check that the credential has platform access.");
    return;
}

Console.WriteLine($"Found {platforms.Count} platform(s):");
foreach (var p in platforms)
    Console.WriteLine($"  {p?["id"]}  |  {p?["name"]}  |  type={p?["type"]}");

// * Auto-select the platform when only one exists — credentials-only onboarding.
var tradePlatform = Guid.Parse(platforms[0]!["id"]!.GetValue<string>());
var mt4 = client.MT4(tradePlatform);
Console.WriteLine();

// -- server time --------------------------------------------------------------
try
{
    var time = await mt4.ServerTimeAsync();
    Console.WriteLine($"Server time: {time:O}");
}
catch (ApiError err)
{
    Console.WriteLine($"API error [{err.Code}]: {err.Description}");
    if (err.ActivityId is not null)
        Console.WriteLine($"  Activity ID: {err.ActivityId}");
}

// -- pagination example -------------------------------------------------------
// * Page<T> carries the cursor; PageIterator walks pages without loading the
//   full dataset. Here we show just the first page.
Console.WriteLine();
Console.WriteLine("First page of users (limit=5):");
var page = await mt4.UsersRequestAsync(limit: 5);
foreach (var user in page.Items)
    Console.WriteLine($"  login={user.Login}  group={user.Group}");
Console.WriteLine(page.HasMore ? "  ... more pages available" : "  (no more pages)");

static string Require(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Set the {name} environment variable first.");

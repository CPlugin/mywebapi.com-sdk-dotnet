using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public sealed class SignalRLoopbackServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SignalRLoopbackServer(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static async Task<SignalRLoopbackServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        var app = builder.Build();
        app.MapHub<LoopbackHub>("/hubs/mt4/v2");
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Urls.Single();
        return new SignalRLoopbackServer(app, new Uri(address.EndsWith("/", StringComparison.Ordinal) ? address : address + "/"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class LoopbackHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("OnConnectionStatus", new { connected = true }).ConfigureAwait(false);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public Task SubscribeToTicks(string symbol) =>
        Clients.Caller.SendAsync("OnTick", new
        {
            symbol,
            bid = 1.2,
            ask = 1.3,
            lastTime = DateTime.UtcNow,
        });

    public Task UnsubscribeFromTicks(string symbol) => Task.CompletedTask;
}

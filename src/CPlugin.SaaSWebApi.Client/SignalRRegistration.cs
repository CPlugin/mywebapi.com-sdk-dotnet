using System;
using Microsoft.AspNetCore.SignalR.Client;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Registration handle for hub callbacks attached <b>before</b> the connection
/// is built. The attach action runs when the connection materialises (inside
/// <c>StartAsync</c>, before the WebSocket handshake), so early server pushes — e.g. the
/// post-handshake <c>OnConnectionStatus</c> — are never missed.</summary>
/// <remarks>
/// // ! This mirrors the pending-handlers fix from the TypeScript SDK (race-free
/// //   OnConnectionStatus): handlers queued pre-start are drained onto the
/// //   HubConnection BEFORE conn.StartAsync().
/// </remarks>
internal sealed class DeferredRegistration : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<HubConnection, IDisposable> _attach;
    private IDisposable? _inner;
    private bool _disposed;

    public DeferredRegistration(Func<HubConnection, IDisposable> attach) => _attach = attach;

    /// <summary>Attach to the just-built connection; no-op if the handle was disposed
    /// while still pending.</summary>
    public void Materialize(HubConnection connection)
    {
        // * Same lock as Dispose(): a caller disposing the handle concurrently with the
        //   StartAsync drain must never end up with a silently-attached handler.
        lock (_sync)
        {
            if (_disposed) return;
            _inner = _attach(connection);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _inner?.Dispose();
            _inner = null;
        }
    }
}

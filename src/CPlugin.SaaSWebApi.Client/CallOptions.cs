using System.Collections.Generic;
using System.Threading;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Per-call cross-cutting options: idempotency, sparse fieldsets, cancellation.</summary>
public sealed class CallOptions
{
    /// <summary>Optional idempotency key (Stripe/AWS convention). Any string ≤255 chars.</summary>
    public string? IdempotencyKey { get; init; }
    /// <summary>Field selection (?fields=). Subset of response DTO property names.</summary>
    public IReadOnlyCollection<string>? Fields { get; init; }
    /// <summary>Cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }
}

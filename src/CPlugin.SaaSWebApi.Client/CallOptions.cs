using System;
using System.Collections.Generic;
using System.Threading;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Per-call cross-cutting options: idempotency, sparse fieldsets, request timeout, cancellation.</summary>
public sealed class CallOptions
{
    /// <summary>Smallest accepted <see cref="RequestTimeout"/>: 1 s.</summary>
    public static readonly TimeSpan MinRequestTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Largest accepted <see cref="RequestTimeout"/>: 300 s.</summary>
    public static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromSeconds(300);

    private readonly TimeSpan? _requestTimeout;

    /// <summary>Optional idempotency key (Stripe/AWS convention). Any string ≤255 chars.</summary>
    /// <remarks>Set one on every change and trade: it is what makes repeating the call safe
    /// after <see cref="ApiError.IsOutcomeUnknown"/> or a lost connection — the server executes a
    /// key once and answers repeats with the original result.</remarks>
    public string? IdempotencyKey { get; init; }
    /// <summary>Field selection (?fields=). Subset of response DTO property names.</summary>
    public IReadOnlyCollection<string>? Fields { get; init; }
    /// <summary>Cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>How long the server waits for the trading server before it answers with
    /// <c>Timeout</c> or <c>OutcomeUnknown</c>, sent as the <c>X-Request-Timeout</c> header.
    /// From <see cref="MinRequestTimeout"/> to <see cref="MaxRequestTimeout"/>.</summary>
    /// <remarks>Null — <see cref="CPluginWebApiClientOptions.RequestTimeout"/>, or else the
    /// server's default for the operation (trade 5 s, read 10 s, change 15 s, history 30 s,
    /// maintenance 60 s; each method's documentation names its own). The client waits for the
    /// answer 30 s longer than this, whatever <see cref="CPluginWebApiClientOptions.Timeout"/> says.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1–300 s.</exception>
    public TimeSpan? RequestTimeout
    {
        get => _requestTimeout;
        init => _requestTimeout = value is { } v ? ValidateRequestTimeout(v, nameof(RequestTimeout)) : null;
    }

    internal static TimeSpan ValidateRequestTimeout(TimeSpan value, string paramName)
    {
        if (value < MinRequestTimeout || value > MaxRequestTimeout)
            throw new ArgumentOutOfRangeException(paramName, value,
                "Request timeout must be from 1 to 300 seconds.");
        return value;
    }
}

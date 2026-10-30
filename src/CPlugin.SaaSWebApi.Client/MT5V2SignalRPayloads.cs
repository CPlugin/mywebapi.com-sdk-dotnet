using System;
using System.Collections.Generic;

namespace CPlugin.SaaSWebApi.Client;

// * Names and shapes mirror `WebAPI/Hubs/MT5/v2/MT5V2Payloads.cs` exactly
//   (same contract the TypeScript SDK's signalr.mt5.ts binds to).

/// <summary>Pushed by the hub right after the handshake and whenever the
/// platform connection state changes.</summary>
public sealed record MT5ConnectionStatusPayload(bool Connected);

/// <summary>Margin-call event for one account on the bound trade platform.</summary>
public sealed record MT5MarginCallPayload(long Login, string Type, string Direction);

/// <summary>Market tick of one symbol on the bound trade platform, pushed by <c>OnTick</c>
/// and by the <c>StreamTicks</c> stream.</summary>
/// <param name="Symbol">Instrument name as reported by the trade server.</param>
/// <param name="Bid">Best bid price of the tick.</param>
/// <param name="Ask">Best ask price of the tick.</param>
/// <param name="Last">Price of the last committed transaction; zero when the price feed carries none.</param>
/// <param name="LastTime">Tick time in the trade server's time zone.</param>
/// <param name="VolumeExt">Tick volume with extended precision, as supplied by the data provider.</param>
/// <param name="Flags">Names of the set tick flags, for example <c>Bid, Ask</c>.</param>
public sealed record MT5TickPayload(
    string    Symbol,
    double    Bid,
    double    Ask,
    double    Last,
    DateTime? LastTime,
    ulong     VolumeExt,
    string    Flags);

/// <summary>Batch of ticks of one symbol, pushed by <c>OnTicksBatch</c> instead of one
/// <c>OnTick</c> message per tick. The list is ordered oldest first and is never empty.</summary>
/// <param name="Symbol">Instrument name the batch belongs to — the symbol given to
/// <c>SubscribeToTicksBatch</c>.</param>
/// <param name="Ticks">Ticks accumulated since the previous batch, oldest first.</param>
public sealed record MT5TicksBatchPayload(string Symbol, IReadOnlyList<MT5TickPayload> Ticks);

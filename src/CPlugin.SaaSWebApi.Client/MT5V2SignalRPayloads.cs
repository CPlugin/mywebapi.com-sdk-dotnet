namespace CPlugin.SaaSWebApi.Client;

// * Names and shapes mirror `WebAPI/Hubs/MT5/v2/MT5V2Payloads.cs` exactly
//   (same contract the TypeScript SDK's signalr.mt5.ts binds to).

/// <summary>Pushed by the hub right after the handshake and whenever the
/// platform connection state changes.</summary>
public sealed record MT5ConnectionStatusPayload(bool Connected);

/// <summary>Margin-call event for one account on the bound trade platform.</summary>
public sealed record MT5MarginCallPayload(long Login, string Type, string Direction);

using System;

namespace CPlugin.SaaSWebApi.Client;

// * SignalR payload records — mirror the server contract at
// *   WebAPI/Hubs/MT4/v2/MT4V2Payloads.cs exactly. Records with init-only setters,
// *   so SignalR's default JSON deserialiser (Newtonsoft on the server, STJ on
// *   the client) populates them correctly without ceremony.

/// <summary>Pump connection up/down notification — emitted once per status change,
/// plus once on connect.</summary>
public sealed record ConnectionStatusPayload(bool Connected);

/// <summary>Single tick update.</summary>
public sealed record TickPayload
{
    public string  Symbol   { get; init; } = string.Empty;
    public double  Bid      { get; init; }
    public double  Ask      { get; init; }
    public DateTime? LastTime { get; init; }
}

/// <summary>Margin call snapshot for one account.</summary>
public sealed record MarginCallPayload
{
    public int     Login            { get; init; }
    public string? Group            { get; init; }
    public int     Leverage         { get; init; }
    public int     Updated          { get; init; }
    public double  Balance          { get; init; }
    public double  Equity           { get; init; }
    public decimal VolumeLots       { get; init; }
    public double  Margin           { get; init; }
    public double  Free             { get; init; }
    public double  Level            { get; init; }
    public int     ControllingType  { get; init; }
    public int     LevelType        { get; init; }
}

/// <summary>Lifecycle marker for a trade record event.</summary>
public enum TradeUpdateKind { Added, Updated, Deleted }

/// <summary>Order/trade record state transition.</summary>
public sealed record TradeUpdatePayload
{
    public TradeUpdateKind Kind        { get; init; }
    public int             Order       { get; init; }
    public int             Login       { get; init; }
    public string          Symbol      { get; init; } = string.Empty;
    public int             Cmd         { get; init; }
    public decimal         VolumeLots  { get; init; }
    public double          OpenPrice   { get; init; }
    public DateTime        OpenTime    { get; init; }
    public double          Sl          { get; init; }
    public double          Tp          { get; init; }
    public double          ClosePrice  { get; init; }
    public DateTime        CloseTime   { get; init; }
    public double          Commission  { get; init; }
    public double          Storage     { get; init; }
    public double          Profit      { get; init; }
    public double          Taxes       { get; init; }
    public string          Comment     { get; init; } = string.Empty;
}

public enum UserUpdateKind { Added, Updated, Deleted }

/// <summary>Trading account state transition. Slim by design — password / OTP /
/// MQID / reserved blobs are deliberately NOT surfaced.</summary>
public sealed record UserUpdatePayload
{
    public UserUpdateKind Kind     { get; init; }
    public int            Login    { get; init; }
    public string         Group    { get; init; } = string.Empty;
    public string         Name     { get; init; } = string.Empty;
    public string         Country  { get; init; } = string.Empty;
    public int            Leverage { get; init; }
    public double         Balance  { get; init; }
    public double         Credit   { get; init; }
    public bool           Enabled  { get; init; }
    public bool           ReadOnly { get; init; }
    public DateTime       RegDate  { get; init; }
    public DateTime       LastDate { get; init; }
    public string         Email    { get; init; } = string.Empty;
    public string         Phone    { get; init; } = string.Empty;
    public string         Comment  { get; init; } = string.Empty;
}

public enum SymbolUpdateKind { Added, Updated, Deleted }

/// <summary>Symbol configuration state transition.</summary>
public sealed record SymbolUpdatePayload
{
    public SymbolUpdateKind Kind             { get; init; }
    public string           Symbol           { get; init; } = string.Empty;
    public string           Description      { get; init; } = string.Empty;
    public string           Currency         { get; init; } = string.Empty;
    public int              Digits           { get; init; }
    public int              TradeMode        { get; init; }
    public int              Spread           { get; init; }
    public int              StopsLevel       { get; init; }
    public int              FreezeLevel      { get; init; }
    public double           ContractSize     { get; init; }
    public double           TickValue        { get; init; }
    public double           TickSize         { get; init; }
    public double           MarginInitial    { get; init; }
    public double           MarginMaintenance { get; init; }
}

using System;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Thrown when a v2 response envelope carries a non-null <c>error</c>.</summary>
/// <remarks>
/// Named <c>ApiError</c> (not <c>*Exception</c>) deliberately, for surface parity with the
/// TypeScript and Python SDKs — the three SDKs expose the same error shape
/// <c>{ code, description, activityId }</c>.
/// </remarks>
public sealed class ApiError : Exception
{
    /// <summary>Stable transport-level error code (e.g. <c>NotFound</c>, <c>Forbidden</c>,
    /// <c>Timeout</c>, <c>OutcomeUnknown</c>, <c>Busy</c>, <c>Internal</c>) — see <see cref="ApiErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>Human-readable error description from the server.</summary>
    public string? Description { get; }

    /// <summary>W3C trace id — quote it when contacting support; it locates the request in server logs.</summary>
    public string? ActivityId { get; }

    /// <summary>Raw platform manager result code when the error came from the trading platform; otherwise null.</summary>
    public string? ManagerCode { get; }

    /// <summary>HTTP status of the response that carried the envelope.</summary>
    public int Status { get; }

    /// <summary>What happened to a request that did not finish in time — the
    /// <c>X-Request-Outcome</c> response header (see <see cref="RequestOutcomes"/>); null otherwise.</summary>
    public string? Outcome { get; }

    /// <summary>Server-side deadline the server applied to this request
    /// (<c>X-Request-Timeout-Applied</c>); null when the server did not report one.</summary>
    public TimeSpan? AppliedRequestTimeout { get; }

    public ApiError(string code, string? description, string? activityId, string? managerCode, int status)
        : this(code, description, activityId, managerCode, status, outcome: null, appliedRequestTimeout: null)
    {
    }

    public ApiError(string code, string? description, string? activityId, string? managerCode, int status,
        string? outcome, TimeSpan? appliedRequestTimeout)
        : base(description ?? $"v2 error: {code}")
    {
        Code = code;
        Description = description;
        ActivityId = activityId;
        ManagerCode = managerCode;
        Status = status;
        Outcome = string.IsNullOrEmpty(outcome) ? null : outcome;
        AppliedRequestTimeout = appliedRequestTimeout;
    }

    /// <summary>A read did not finish in time. Nothing was changed; safe to repeat, possibly with
    /// a longer <see cref="CallOptions.RequestTimeout"/>.</summary>
    public bool IsTimeout => Is(ApiErrorCodes.Timeout);

    /// <summary>A change or a trade did not finish in time and the server may still apply it —
    /// also the answer to a repeat whose <c>Idempotency-Key</c> is still being processed
    /// (<see cref="IsInProgress"/>). Never repeat blindly: repeat with the same
    /// <see cref="CallOptions.IdempotencyKey"/> (the server executes it at most once and then
    /// returns the real result), or check the resulting state first.</summary>
    public bool IsOutcomeUnknown => Is(ApiErrorCodes.OutcomeUnknown);

    /// <summary>Too many requests wait for this trading platform; the request was refused before it
    /// was sent there. Safe to repeat after a pause.</summary>
    public bool IsBusy => Is(ApiErrorCodes.Busy);

    /// <summary>A request with the same <c>Idempotency-Key</c> is still running; this one was not
    /// executed. Repeat later with the same key to get the original result.</summary>
    public bool IsInProgress => OutcomeIs(RequestOutcomes.InProgress);

    /// <summary>True when repeating the request cannot apply anything twice: a read that timed out
    /// (<c>Timeout</c> / <c>timeout</c>), or a request refused before it reached the trading platform
    /// (<c>Busy</c> / <c>not-started</c>). False for every other error and for any other combination
    /// of code and outcome — including <see cref="IsOutcomeUnknown"/>, whose recovery is a repeat
    /// with the same <c>Idempotency-Key</c>.</summary>
    /// <remarks>The code alone decides when the outcome header is absent: the server gives
    /// <c>Timeout</c> only to reads and <c>Busy</c> only to requests it did not start.</remarks>
    public bool IsSafeToRetry =>
        (IsTimeout && (Outcome is null || OutcomeIs(RequestOutcomes.Timeout)))
        || (IsBusy && (Outcome is null || OutcomeIs(RequestOutcomes.NotStarted)));

    private bool Is(string code) => string.Equals(Code, code, StringComparison.Ordinal);

    private bool OutcomeIs(string outcome) => string.Equals(Outcome, outcome, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Values of <see cref="ApiError.Code"/> (the v2 <c>error.code</c> names).</summary>
public static class ApiErrorCodes
{
    public const string NoConnect = "NoConnect";
    public const string Validation = "Validation";
    public const string MT4Error = "MT4Error";
    public const string Forbidden = "Forbidden";
    public const string NotFound = "NotFound";
    public const string MT5Error = "MT5Error";
    /// <summary>A read did not finish in time; nothing was changed.</summary>
    public const string Timeout = "Timeout";
    /// <summary>A change or trade did not finish in time and may still be applied.</summary>
    public const string OutcomeUnknown = "OutcomeUnknown";
    /// <summary>Refused before it was sent to the trading platform.</summary>
    public const string Busy = "Busy";
    public const string Internal = "Internal";
}

/// <summary>Values of <see cref="ApiError.Outcome"/> (the <c>X-Request-Outcome</c> response header).</summary>
public static class RequestOutcomes
{
    /// <summary>A read did not finish in time.</summary>
    public const string Timeout = "timeout";
    /// <summary>A change or trade started and did not finish in time; it may still be applied.</summary>
    public const string Unknown = "unknown";
    /// <summary>Refused before it was sent to the trading platform.</summary>
    public const string NotStarted = "not-started";
    /// <summary>A request with the same <c>Idempotency-Key</c> is still running; this one was not executed.</summary>
    public const string InProgress = "in-progress";
}

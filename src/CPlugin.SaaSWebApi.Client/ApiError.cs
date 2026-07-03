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
    /// <summary>Stable transport-level error code (e.g. <c>NotFound</c>, <c>Forbidden</c>, <c>Internal</c>).</summary>
    public string Code { get; }

    /// <summary>Human-readable error description from the server.</summary>
    public string? Description { get; }

    /// <summary>W3C trace id — quote it when contacting support; it locates the request in server logs.</summary>
    public string? ActivityId { get; }

    /// <summary>Raw platform manager result code when the error came from the trading platform; otherwise null.</summary>
    public string? ManagerCode { get; }

    /// <summary>HTTP status of the response that carried the envelope.</summary>
    public int Status { get; }

    public ApiError(string code, string? description, string? activityId, string? managerCode, int status)
        : base(description ?? $"v2 error: {code}")
    {
        Code = code;
        Description = description;
        ActivityId = activityId;
        ManagerCode = managerCode;
        Status = status;
    }
}

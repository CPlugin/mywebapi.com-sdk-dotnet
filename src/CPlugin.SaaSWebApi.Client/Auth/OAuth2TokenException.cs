using System.Net;
using System.Net.Http;

namespace CPlugin.SaaSWebApi.Client.Auth;

/// <summary>Raised when an OAuth2 token-endpoint exchange or OIDC discovery
/// request fails. Inherits <see cref="HttpRequestException"/> so consumers can
/// catch broadly. <see cref="StatusCode"/> shadows the base property because
/// we need a nullable value — discovery failures may carry no HTTP response.</summary>
public sealed class OAuth2TokenException : HttpRequestException
{
    public string? Error { get; }
    public string? ErrorDescription { get; }
    // * `HttpRequestException.StatusCode` exists from .NET 5 onward (non-nullable).
    // *   On netstandard2.1 the base class doesn't define it, so `new` would be
    // *   a spurious modifier — guard with TFM.
#if NET8_0_OR_GREATER
    public new HttpStatusCode? StatusCode { get; }
#else
    public HttpStatusCode? StatusCode { get; }
#endif

    public OAuth2TokenException(string? error, string? errorDescription, HttpStatusCode? statusCode)
        : base(FormatMessage(error, errorDescription, statusCode))
    {
        Error = error;
        ErrorDescription = errorDescription;
        StatusCode = statusCode;
    }

    private static string FormatMessage(string? error, string? description, HttpStatusCode? status)
    {
        var code = error ?? status?.ToString() ?? "unknown";
        var desc = description ?? "<no description>";
        return $"OAuth2 token error ({code}): {desc}";
    }
}

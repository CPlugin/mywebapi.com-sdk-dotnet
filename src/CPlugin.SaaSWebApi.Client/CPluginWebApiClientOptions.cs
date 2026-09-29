using System;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Configuration for <see cref="CPluginWebApiClient"/> — pick an environment,
/// supply credentials once; token acquisition, caching and refresh are automatic.</summary>
/// <remarks>
/// <para>Auth modes are mutually exclusive:</para>
/// <list type="bullet">
///   <item><description><b>OAuth2 client_credentials</b> (recommended): set <see cref="ClientId"/>
///   and <see cref="ClientSecret"/>. The OIDC authority comes from the environment preset.
///   Credentials are managed in the CPlugin Toolbox
///   (staging: <c>https://pre.toolbox.cplugin.com</c>, production: <c>https://toolbox.cplugin.com</c>).</description></item>
///   <item><description><b>Static token</b>: set <see cref="Token"/>. For tests and short-lived
///   integrations; no refresh-on-expiry.</description></item>
/// </list>
/// </remarks>
public sealed record CPluginWebApiClientOptions
{
    /// <summary>Environment preset. Default: <see cref="CPluginEnvironment.Staging"/> —
    /// safe default for first experiments; switch to <see cref="CPluginEnvironment.Prod"/> explicitly.</summary>
    public CPluginEnvironment Environment { get; init; } = CPluginEnvironment.Staging;

    /// <summary>API base URL — required (and only used) when <see cref="Environment"/> is
    /// <see cref="CPluginEnvironment.Custom"/>.</summary>
    public string? ApiBaseUrl { get; init; }

    /// <summary>OIDC authority URL — required (and only used) when <see cref="Environment"/> is
    /// <see cref="CPluginEnvironment.Custom"/>.</summary>
    public string? Authority { get; init; }

    /// <summary>OAuth2 client_credentials: client identifier.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth2 client_credentials: client secret. Never hard-code it — read from
    /// configuration or environment.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Pre-issued JWT bearer. Mutually exclusive with
    /// <see cref="ClientId"/> + <see cref="ClientSecret"/>.</summary>
    public string? Token { get; init; }

    /// <summary>Optional OAuth2 scopes to request alongside client_credentials.</summary>
    public string[]? Scopes { get; init; }

    /// <summary>Minimum time the client waits for an answer to one request. Default: 30 s.</summary>
    /// <remarks>Extended automatically to the request's server-side deadline plus 30 s
    /// (<see cref="RequestTimeout"/>, <see cref="CallOptions.RequestTimeout"/> or the operation's
    /// default), so the server's own <c>Timeout</c> / <c>OutcomeUnknown</c> answer is not lost.
    /// When it expires the call throws <see cref="System.Threading.Tasks.TaskCanceledException"/>
    /// wrapping a <see cref="TimeoutException"/>; for a change or a trade the outcome is then unknown.
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables the client-side deadline
    /// (through <c>AddCPluginWebApiSdk</c> the resilience pipeline still ends an attempt after 1 hour).</remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Client-wide server-side deadline, sent as <c>X-Request-Timeout</c> on every call
    /// that sets no <see cref="CallOptions.RequestTimeout"/> of its own. 1–300 s.</summary>
    /// <remarks>Null (default) — the server applies its default per operation: trade 5 s,
    /// read 10 s, change 15 s, history 30 s, maintenance 60 s.</remarks>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary><c>true</c> when a bearer token was provided directly (static-token mode).</summary>
    public bool UsesStaticToken => !string.IsNullOrEmpty(Token);

    /// <summary>Validate eagerly — called by the client constructor so misconfigurations
    /// surface at construction, not on the first HTTP call.</summary>
    /// <exception cref="InvalidOperationException">Neither or both auth modes configured,
    /// a Custom environment without explicit URLs, or a timeout out of range.</exception>
    public (string ApiBaseUrl, string Authority) Validate()
    {
        // * Resolve first — Custom without URLs throws here.
        var resolved = CPluginEnvironments.Resolve(Environment, ApiBaseUrl, Authority);

        var hasToken = UsesStaticToken;
        var hasCc = !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);

        if (hasToken && (ClientId is not null || ClientSecret is not null))
            throw new InvalidOperationException(
                "CPluginWebApiClientOptions: set EITHER Token OR (ClientId + ClientSecret), not both.");
        if (!hasToken && !hasCc)
            throw new InvalidOperationException(
                "CPluginWebApiClientOptions: set either Token, or both ClientId and ClientSecret.");
        if (Timeout <= TimeSpan.Zero && Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            throw new InvalidOperationException(
                "CPluginWebApiClientOptions: Timeout must be positive or Timeout.InfiniteTimeSpan.");
        if (RequestTimeout is { } rt && (rt < CallOptions.MinRequestTimeout || rt > CallOptions.MaxRequestTimeout))
            throw new InvalidOperationException(
                "CPluginWebApiClientOptions: RequestTimeout must be from 1 to 300 seconds.");

        return resolved;
    }
}

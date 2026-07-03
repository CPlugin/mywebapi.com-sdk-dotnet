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

    /// <summary>Per-request timeout. Default: 30 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary><c>true</c> when a bearer token was provided directly (static-token mode).</summary>
    public bool UsesStaticToken => !string.IsNullOrEmpty(Token);

    /// <summary>Validate eagerly — called by the client constructor so misconfigurations
    /// surface at construction, not on the first HTTP call.</summary>
    /// <exception cref="InvalidOperationException">Neither or both auth modes configured,
    /// or a Custom environment without explicit URLs.</exception>
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

        return resolved;
    }
}

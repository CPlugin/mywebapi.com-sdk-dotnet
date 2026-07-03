using System;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Connection options for the per-hub SignalR clients
/// (<see cref="MT4V2SignalRClient"/> / <c>MT5V2SignalRClient</c>).
/// The unified REST entry point uses <c>CPluginWebApiClientOptions</c> instead.</summary>
/// <remarks>
/// <para>This is a <c>record</c>: <c>init</c> properties support both direct
/// object-initialiser syntax and <c>with</c> mutation.</para>
/// <para>Auth modes are mutually exclusive:</para>
/// <list type="bullet">
///   <item><description><b>Static token</b>: set <see cref="Token"/>. Useful for tests and
///   short-lived integrations where a JWT is pasted in.</description></item>
///   <item><description><b>OAuth2 client_credentials</b>: set <see cref="ClientId"/>,
///   <see cref="ClientSecret"/>, and <see cref="IdentityUrl"/>. SDK then handles
///   discovery + token acquisition + 401 retry transparently.</description></item>
/// </list>
/// </remarks>
public sealed record MT4V2ClientOptions
{
    /// <summary>WebAPI base URL (trailing slashes are stripped). E.g. <c>https://api.example.com</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Trade-platform GUID — bound to the client at construction. Every v2 MT4 endpoint is scoped by it.</summary>
    public required Guid TradePlatform { get; init; }

    /// <summary>Pre-issued JWT bearer (legacy / test path). Mutually exclusive with <see cref="ClientId"/>+<see cref="ClientSecret"/>+<see cref="IdentityUrl"/>.</summary>
    public string? Token { get; init; }

    /// <summary>OAuth2 client_credentials: client identifier.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth2 client_credentials: client secret.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>IdentityServer base URL. SDK resolves <c>token_endpoint</c> via discovery at <c>{IdentityUrl}/.well-known/openid-configuration</c>.</summary>
    public string? IdentityUrl { get; init; }

    /// <summary>Optional OAuth2 scopes to request alongside client_credentials.</summary>
    public string[]? Scopes { get; init; }

    /// <summary>Per-request timeout. Default: 30 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Validate the options eagerly — call at the boundary (ctor / builder / DI factory)
    /// so misconfigurations surface immediately, not on the first HTTP call.</summary>
    /// <exception cref="InvalidOperationException">Thrown when neither / both auth modes set,
    /// or when required <see cref="BaseUrl"/> / <see cref="TradePlatform"/> are missing.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException("MT4V2ClientOptions: BaseUrl is required.");
        if (TradePlatform == Guid.Empty)
            throw new InvalidOperationException("MT4V2ClientOptions: TradePlatform is required.");

        var hasToken = !string.IsNullOrEmpty(Token);
        var hasCc = !string.IsNullOrEmpty(ClientId)
                    && !string.IsNullOrEmpty(ClientSecret)
                    && !string.IsNullOrEmpty(IdentityUrl);

        if (hasToken && hasCc)
            throw new InvalidOperationException(
                "MT4V2ClientOptions: set EITHER Token OR (ClientId + ClientSecret + IdentityUrl), not both.");
        if (!hasToken && !hasCc)
            throw new InvalidOperationException(
                "MT4V2ClientOptions: set either Token, or all three of (ClientId, ClientSecret, IdentityUrl).");
    }

    /// <summary>Internal helper exposed for the builder/DI to decide which path to wire.
    /// <c>true</c> when a bearer token was provided directly (static-token mode);
    /// <c>false</c> when OAuth2 client_credentials is configured.</summary>
    public bool UsesStaticToken => !string.IsNullOrEmpty(Token);
}

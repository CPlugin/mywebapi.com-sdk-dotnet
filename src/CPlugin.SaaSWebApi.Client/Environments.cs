using System;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Named environment presets for the CPlugin SaaS WebAPI. Presets carry only
/// customer-facing base URLs — no internal hosts or credentials. Client credentials
/// are managed in the CPlugin Toolbox (staging: https://pre.toolbox.cplugin.com,
/// production: https://toolbox.cplugin.com).</summary>
public enum CPluginEnvironment
{
    /// <summary>Production: API <c>https://cloud.mywebapi.com</c>, auth <c>https://auth.cplugin.net</c>.</summary>
    Prod,

    /// <summary>Staging: API <c>https://pre.mywebapi.com</c>, auth <c>https://pre.auth.cplugin.net</c>.</summary>
    Staging,

    /// <summary>User-supplied API base URL and OIDC authority (both required).</summary>
    Custom,
}

/// <summary>Resolves a <see cref="CPluginEnvironment"/> to concrete base URLs.</summary>
public static class CPluginEnvironments
{
    /// <summary>Resolve an environment to <c>(ApiBaseUrl, Authority)</c>.
    /// For <see cref="CPluginEnvironment.Custom"/> both URLs are required and trailing
    /// slashes are stripped; for presets the explicit URLs are ignored.</summary>
    /// <exception cref="InvalidOperationException">Custom environment without both URLs.</exception>
    public static (string ApiBaseUrl, string Authority) Resolve(
        CPluginEnvironment env, string? apiBaseUrl, string? authority)
    {
        switch (env)
        {
            case CPluginEnvironment.Prod:
                return ("https://cloud.mywebapi.com", "https://auth.cplugin.net");
            case CPluginEnvironment.Staging:
                return ("https://pre.mywebapi.com", "https://pre.auth.cplugin.net");
            case CPluginEnvironment.Custom:
                if (string.IsNullOrEmpty(apiBaseUrl) || string.IsNullOrEmpty(authority))
                    throw new InvalidOperationException(
                        "Custom environment requires both ApiBaseUrl and Authority.");
                return (apiBaseUrl!.TrimEnd('/'), authority!.TrimEnd('/'));
            default:
                throw new InvalidOperationException($"Unknown environment: {env}.");
        }
    }
}

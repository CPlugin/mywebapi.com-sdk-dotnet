#if NET8_0_OR_GREATER
using System;
using System.Net.Http;
using CPlugin.SaaSWebApi.Client.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace CPlugin.SaaSWebApi.Client.DependencyInjection;

/// <summary>DI registration for the CPlugin SaaS WebAPI .NET SDK.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register <see cref="CPluginWebApiClient"/>, its OAuth2 client_credentials
    /// handler chain, and a standard resilience pipeline (retries with jitter + circuit
    /// breaker) with the DI container.</summary>
    /// <remarks>
    /// <para>Choose the auth mode in the options: <see cref="CPluginWebApiClientOptions.Token"/>
    /// (static) OR <see cref="CPluginWebApiClientOptions.ClientId"/> +
    /// <see cref="CPluginWebApiClientOptions.ClientSecret"/> (client_credentials). Validation
    /// runs on first resolution and surfaces as <see cref="OptionsValidationException"/>.</para>
    /// <para>Two named HttpClients are registered:
    /// <list type="bullet">
    ///   <item><description><c>CPluginWebApi.Tokens</c> — OIDC discovery + token exchange;
    ///   never recurses through the api handler chain.</description></item>
    ///   <item><description><c>CPluginWebApi</c> — the api-facing client with the auth handler
    ///   and the standard resilience pipeline.</description></item>
    /// </list></para>
    /// <para>Usage:
    /// <code>
    /// services.AddCPluginWebApiSdk(sp => new()
    /// {
    ///     Environment  = CPluginEnvironment.Prod,
    ///     ClientId     = "your-client-id",
    ///     ClientSecret = builder.Configuration["CPlugin:ClientSecret"],
    /// });
    /// </code></para>
    /// </remarks>
    /// <param name="services">DI container.</param>
    /// <param name="optionsFactory">Factory returning a fully-constructed
    /// <see cref="CPluginWebApiClientOptions"/> (init-only record — use object-initializer syntax).</param>
    public static IServiceCollection AddCPluginWebApiSdk(
        this IServiceCollection services,
        Func<IServiceProvider, CPluginWebApiClientOptions> optionsFactory)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (optionsFactory is null) throw new ArgumentNullException(nameof(optionsFactory));

        // * AddOptions<T>() registers the OptionsManager pipeline (IOptions<T>, IOptionsSnapshot<T>,
        // *   IOptionsMonitor<T>) AND wires IValidateOptions<T> instances into Value access.
        services.AddOptions<CPluginWebApiClientOptions>();
        services.AddSingleton<IValidateOptions<CPluginWebApiClientOptions>, OptionsValidator>();

        // * Custom IOptionsFactory that produces options via the caller's delegate and runs the
        // *   validator pipeline manually (replacing the default factory bypasses auto-validation).
        services.Replace(ServiceDescriptor.Singleton<IOptionsFactory<CPluginWebApiClientOptions>>(
            sp => new OptionsFactoryAdapter(
                () => optionsFactory(sp),
                sp.GetServices<IValidateOptions<CPluginWebApiClientOptions>>())));

        // * Dedicated HttpClient for OAuth2 token-endpoint traffic. Separate from the api-facing
        // *   client so token requests never recurse through ClientCredentialsHandler.
        // ! Both named clients here end up captured inside long-lived singletons, which pins
        // !   their handler chains and defeats IHttpClientFactory's handler rotation. DNS
        // !   changes are instead honoured at the connection-pool level via
        // !   SocketsHttpHandler.PooledConnectionLifetime (the recommended pattern for
        // !   long-lived HttpClient instances).
        services.AddHttpClient("CPluginWebApi.Tokens")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });

        services.AddSingleton(_ => new TokenCache(skew: TimeSpan.FromSeconds(60)));

        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CPluginWebApiClientOptions>>().Value;
            var (_, authority) = opts.Validate();
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("CPluginWebApi.Tokens");
            // ! Only meaningful in client_credentials mode; dormant for static tokens.
            return new OidcDiscoveryClient(http, authority);
        });

        services.AddTransient(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CPluginWebApiClientOptions>>().Value;
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("CPluginWebApi.Tokens");
            return new ClientCredentialsHandler(
                sp.GetRequiredService<TokenCache>(),
                sp.GetRequiredService<OidcDiscoveryClient>(),
                http,
                opts.ClientId ?? string.Empty,
                opts.ClientSecret ?? string.Empty,
                opts.Scopes);
        });

        // * Api-facing named client. Auth handler goes in first, resilience wraps everything
        // *   (matches the Microsoft sample for resilience + custom handlers).
        services.AddHttpClient("CPluginWebApi")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // * See the note on "CPluginWebApi.Tokens" — pool-level DNS rotation.
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            })
            .ConfigureHttpClient((sp, http) =>
            {
                var opts = sp.GetRequiredService<IOptions<CPluginWebApiClientOptions>>().Value;
                var (apiBase, _) = opts.Validate();
                http.BaseAddress = new Uri(apiBase + "/");
                http.Timeout = opts.Timeout;
                if (opts.UsesStaticToken)
                {
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.Token);
                }
            })
            .AddHttpMessageHandler(sp =>
            {
                // * The chain shape is fixed at startup, so static-token mode gets a
                // *   pass-through in the auth slot instead of the cc handler.
                var opts = sp.GetRequiredService<IOptions<CPluginWebApiClientOptions>>().Value;
                if (opts.UsesStaticToken)
                    return new PassThroughHandler();
                return sp.GetRequiredService<ClientCredentialsHandler>();
            })
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 3;
                o.Retry.BackoffType = DelayBackoffType.Exponential;
                o.Retry.UseJitter = true;
            });

        // * Singleton entry point: one shared HttpClient (factory-managed handlers rotate
        // *   underneath), one token provider shared by REST and SignalR.
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CPluginWebApiClientOptions>>().Value;
            var (apiBase, authority) = opts.Validate();
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("CPluginWebApi");

            ClientAccessTokenProvider? provider = null;
            if (!opts.UsesStaticToken)
            {
                provider = new ClientAccessTokenProvider(
                    sp.GetRequiredService<TokenCache>(),
                    sp.GetRequiredService<OidcDiscoveryClient>(),
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("CPluginWebApi.Tokens"),
                    opts.ClientId!, opts.ClientSecret!, opts.Scopes);
            }

            return new CPluginWebApiClient(http, apiBase, authority, provider, opts.Token);
        });

        return services;
    }

    /// <summary>No-op DelegatingHandler used in static-token mode so the handler chain
    /// keeps one registration shape regardless of auth mode.</summary>
    private sealed class PassThroughHandler : DelegatingHandler { }

    /// <summary>Produces options from the caller's delegate and runs registered
    /// <see cref="IValidateOptions{TOptions}"/> instances, translating failures into
    /// <see cref="OptionsValidationException"/> (default-factory parity).</summary>
    private sealed class OptionsFactoryAdapter : IOptionsFactory<CPluginWebApiClientOptions>
    {
        private readonly Func<CPluginWebApiClientOptions> _factory;
        private readonly System.Collections.Generic.IEnumerable<IValidateOptions<CPluginWebApiClientOptions>> _validators;

        public OptionsFactoryAdapter(
            Func<CPluginWebApiClientOptions> factory,
            System.Collections.Generic.IEnumerable<IValidateOptions<CPluginWebApiClientOptions>> validators)
        {
            _factory = factory;
            _validators = validators;
        }

        public CPluginWebApiClientOptions Create(string name)
        {
            var options = _factory();
            var failures = new System.Collections.Generic.List<string>();
            foreach (var v in _validators)
            {
                var result = v.Validate(name, options);
                if (result is { Failed: true })
                    failures.AddRange(result.Failures);
            }
            if (failures.Count > 0)
                throw new OptionsValidationException(name, typeof(CPluginWebApiClientOptions), failures);
            return options;
        }
    }

    /// <summary>Bridges <see cref="CPluginWebApiClientOptions.Validate"/> into the Options
    /// framework's <see cref="IValidateOptions{TOptions}"/> contract.</summary>
    private sealed class OptionsValidator : IValidateOptions<CPluginWebApiClientOptions>
    {
        public ValidateOptionsResult Validate(string? name, CPluginWebApiClientOptions options)
        {
            try
            {
                options.Validate();
                return ValidateOptionsResult.Success;
            }
            catch (InvalidOperationException ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }
    }
}
#endif

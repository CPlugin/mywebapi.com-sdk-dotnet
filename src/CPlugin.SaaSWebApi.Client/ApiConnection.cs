using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Internal transport seam shared by all generated endpoint methods:
/// one authenticated <see cref="HttpClient"/>, STJ envelope deserialization,
/// idempotency / sparse-fieldset cross-cutting options.</summary>
internal sealed class ApiConnection
{
    // * Server contract: camelCase JSON; nulls omitted. Case-insensitive read keeps us
    //   robust if server-side naming policy details shift.
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public ApiConnection(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Send a request and deserialize the v2 envelope of type <typeparamref name="TEnv"/>.</summary>
    /// <remarks>
    /// The v2 contract signals endpoint errors in-envelope (HTTP 200 + non-null <c>error</c>),
    /// so this method parses the envelope on <b>any</b> status as long as the body is JSON.
    /// Only non-JSON responses (proxies, dead routes) surface as <see cref="HttpRequestException"/>.
    /// Unwrapping into data / <see cref="ApiError"/> is the caller's job (see <see cref="EnvelopeGuard"/>).
    /// </remarks>
    public async Task<TEnv> SendAsync<TEnv>(
        HttpMethod method, string relativeUrl, object? body, CallOptions? options, CancellationToken ct)
    {
        // * CallOptions.CancellationToken wins over the positional token when set —
        //   generated methods always pass `default` positionally.
        var token = options?.CancellationToken ?? default;
        if (token == default) token = ct;

        var url = ApplyFields(relativeUrl, options);
        using var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(options?.IdempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", options!.IdempotencyKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        var isJson = resp.Content.Headers.ContentType?.MediaType?.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isJson)
        {
            // ! Non-JSON means we never reached the v2 endpoint (proxy error page, wrong route).
            resp.EnsureSuccessStatusCode();
            throw new HttpRequestException(
                $"Expected a JSON v2 envelope from {relativeUrl}, got '{resp.Content.Headers.ContentType?.MediaType}'.");
        }

        using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var env = await JsonSerializer.DeserializeAsync<TEnv>(stream, Json, token).ConfigureAwait(false);
        return env ?? throw new HttpRequestException(
            $"Empty v2 envelope from {relativeUrl} (HTTP {(int)resp.StatusCode}).");
    }

    private static string ApplyFields(string url, CallOptions? options)
    {
        if (options?.Fields is not { Count: > 0 } fields) return url;
        var sep = url.Contains("?") ? '&' : '?';
        return url + sep + "fields=" + Uri.EscapeDataString(string.Join(",", fields));
    }
}

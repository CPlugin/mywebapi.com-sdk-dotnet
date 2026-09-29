using System;
using System.Globalization;
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

    // * How much longer than the server-side deadline the client waits for the answer: the
    //   server may extend a deadline by up to 20 s while it opens the platform connection on
    //   the request's behalf, plus 10 s for the network and the answer itself.
    // ! Giving up before the server answers turns a definite Timeout / OutcomeUnknown envelope
    //   into a client-side cancellation that says nothing about a write — so the client
    //   deadline always outlives the server one.
    internal static readonly TimeSpan ResponseAllowance = TimeSpan.FromSeconds(30);

    internal const string RequestTimeoutHeader = "X-Request-Timeout";
    internal const string AppliedTimeoutHeader = "X-Request-Timeout-Applied";
    internal const string OutcomeHeader = "X-Request-Outcome";

    private readonly HttpClient _http;
    private readonly TimeSpan? _timeout;
    private readonly TimeSpan? _defaultRequestTimeout;

    /// <param name="http">Authenticated HttpClient. Its own <see cref="HttpClient.Timeout"/> still
    /// applies — the SDK's clients set it to infinite and rely on <paramref name="timeout"/>.</param>
    /// <param name="timeout">Minimum client-side wait per request; null — no deadline of our own.</param>
    /// <param name="defaultRequestTimeout">Client-wide server deadline, used when a call sets none.</param>
    public ApiConnection(HttpClient http, TimeSpan? timeout = null, TimeSpan? defaultRequestTimeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout;
        _defaultRequestTimeout = defaultRequestTimeout is { } d
            ? CallOptions.ValidateRequestTimeout(d, nameof(defaultRequestTimeout))
            : null;
    }

    /// <summary>Send a request and deserialize the v2 envelope of type <typeparamref name="TEnv"/>.</summary>
    /// <remarks>
    /// The v2 contract signals endpoint errors in-envelope (HTTP 200 + non-null <c>error</c>),
    /// so this method parses the envelope on <b>any</b> status as long as the body is JSON.
    /// Only non-JSON responses (proxies, dead routes) surface as <see cref="HttpRequestException"/>.
    /// Unwrapping into data / <see cref="ApiError"/> is the caller's job (see <see cref="EnvelopeGuard"/>).
    /// <para><c>defaultRequestTimeoutSeconds</c> is the operation's documented server-side deadline
    /// (from the spec); it sizes the client-side wait when the caller requests no deadline.</para>
    /// </remarks>
    public async Task<ApiResponse<TEnv>> SendAsync<TEnv>(
        HttpMethod method, string relativeUrl, object? body, CallOptions? options, CancellationToken ct,
        double? defaultRequestTimeoutSeconds = null)
    {
        // * CallOptions.CancellationToken wins over the positional token when set —
        //   generated methods always pass `default` positionally.
        var token = options?.CancellationToken ?? default;
        if (token == default) token = ct;

        // * Per-call value beats the client-wide one; the header goes out only when one of them
        //   is set, so the server applies its per-operation default otherwise.
        var requested = options?.RequestTimeout ?? _defaultRequestTimeout;
        var serverDeadline = requested
            ?? (defaultRequestTimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null);

        var url = ApplyFields(relativeUrl, options);
        using var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(options?.IdempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", options!.IdempotencyKey);
        if (requested is { } rt)
            req.Headers.TryAddWithoutValidation(RequestTimeoutHeader, FormatSeconds(rt));

        using var deadline = Deadline.Start(ClientTimeout(_timeout, serverDeadline), token);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            var info = new ResponseInfo(
                (int)resp.StatusCode,
                Header(resp, OutcomeHeader),
                ParseSeconds(Header(resp, AppliedTimeoutHeader)));

            var isJson = resp.Content.Headers.ContentType?.MediaType?.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isJson)
            {
                // ! Non-JSON means we never reached the v2 endpoint (proxy error page, wrong route).
                resp.EnsureSuccessStatusCode();
                throw new HttpRequestException(
                    $"Expected a JSON v2 envelope from {relativeUrl}, got '{resp.Content.Headers.ContentType?.MediaType}'.");
            }

#if NET5_0_OR_GREATER
            using var stream = await resp.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
#else
            // * No cancellable overload on netstandard2.0. With ResponseHeadersRead the stream is
            //   already there; the deadline still covers reading it (DeserializeAsync below).
            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            var env = await JsonSerializer.DeserializeAsync<TEnv>(stream, Json, deadline.Token).ConfigureAwait(false);
            if (env is null)
                throw new HttpRequestException(
                    $"Empty v2 envelope from {relativeUrl} (HTTP {(int)resp.StatusCode}).");
            return new ApiResponse<TEnv>(env, info);
        }
        catch (OperationCanceledException ex) when (deadline.Expired && !token.IsCancellationRequested)
        {
            throw deadline.TimeoutException(method, relativeUrl, ex);
        }
    }

    /// <summary>Client-side wait for one request: at least <paramref name="timeout"/>, and always
    /// longer than the server-side deadline by <see cref="ResponseAllowance"/>.</summary>
    internal static TimeSpan? ClientTimeout(TimeSpan? timeout, TimeSpan? serverDeadline)
    {
        if (timeout is null || timeout == System.Threading.Timeout.InfiniteTimeSpan) return timeout;
        if (serverDeadline is not { } sd) return timeout;
        var needed = sd + ResponseAllowance;
        return needed > timeout.Value ? needed : timeout;
    }

    internal static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static TimeSpan? ParseSeconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 && s < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(s)
            : null;

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static string ApplyFields(string url, CallOptions? options)
    {
        if (options?.Fields is not { Count: > 0 } fields) return url;
        var sep = url.Contains("?") ? '&' : '?';
        return url + sep + "fields=" + Uri.EscapeDataString(string.Join(",", fields));
    }
}
/// <summary>Deserialized v2 envelope together with the response metadata the envelope does not carry.</summary>
internal readonly struct ApiResponse<TEnv>
{
    public ApiResponse(TEnv envelope, ResponseInfo info)
    {
        Envelope = envelope;
        Info = info;
    }

    public TEnv Envelope { get; }
    public ResponseInfo Info { get; }
    public int StatusCode => Info.StatusCode;
}

/// <summary>HTTP status and the timeout-related response headers of one v2 call.</summary>
internal readonly struct ResponseInfo
{
    public ResponseInfo(int statusCode, string? outcome = null, TimeSpan? appliedRequestTimeout = null)
    {
        StatusCode = statusCode;
        Outcome = outcome;
        AppliedRequestTimeout = appliedRequestTimeout;
    }

    public int StatusCode { get; }
    /// <summary><c>X-Request-Outcome</c>, when the server set it.</summary>
    public string? Outcome { get; }
    /// <summary><c>X-Request-Timeout-Applied</c>, when the server set it.</summary>
    public TimeSpan? AppliedRequestTimeout { get; }
}

/// <summary>Client-side deadline for one request, linked to the caller's token.</summary>
internal sealed class Deadline : IDisposable
{
    private readonly CancellationTokenSource? _cts;
    private readonly TimeSpan _after;

    private Deadline(CancellationTokenSource? cts, TimeSpan after, CancellationToken token)
    {
        _cts = cts;
        _after = after;
        Token = cts?.Token ?? token;
    }

    public static Deadline Start(TimeSpan? after, CancellationToken token)
    {
        if (after is not { } a || a == Timeout.InfiniteTimeSpan) return new Deadline(null, default, token);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(a);
        return new Deadline(cts, a, token);
    }

    public CancellationToken Token { get; }

    /// <summary>True when the deadline itself (not the caller) cancelled the request.</summary>
    public bool Expired => _cts is { IsCancellationRequested: true };

    /// <summary>Same exception type <see cref="HttpClient.Timeout"/> raises (a
    /// <see cref="TaskCanceledException"/> wrapping a <see cref="System.TimeoutException"/>),
    /// so existing handlers keep working.</summary>
    public TaskCanceledException TimeoutException(HttpMethod method, string url, Exception inner)
    {
        var message = string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1}: no answer within {2} s. For a change or a trade the outcome is unknown; "
            + "repeat it only with the same Idempotency-Key.",
            method.Method, url, ApiConnection.FormatSeconds(_after));
        return new TaskCanceledException(message, new System.TimeoutException(message, inner));
    }

    public void Dispose() => _cts?.Dispose();
}

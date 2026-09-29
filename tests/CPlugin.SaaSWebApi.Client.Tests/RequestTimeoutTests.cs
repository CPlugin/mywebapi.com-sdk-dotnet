using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPlugin.SaaSWebApi.Client;
using CPlugin.SaaSWebApi.Client.DependencyInjection;
using CPlugin.SaaSWebApi.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

// * Request timeouts: the X-Request-Timeout header, the client-side deadline that must outlive
//   the server-side one, the Timeout / OutcomeUnknown / Busy codes with X-Request-Outcome, and
//   the retry policy around them. No network — everything goes through StubHandler.
public class RequestTimeoutTests
{
    private static readonly Guid Platform = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond =
            (_, _) => Task.FromResult(Json("""{"data":true}"""));
        public HttpRequestMessage? LastRequest;
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            LastRequest = r;
            Interlocked.Increment(ref Calls);
            return Respond(r, ct);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ErrorEnvelope(string code, string? outcome, string? applied = null)
    {
        var resp = Json($$$"""{"data":null,"error":{"code":"{{{code}}}","message":"m"},"meta":{"activityId":"trace-t"}}""");
        if (outcome is not null) resp.Headers.TryAddWithoutValidation("X-Request-Outcome", outcome);
        if (applied is not null) resp.Headers.TryAddWithoutValidation("X-Request-Timeout-Applied", applied);
        return resp;
    }

    private static ApiConnection Conn(StubHandler h, TimeSpan? timeout = null, TimeSpan? defaultRequestTimeout = null) =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://x"), Timeout = Timeout.InfiniteTimeSpan },
            timeout, defaultRequestTimeout);

    private static string? SentTimeout(StubHandler h) =>
        h.LastRequest!.Headers.TryGetValues("X-Request-Timeout", out var v) ? v.Single() : null;

    // --- header ----------------------------------------------------------------

    [Fact]
    public async Task No_header_when_no_timeout_is_requested()
    {
        var h = new StubHandler { Respond = (_, _) => Task.FromResult(Json("""{"data":"2026-09-29T10:00:00Z"}""")) };
        await new MT4Endpoints(Conn(h), Platform).ServerTimeAsync();
        Assert.Null(SentTimeout(h));
    }

    [Fact]
    public async Task Call_timeout_is_sent_in_invariant_seconds()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma must not leak
        try
        {
            var h = new StubHandler { Respond = (_, _) => Task.FromResult(Json("""{"data":"2026-09-29T10:00:00Z"}""")) };
            await new MT4Endpoints(Conn(h), Platform).ServerTimeAsync(
                new CallOptions { RequestTimeout = TimeSpan.FromMilliseconds(2500) });
            Assert.Equal("2.5", SentTimeout(h));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Client_default_applies_and_call_value_overrides_it()
    {
        var h = new StubHandler();
        var conn = Conn(h, defaultRequestTimeout: TimeSpan.FromSeconds(20));

        await conn.SendAsync<BooleanApiResponse>(HttpMethod.Post, "api/v2/MT4/g/Thing", null, null, default);
        Assert.Equal("20", SentTimeout(h));

        await conn.SendAsync<BooleanApiResponse>(HttpMethod.Post, "api/v2/MT4/g/Thing", null,
            new CallOptions { RequestTimeout = TimeSpan.FromSeconds(45) }, default);
        Assert.Equal("45", SentTimeout(h));
    }

    // --- validation --------------------------------------------------------------

    [Theory]
    [InlineData(0.5)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(300.5)]
    public void Call_timeout_outside_1_to_300_seconds_is_rejected(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CallOptions { RequestTimeout = TimeSpan.FromSeconds(seconds) });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Call_timeout_bounds_are_inclusive(double seconds)
    {
        var o = new CallOptions { RequestTimeout = TimeSpan.FromSeconds(seconds) };
        Assert.Equal(TimeSpan.FromSeconds(seconds), o.RequestTimeout);
    }

    [Fact]
    public void Client_options_reject_out_of_range_timeouts()
    {
        var baseOptions = new CPluginWebApiClientOptions { Token = "t" };
        Assert.Throws<InvalidOperationException>(() =>
            (baseOptions with { RequestTimeout = TimeSpan.FromSeconds(301) }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (baseOptions with { RequestTimeout = TimeSpan.Zero }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (baseOptions with { Timeout = TimeSpan.Zero }).Validate());
        (baseOptions with { Timeout = Timeout.InfiniteTimeSpan, RequestTimeout = TimeSpan.FromSeconds(60) }).Validate();
    }

    // --- client-side deadline ------------------------------------------------------

    [Theory]
    [InlineData(30, null, 30)]   // no server deadline known: the configured minimum
    [InlineData(30, 10.0, 40)]   // read default: 10 s + 30 s allowance
    [InlineData(30, 300.0, 330)] // longest server deadline
    [InlineData(120, 5.0, 120)]  // configured minimum is longer already
    public void Client_deadline_outlives_the_server_deadline(double timeout, double? server, double expected)
    {
        var actual = ApiConnection.ClientTimeout(
            TimeSpan.FromSeconds(timeout), server is { } s ? TimeSpan.FromSeconds(s) : null);
        Assert.Equal(TimeSpan.FromSeconds(expected), actual);
    }

    [Fact]
    public void Infinite_timeout_stays_infinite()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan,
            ApiConnection.ClientTimeout(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Requested_server_deadline_extends_a_shorter_client_timeout()
    {
        // * Timeout 100 ms alone would cancel this 300 ms answer; the requested 1 s server
        //   deadline stretches the client-side wait to 31 s.
        var h = new StubHandler
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(300, ct);
                return Json("""{"data":true}""");
            },
        };
        var result = await Conn(h, timeout: TimeSpan.FromMilliseconds(100)).SendAsync<BooleanApiResponse>(
            HttpMethod.Post, "api/v2/MT4/g/Thing", null,
            new CallOptions { RequestTimeout = TimeSpan.FromSeconds(1) }, default);
        Assert.True(result.Envelope.Data);
    }

    [Fact]
    public async Task Client_deadline_expiry_throws_TaskCanceled_wrapping_TimeoutException()
    {
        var h = new StubHandler
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Json("{}");
            },
        };
        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Conn(h, timeout: TimeSpan.FromMilliseconds(150)).SendAsync<BooleanApiResponse>(
                HttpMethod.Post, "api/v2/MT4/g/Thing", null, null, default));
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Contains("Idempotency-Key", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        var h = new StubHandler
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Json("{}");
            },
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Conn(h, timeout: TimeSpan.FromSeconds(30)).SendAsync<BooleanApiResponse>(
                HttpMethod.Post, "api/v2/MT4/g/Thing", null, new CallOptions { CancellationToken = cts.Token }, default));
        Assert.IsNotType<TimeoutException>(ex.InnerException);
    }

    // --- error codes and outcome ---------------------------------------------------

    [Fact]
    public async Task Read_timeout_is_safe_to_retry()
    {
        var h = new StubHandler { Respond = (_, _) => Task.FromResult(ErrorEnvelope("Timeout", "timeout", "10")) };
        var ex = await Assert.ThrowsAsync<ApiError>(() => new MT4Endpoints(Conn(h), Platform).ServerTimeAsync());
        Assert.Equal(ApiErrorCodes.Timeout, ex.Code);
        Assert.Equal(RequestOutcomes.Timeout, ex.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(10), ex.AppliedRequestTimeout);
        Assert.True(ex.IsTimeout);
        Assert.True(ex.IsSafeToRetry);
        Assert.False(ex.IsOutcomeUnknown);
        Assert.Equal("trace-t", ex.ActivityId);
    }

    [Fact]
    public async Task Unknown_write_outcome_is_not_safe_to_retry()
    {
        var h = new StubHandler { Respond = (_, _) => Task.FromResult(ErrorEnvelope("OutcomeUnknown", "unknown", "15")) };
        var ex = await Assert.ThrowsAsync<ApiError>(() =>
            new MT4Endpoints(Conn(h), Platform).CfgDeleteAccessAsync(7,
                new CallOptions { IdempotencyKey = "k-1" }));
        Assert.True(ex.IsOutcomeUnknown);
        Assert.False(ex.IsInProgress);
        Assert.False(ex.IsSafeToRetry);
        Assert.Equal(RequestOutcomes.Unknown, ex.Outcome);
    }

    [Fact]
    public async Task Repeat_of_a_running_idempotency_key_is_in_progress()
    {
        var h = new StubHandler { Respond = (_, _) => Task.FromResult(ErrorEnvelope("OutcomeUnknown", "in-progress")) };
        var ex = await Assert.ThrowsAsync<ApiError>(async () =>
        {
            var result = await Conn(h).SendAsync<BooleanApiResponse>(
                HttpMethod.Post, "api/v2/MT4/g/Thing", null, new CallOptions { IdempotencyKey = "k-1" }, default);
            var env = result.Envelope;
            EnvelopeGuard.Unwrap(env.Data, env.Error, env.Meta, result.Info);
        });
        Assert.True(ex.IsOutcomeUnknown);
        Assert.True(ex.IsInProgress);
        Assert.False(ex.IsSafeToRetry);
    }

    [Fact]
    public async Task Busy_refusal_is_safe_to_retry()
    {
        var h = new StubHandler { Respond = (_, _) => Task.FromResult(ErrorEnvelope("Busy", "not-started")) };
        var ex = await Assert.ThrowsAsync<ApiError>(() =>
            new MT4Endpoints(Conn(h), Platform).CfgDeleteAccessAsync(7));
        Assert.True(ex.IsBusy);
        Assert.True(ex.IsSafeToRetry);
        Assert.Equal(RequestOutcomes.NotStarted, ex.Outcome);
    }

    [Theory]
    [InlineData("NotFound", null)]
    [InlineData("Validation", null)]
    [InlineData("Internal", null)]
    [InlineData("Timeout", "unknown")] // contradictory answers err on the safe side
    [InlineData("Timeout", "in-progress")]
    [InlineData("Busy", "unknown")]
    [InlineData("Timeout", "some-future-value")]
    [InlineData("NotFound", "not-started")]
    [InlineData("OutcomeUnknown", "timeout")]
    public void Other_errors_are_not_safe_to_retry(string code, string? outcome)
    {
        var ex = new ApiError(code, null, null, null, 200, outcome, null);
        Assert.False(ex.IsSafeToRetry);
    }

    [Theory]
    [InlineData("Timeout", null)]
    [InlineData("Timeout", "TIMEOUT")]
    [InlineData("Busy", null)]
    [InlineData("Busy", "not-started")]
    public void Consistent_safe_answers_are_safe_to_retry(string code, string? outcome)
    {
        Assert.True(new ApiError(code, null, null, null, 200, outcome, null).IsSafeToRetry);
    }

    [Fact]
    public void Legacy_constructor_leaves_outcome_empty()
    {
        var ex = new ApiError("NotFound", "d", "a", null, 200);
        Assert.Null(ex.Outcome);
        Assert.Null(ex.AppliedRequestTimeout);
    }

    [Fact]
    public void New_codes_round_trip_through_the_envelope_enum()
    {
        foreach (var name in new[] { "Timeout", "OutcomeUnknown", "Busy" })
            Assert.True(Enum.TryParse<WebApiErrorCode>(name, out _), name);
    }

    // --- generated surface -------------------------------------------------------

    [Fact]
    public async Task Generated_calls_carry_the_operation_default_deadline()
    {
        // * ServerTime is a read (10 s by default): with Timeout 100 ms the client still waits
        //   40 s, so a 300 ms answer arrives.
        var h = new StubHandler
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(300, ct);
                return Json("""{"data":"2026-09-29T10:00:00Z"}""");
            },
        };
        var time = await new MT4Endpoints(Conn(h, timeout: TimeSpan.FromMilliseconds(100)), Platform).ServerTimeAsync();
        Assert.Equal(2026, time.Year);
        Assert.Null(SentTimeout(h));
    }

    [Fact]
    public async Task Operations_without_a_server_deadline_get_a_60_s_basis()
    {
        // * UsersSnapshot is not guarded by the server (no X-Request-Timeout in the spec); the client
        //   still waits 60 s + 30 s rather than its 100 ms minimum.
        var h = new StubHandler
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(300, ct);
                return Json("""{"data":null}""");
            },
        };
        await new MT4Endpoints(Conn(h, timeout: TimeSpan.FromMilliseconds(100)), Platform).UsersSnapshotAsync();
        Assert.Null(SentTimeout(h));
    }

    [Fact]
    public async Task Patch_methods_send_the_patch_object()
    {
        string? body = null;
        var h = new StubHandler
        {
            Respond = async (r, _) =>
            {
                body = await r.Content!.ReadAsStringAsync();
                return Json("""{"data":{"group":"g1"}}""");
            },
        };
        await new MT4Endpoints(Conn(h), Platform).GroupRecordAsync("g1", new { Leverage = 100 });
        Assert.Equal("PATCH", h.LastRequest!.Method.Method);
        Assert.Equal("{\"leverage\":100}", body);
    }

    // --- retry policy (DI pipeline) ------------------------------------------------

    private static (CPluginWebApiClient Client, ServiceProvider Provider) DiClient(StubHandler h, TimeSpan timeout)
    {
        var services = new ServiceCollection();
        services.AddCPluginWebApiSdk(_ => new CPluginWebApiClientOptions
        {
            Environment = CPluginEnvironment.Custom,
            ApiBaseUrl = "https://api.local",
            Authority = "https://auth.local",
            Token = "static-token",
            Timeout = timeout,
        });
        services.AddHttpClient("CPluginWebApi").ConfigurePrimaryHttpMessageHandler(() => h);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<CPluginWebApiClient>(), sp);
    }

    [Fact]
    public async Task Di_pipeline_never_retries_a_write_on_a_transient_status()
    {
        var h = new StubHandler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
        };
        var (client, sp) = DiClient(h, TimeSpan.FromSeconds(30));
        using (sp)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.MT4(Platform).CfgDeleteAccessAsync(7, new CallOptions { IdempotencyKey = "k" }));
            Assert.Equal(1, h.Calls);
        }
    }

    [Fact]
    public async Task Di_pipeline_never_retries_a_write_on_a_transport_fault()
    {
        var h = new StubHandler { Respond = (_, _) => throw new HttpRequestException("reset") };
        var (client, sp) = DiClient(h, TimeSpan.FromSeconds(30));
        using (sp)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.MT4(Platform).CfgDeleteAccessAsync(7));
            Assert.Equal(1, h.Calls);
        }
    }

    [Fact]
    public async Task Di_pipeline_does_not_retry_a_read_whose_key_is_in_progress()
    {
        var h = new StubHandler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                r.Headers.TryAddWithoutValidation("X-Request-Outcome", "in-progress");
                return Task.FromResult(r);
            },
        };
        var (client, sp) = DiClient(h, TimeSpan.FromSeconds(30));
        using (sp)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.MT4(Platform).ServerTimeAsync());
            Assert.Equal(1, h.Calls);
        }
    }

    [Fact]
    public async Task Di_pipeline_lets_a_long_server_deadline_outlive_the_client_timeout()
    {
        // * The standard pipeline would cut this at its 10 s attempt / 30 s total defaults and the
        //   HttpClient at Timeout; here Timeout is 100 ms and the answer takes 300 ms.
        var h = new StubHandler
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(300, ct);
                return Json("""{"data":true}""");
            },
        };
        var (client, sp) = DiClient(h, TimeSpan.FromMilliseconds(100));
        using (sp)
        {
            var ok = await client.MT4(Platform).CfgDeleteAccessAsync(7,
                new CallOptions { RequestTimeout = TimeSpan.FromSeconds(1) });
            Assert.True(ok);
            Assert.Equal("1", SentTimeout(h));
        }
    }

    [Fact]
    public void Resilience_attempt_timeout_covers_the_longest_server_deadline()
    {
        Assert.True(ServiceCollectionExtensions.MaxAttemptTimeout(TimeSpan.FromSeconds(30))
            > CallOptions.MaxRequestTimeout + ApiConnection.ResponseAllowance);
        Assert.True(ServiceCollectionExtensions.MaxAttemptTimeout(TimeSpan.FromMinutes(20))
            > TimeSpan.FromMinutes(20));
    }
}

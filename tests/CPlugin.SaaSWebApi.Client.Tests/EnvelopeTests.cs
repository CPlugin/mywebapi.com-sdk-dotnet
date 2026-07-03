using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPlugin.SaaSWebApi.Client;
using CPlugin.SaaSWebApi.Models;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

// * Contract tests for the envelope transport core: ApiConnection (HTTP + STJ),
//   EnvelopeGuard (unwrap / ApiError), Page/PageIterator (cursor walking).
//   No network — everything goes through StubHandler.
public class EnvelopeTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            LastRequest = r;
            // * Capture the body eagerly — the connection disposes the request after sending.
            LastRequestBody = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return Respond(r);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ApiConnection Conn(StubHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://x") });

    [Fact]
    public async Task Success_envelope_returns_data()
    {
        var h = new StubHandler
        {
            Respond = _ => Json("""{"data":"2026-07-02T10:00:00Z","meta":{"activityId":"abc"}}"""),
        };
        var env = await Conn(h).SendAsync<DateTimeApiResponse>(
            HttpMethod.Get, "api/v2/MT4/g/ServerTime", null, null, default);
        var data = EnvelopeGuard.Unwrap(env.Data, env.Error, env.Meta, 200);
        Assert.NotNull(data);
        Assert.Equal(2026, data!.Value.Year);
    }

    [Fact]
    public async Task Error_envelope_throws_ApiError_with_activity_id()
    {
        var h = new StubHandler
        {
            Respond = _ => Json("""{"data":null,"error":{"code":"NotFound","message":"no such user"},"meta":{"activityId":"trace-1"}}"""),
        };
        var env = await Conn(h).SendAsync<MT4UserApiResponse>(
            HttpMethod.Get, "api/v2/MT4/g/UserRecord/42", null, null, default);
        var ex = Assert.Throws<ApiError>(() => EnvelopeGuard.Unwrap(env.Data, env.Error, env.Meta, 200));
        Assert.Equal("NotFound", ex.Code);
        Assert.Equal("no such user", ex.Description);
        Assert.Equal("trace-1", ex.ActivityId);
        Assert.Equal(200, ex.Status);
    }

    [Fact]
    public async Task Paged_envelope_yields_page_with_cursor()
    {
        var h = new StubHandler
        {
            Respond = _ => Json("""{"data":[{"login":1},{"login":2}],"meta":{"paging":{"nextCursor":"c2","hasMore":true}}}"""),
        };
        var env = await Conn(h).SendAsync<MT4UserListApiResponse>(
            HttpMethod.Get, "api/v2/MT4/g/UsersRequest", null, null, default);
        var page = EnvelopeGuard.UnwrapPage(env.Data, env.Error, env.Meta, 200);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Equal("c2", page.NextCursor);
    }

    [Fact]
    public async Task Last_page_has_no_cursor()
    {
        var h = new StubHandler
        {
            Respond = _ => Json("""{"data":[{"login":3}],"meta":{"paging":{"nextCursor":null,"hasMore":false}}}"""),
        };
        var env = await Conn(h).SendAsync<MT4UserListApiResponse>(
            HttpMethod.Get, "api/v2/MT4/g/UsersRequest", null, null, default);
        var page = EnvelopeGuard.UnwrapPage(env.Data, env.Error, env.Meta, 200);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Idempotency_key_and_fields_are_applied()
    {
        var h = new StubHandler { Respond = _ => Json("""{"data":true}""") };
        var opts = new CallOptions { IdempotencyKey = "k-1", Fields = new[] { "login", "balance" } };
        await Conn(h).SendAsync<BooleanApiResponse>(
            HttpMethod.Post, "api/v2/MT4/g/Thing", new { a = 1 }, opts, default);
        Assert.Equal("k-1", h.LastRequest!.Headers.GetValues("Idempotency-Key").Single());
        Assert.Contains("fields=login%2Cbalance", h.LastRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task Body_is_serialized_as_json()
    {
        var h = new StubHandler { Respond = _ => Json("""{"data":true}""") };
        await Conn(h).SendAsync<BooleanApiResponse>(
            HttpMethod.Post, "api/v2/MT4/g/Thing", new { Login = 42 }, null, default);
        // * camelCase policy applies to outbound bodies.
        Assert.Contains("\"login\":42", h.LastRequestBody);
    }

    [Fact]
    public async Task Non_json_response_throws_HttpRequestException()
    {
        var h = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            { Content = new StringContent("<html>gateway</html>", Encoding.UTF8, "text/html") },
        };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Conn(h).SendAsync<BooleanApiResponse>(HttpMethod.Get, "api/v2/MT4/g/Thing", null, null, default));
    }

    [Fact]
    public async Task PageIterator_walks_until_hasMore_false()
    {
        var cursors = new List<string?>();
        var pages = new Queue<Page<int>>(new[]
        {
            new Page<int>(new[] { 1, 2 }, "c2", true, null),
            new Page<int>(new[] { 3 }, null, false, null),
        });
        var all = new List<int>();
        await foreach (var item in PageIterator.ItemsAsync<int>(cur =>
        {
            cursors.Add(cur);
            return Task.FromResult(pages.Dequeue());
        }))
        {
            all.Add(item);
        }

        Assert.Equal(new[] { 1, 2, 3 }, all);
        Assert.Equal(new string?[] { null, "c2" }, cursors);
    }
}

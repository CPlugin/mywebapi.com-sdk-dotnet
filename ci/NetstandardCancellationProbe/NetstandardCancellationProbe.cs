using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPlugin.SaaSWebApi.Client.Auth;

var targetFramework = typeof(ClientCredentialsHandler).Assembly
    .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
    .OfType<TargetFrameworkAttribute>()
    .SingleOrDefault()?.FrameworkName;
if (!string.Equals(targetFramework, ".NETStandard,Version=v2.0", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"probe loaded unexpected SDK target: {targetFramework}");

using var callerCancellation = new CancellationTokenSource();
var tokenCalls = 0;
using var tokenHttp = new HttpClient(new RoutingHandler(request =>
{
    var path = request.RequestUri!.AbsolutePath;
    if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, "{\"issuer\":\"https://idp.local\",\"jwks_uri\":\"https://idp.local/.well-known/openid-configuration/jwks\",\"authorization_endpoint\":\"https://idp.local/connect/authorize\",\"token_endpoint\":\"https://idp.local/connect/token\",\"userinfo_endpoint\":\"https://idp.local/connect/userinfo\"}");
    if (path.EndsWith("/jwks", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, "{\"keys\":[]}");
    if (path.EndsWith("/connect/token", StringComparison.Ordinal))
    {
        var call = Interlocked.Increment(ref tokenCalls);
        return Json(HttpStatusCode.OK, $"{{\"access_token\":\"token-{call}\",\"token_type\":\"Bearer\",\"expires_in\":600}}");
    }
    return new HttpResponseMessage(HttpStatusCode.NotFound);
}));

var apiHandler = new AlwaysUnauthorizedHandler();
var handler = new ClientCredentialsHandler(
    new TokenCache(),
    new OidcDiscoveryClient(tokenHttp, "https://idp.local"),
    tokenHttp,
    "client",
    "secret")
{
    InnerHandler = apiHandler,
};
using var client = new HttpClient(handler)
{
    BaseAddress = new Uri("https://api.local"),
};
using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/echo")
{
    // The public SendAsync path must honor cancellation while replaying this body
    // after the first 401. The body stream intentionally never becomes available.
    Content = new BlockingContent(callerCancellation),
};

var sendTask = client.SendAsync(request, callerCancellation.Token);
var completed = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(2)));
if (completed != sendTask)
    throw new InvalidOperationException("public ClientCredentialsHandler.SendAsync ignored cancellation during netstandard body replay");

try
{
    using var response = await sendTask;
    throw new InvalidOperationException("request unexpectedly completed after a permanently blocked body stream");
}
catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
{
    if (Volatile.Read(ref tokenCalls) != 2 || apiHandler.Requests != 1)
        throw new InvalidOperationException($"probe did not reach the intended 401 replay path: tokens={tokenCalls}, api={apiHandler.Requests}");
    Console.WriteLine("PASS: public netstandard2.0 request path honors body-replay cancellation");
}

static HttpResponseMessage Json(HttpStatusCode status, string body) =>
    new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

sealed class RoutingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
    public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_route(request));
}

sealed class AlwaysUnauthorizedHandler : HttpMessageHandler
{
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}

sealed class BlockingContent : HttpContent
{
    private readonly CancellationTokenSource _callerCancellation;
    public BlockingContent(CancellationTokenSource callerCancellation) => _callerCancellation = callerCancellation;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
    protected override bool TryComputeLength(out long length) { length = 0; return true; }
    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            _callerCancellation.Cancel();
        });
        return new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }
}

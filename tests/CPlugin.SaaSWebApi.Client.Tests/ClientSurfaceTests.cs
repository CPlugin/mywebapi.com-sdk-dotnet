using System;
using System.Linq;
using System.Reflection;
using CPlugin.SaaSWebApi.Client;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

// * Shape assertions over the generated facade: coverage count, pagination typing,
//   XML-doc-bearing public surface. Guards against silent generator regressions.
public class ClientSurfaceTests
{
    private static int CountEndpointMethods(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(m => m.Name.EndsWith("Async", StringComparison.Ordinal));

    [Fact]
    public void Generated_surface_covers_all_spec_operations()
    {
        // ! 456 operations in clients/spec/v2.json (440 from the live v2 document plus the 13
        // ! x86-sidecar MT4 operations it does not compile) — update alongside spec regeneration.
        Assert.Equal(456, CountEndpointMethods(typeof(MT4Endpoints)) + CountEndpointMethods(typeof(MT5Endpoints)));
    }

    [Fact]
    public void Paged_endpoints_return_Page()
    {
        var m = typeof(MT4Endpoints).GetMethod("UsersRequestAsync");
        Assert.NotNull(m);
        var inner = m!.ReturnType.GetGenericArguments().Single(); // Task<T> -> T
        Assert.True(inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(Page<>));
    }

    [Fact]
    public void MT5_surface_exposes_trade_reads()
    {
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("PositionRequestByGroupAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("DealRequestByGroupAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("TimeServerAsync"));
        // * GroupGet answers from the cache next to GroupRequest (both are part of the surface).
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("GroupGetAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("GroupRequestAsync"));
    }

    [Fact]
    public void MT5_surface_exposes_the_subscription_group()
    {
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("SubscriptionRequestByIDAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("SubscriptionHistoryRequestByIDAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("SubscriptionJoinAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("SubscriptionUpdateAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("SubscriptionCancelAsync"));
    }

    [Fact]
    public void MT5_surface_exposes_the_client_group()
    {
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("ClientRequestAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("ClientAddAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("ClientUpdateAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("CommentAddAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("DocumentAddAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("AttachmentAddAsync"));
    }

    [Fact]
    public void MT5_surface_exposes_reports_and_trade_operations()
    {
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("DailyRequestAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("SummaryGetAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("ExposureGetAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("TradeProfitAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("TradeMarginCheckExtAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("TradeRateBuyAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("TradeAccountSetAsync"));
    }

    [Fact]
    public void MT4_surface_exposes_the_summary_and_exposure_group()
    {
        Assert.NotNull(typeof(MT4Endpoints).GetMethod("SummaryGetAllAsync"));
        Assert.NotNull(typeof(MT4Endpoints).GetMethod("SummaryGetByTypeAsync"));
        Assert.NotNull(typeof(MT4Endpoints).GetMethod("ExposureGetAsync"));
        Assert.NotNull(typeof(MT4Endpoints).GetMethod("ExposureValueGetAsync"));
        Assert.NotNull(typeof(MT4Endpoints).GetMethod("TradeCalcProfitAsync"));
        Assert.NotNull(typeof(MT4Endpoints).GetMethod("LogsModeAsync"));
        Assert.NotNull(typeof(MT4Endpoints).GetMethod("LogsOutAsync"));
    }

    [Fact]
    public void Every_endpoint_method_accepts_CallOptions_last()
    {
        foreach (var t in new[] { typeof(MT4Endpoints), typeof(MT5Endpoints) })
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name.EndsWith("Async", StringComparison.Ordinal)))
            {
                var last = m.GetParameters().LastOrDefault();
                Assert.True(last is not null && last.ParameterType == typeof(CallOptions),
                    $"{t.Name}.{m.Name} must take CallOptions as its last parameter");
            }
        }
    }
}

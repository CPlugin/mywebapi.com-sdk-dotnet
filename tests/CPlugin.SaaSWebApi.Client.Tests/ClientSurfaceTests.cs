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
        // ! 172 operations in clients/spec/v2.json — update alongside spec regeneration.
        Assert.Equal(172, CountEndpointMethods(typeof(MT4Endpoints)) + CountEndpointMethods(typeof(MT5Endpoints)));
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
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("PositionByGroupAsync"));
        Assert.NotNull(typeof(MT5Endpoints).GetMethod("ServerTimeAsync"));
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

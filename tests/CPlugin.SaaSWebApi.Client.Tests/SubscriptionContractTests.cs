using System.Text.Json;
using CPlugin.SaaSWebApi.Models;
using Xunit;

namespace CPlugin.SaaSWebApi.Client.Tests;

// * The subscription schemas travel as names, not numbers: the enum members and the
//   flags string are the wire contract, so a generator or spec regression shows up here.
public class SubscriptionContractTests
{
    [Fact]
    public void Subscription_enums_travel_by_name()
    {
        var json = JsonSerializer.Serialize(new MT5Subscription
        {
            Status = MT5SubscriptionStatus.Suspended,
            Flags = "FreePeriod",
        });
        Assert.Contains("\"status\":\"Suspended\"", json);
        Assert.Contains("\"flags\":\"FreePeriod\"", json);

        var back = JsonSerializer.Deserialize<MT5Subscription>(
            """{"status":"Canceled","flags":"None"}""");
        Assert.Equal(MT5SubscriptionStatus.Canceled, back!.Status);
        Assert.Equal("None", back.Flags);
    }

    [Fact]
    public void Subscription_history_and_configuration_enums_travel_by_name()
    {
        var history = JsonSerializer.Deserialize<MT5SubscriptionHistory>("""{"action":"Deleted"}""");
        Assert.Equal(MT5SubscriptionAction.Deleted, history!.Action);

        var config = JsonSerializer.Deserialize<MT5ConSubscription>(
            """{"type":"Folder","controlMode":"View","periodMode":"Monthly","periodFreeMode":"Custom","flags":"Enable, AutoProlong"}""");
        Assert.Equal(MT5SubscriptionType.Folder, config!.Type);
        Assert.Equal(MT5SubscriptionControlMode.View, config.ControlMode);
        Assert.Equal(MT5SubscriptionPeriod.Monthly, config.PeriodMode);
        Assert.Equal(MT5SubscriptionFreePeriod.Custom, config.PeriodFreeMode);
        Assert.Equal("Enable, AutoProlong", config.Flags);

        var symbol = JsonSerializer.Deserialize<MT5ConSubscriptionSymbol>(
            """{"level":"RealtimeLevel2","tickHistory":"LastYear"}""");
        Assert.Equal(MT5SubscriptionSymbolLevel.RealtimeLevel2, symbol!.Level);
        Assert.Equal(MT5SubscriptionTickHistory.LastYear, symbol.TickHistory);
    }

    [Fact]
    public void Tick_statistics_carry_the_option_greeks()
    {
        var stat = JsonSerializer.Deserialize<MT5MarketTickStat>(
            """{"symbol":"EURUSD","priceGreeksDelta":0.51,"priceGreeksTheta":-0.02,"priceGreeksGamma":0.01,"priceGreeksVega":0.12,"priceGreeksRho":0.03,"priceGreeksOmega":0.44,"priceSensitivity":0.87}""");

        Assert.Equal(0.51, stat!.PriceGreeksDelta);
        Assert.Equal(-0.02, stat.PriceGreeksTheta);
        Assert.Equal(0.01, stat.PriceGreeksGamma);
        Assert.Equal(0.12, stat.PriceGreeksVega);
        Assert.Equal(0.03, stat.PriceGreeksRho);
        Assert.Equal(0.44, stat.PriceGreeksOmega);
        Assert.Equal(0.87, stat.PriceSensitivity);

        var json = JsonSerializer.Serialize(stat);
        Assert.Contains("\"priceGreeksOmega\":0.44", json);
        Assert.Contains("\"priceSensitivity\":0.87", json);
    }

    [Fact]
    public void News_time_is_a_wall_clock_value_of_the_trade_server()
    {
        var news = JsonSerializer.Deserialize<MT5News>("""{"id":7,"subject":"News","time":"2026-11-23T10:15:00"}""");
        Assert.Equal("2026-11-23T10:15:00", news!.Time!.Value.DateTime.ToString("yyyy-MM-ddTHH:mm:ss"));
    }
}

using System.Diagnostics;


using OpenFeature.Constant;

namespace Bunting.Sdk.Tests;

public class ResilienceTests
{
    [Fact]
    public async Task A_published_change_reaches_the_application_in_under_a_second()
    {
        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool("promo", false));
        await server.StartAsync();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server, o => o.PollingInterval = TimeSpan.FromMinutes(5)));
        await Harness.Eventually(() => harness.Provider.ConnectionState == ConnectionState.Streaming);
        Assert.False(await harness.Client.GetBooleanValueAsync("promo", true));

        for (var round = 0; round < 3; round++)
        {
            var target = round % 2 == 0;
            var clock = Stopwatch.StartNew();
            server.Publish(Flags.Bool("promo", target));
            await Harness.Eventually(async () => await harness.Client.GetBooleanValueAsync("promo", !target) == target, TimeSpan.FromSeconds(1));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        }
    }

    [Fact]
    public async Task A_change_raises_ConfigurationChanged()
    {
        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool("promo", false));
        await server.StartAsync();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server));
        await Harness.Eventually(() => harness.Provider.ConnectionState == ConnectionState.Streaming);

        server.Publish(Flags.Bool("promo", true));

        await Harness.Eventually(() => harness.Events.Contains(ProviderEventTypes.ProviderConfigurationChanged));
    }

    [Fact]
    public async Task When_the_server_goes_away_the_last_ruleset_keeps_working_and_the_provider_recovers()
    {
        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool("promo", true));
        await server.StartAsync();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server));
        await Harness.Eventually(() => harness.Provider.ConnectionState == ConnectionState.Streaming);

        await server.StopAsync();

        await Harness.Eventually(() => harness.Events.Contains(ProviderEventTypes.ProviderStale), because: "stale event");
        var details = await harness.Client.GetBooleanDetailsAsync("promo", false);
        Assert.True(details.Value);
        Assert.Equal("STALE", details.Reason);

        // A change made while the SDK cannot reach the server...
        server.Publish(Flags.Bool("promo", false), Flags.Bool("added-later", true));
        await server.StartAsync();

        // ...is picked up automatically once the server is back.
        await Harness.Eventually(async () => !await harness.Client.GetBooleanValueAsync("promo", true), because: "recovered value");
        Assert.True(await harness.Client.GetBooleanValueAsync("added-later", false));
        var recovered = await harness.Client.GetBooleanDetailsAsync("promo", true);
        Assert.NotEqual("STALE", recovered.Reason);
        Assert.Equal(ConnectionState.Streaming, await WaitFor(() => harness.Provider.ConnectionState, ConnectionState.Streaming));
    }

    [Fact]
    public async Task Without_any_ruleset_the_provider_reports_not_ready_and_becomes_ready_when_the_server_appears()
    {
        await using var server = new FakeBuntingServer(); // not started
        server.Publish(Flags.Bool("promo", true));
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server, o => o.InitializeTimeout = TimeSpan.FromMilliseconds(300)));

        var details = await harness.Client.GetBooleanDetailsAsync("promo", false);
        Assert.False(details.Value);
        Assert.Equal(ErrorType.ProviderNotReady, details.ErrorType);
        Assert.Null(harness.Provider.RulesetVersion);

        await server.StartAsync();

        await Harness.Eventually(async () => await harness.Client.GetBooleanValueAsync("promo", false), because: "ready after server start");
        Assert.Contains(ProviderEventTypes.ProviderReady, harness.Events);
    }

    [Fact]
    public async Task Without_a_stream_the_provider_polls_and_confirms_unchanged_rulesets_with_ETags()
    {
        await using var server = new FakeBuntingServer { StreamEnabled = false };
        server.Publish(Flags.Bool("promo", false));
        await server.StartAsync();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server));

        await Harness.Eventually(() => server.NotModifiedResponses >= 2, because: "polling with If-None-Match");
        Assert.Equal(ConnectionState.Polling, harness.Provider.ConnectionState);

        server.Publish([Flags.Bool("promo", true)], [], announce: false);

        await Harness.Eventually(async () => await harness.Client.GetBooleanValueAsync("promo", false), TimeSpan.FromSeconds(2), "change found by polling");
    }

    [Fact]
    public async Task A_wrong_api_key_never_makes_the_provider_ready_and_never_throws()
    {
        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool("promo", true));
        await server.StartAsync();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server, o =>
        {
            o.ApiKey = "bnt_srv_wrong";
            o.InitializeTimeout = TimeSpan.FromMilliseconds(300);
        }));

        var details = await harness.Client.GetBooleanDetailsAsync("promo", false);

        Assert.False(details.Value);
        Assert.Equal(ErrorType.ProviderNotReady, details.ErrorType);
        await Harness.Eventually(() => harness.Events.Contains(ProviderEventTypes.ProviderError));
    }

    private static async Task<T> WaitFor<T>(Func<T> read, T expected)
    {
        await Harness.Eventually(() => EqualityComparer<T>.Default.Equals(read(), expected));
        return read();
    }
}

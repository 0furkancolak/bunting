using Bunting.Domain;
using Bunting.Evaluation;
using OpenFeature.Constant;
using OpenFeature.Model;
using EvaluationContext = OpenFeature.Model.EvaluationContext;

namespace Bunting.Sdk.Tests;

public class EvaluationTests
{
    private static async Task<(FakeBuntingServer Server, Harness Harness)> Start(params FlagSnapshot[] flags)
    {
        var server = new FakeBuntingServer();
        server.Publish(flags);
        await server.StartAsync();
        return (server, await Harness.StartAsync(Harness.FastOptions(server)));
    }

    [Fact]
    public async Task Evaluates_every_flag_type_locally()
    {
        var (server, harness) = await Start(
            Flags.Bool("flag-bool", true),
            Flags.Of("flag-string", FlagType.String, "blue", "red"),
            Flags.Of("flag-int", FlagType.Integer, 7, 1),
            Flags.Of("flag-double", FlagType.Double, 1.5, 0.0),
            Flags.Of("flag-object", FlagType.Object, new { color = "blue", sizes = new[] { 1, 2 } }, new { color = "red", sizes = Array.Empty<int>() }));
        await using var _ = server;
        await using var __ = harness;

        Assert.True(await harness.Client.GetBooleanValueAsync("flag-bool", false));
        Assert.Equal("blue", await harness.Client.GetStringValueAsync("flag-string", "none"));
        Assert.Equal(7, await harness.Client.GetIntegerValueAsync("flag-int", 0));
        Assert.Equal(1.5, await harness.Client.GetDoubleValueAsync("flag-double", 0));

        var structure = await harness.Client.GetObjectValueAsync("flag-object", new Value());
        Assert.Equal("blue", structure.AsStructure!["color"].AsString);
        Assert.Equal(2, structure.AsStructure["sizes"].AsList!.Count);
    }

    [Fact]
    public async Task Targeting_rules_use_the_context_and_report_reason_variant_and_rule()
    {
        var (server, harness) = await Start(Flags.Bool("beta", false, Flags.Rule("pro-users", "plan", "pro", "on")));
        await using var _ = server;
        await using var __ = harness;

        var pro = EvaluationContext.Builder().SetTargetingKey("u1").Set("plan", "pro").Build();
        var free = EvaluationContext.Builder().SetTargetingKey("u2").Set("plan", "free").Build();

        var matched = await harness.Client.GetBooleanDetailsAsync("beta", false, pro);
        Assert.True(matched.Value);
        Assert.Equal("on", matched.Variant);
        Assert.Equal(Reason.TargetingMatch, matched.Reason);
        Assert.Equal("pro-users", matched.FlagMetadata?.GetString("ruleId"));

        var fallthrough = await harness.Client.GetBooleanDetailsAsync("beta", true, free);
        Assert.False(fallthrough.Value);
        Assert.Equal("off", fallthrough.Variant);
    }

    [Fact]
    public async Task Errors_return_the_default_value_and_never_throw()
    {
        var (server, harness) = await Start(Flags.Of("flag-string", FlagType.String, "blue", "red"));
        await using var _ = server;
        await using var __ = harness;

        var missing = await harness.Client.GetBooleanDetailsAsync("does-not-exist", true);
        Assert.True(missing.Value);
        Assert.Equal(ErrorType.FlagNotFound, missing.ErrorType);
        Assert.Equal(Reason.Error, missing.Reason);

        var mismatch = await harness.Client.GetBooleanDetailsAsync("flag-string", true);
        Assert.True(mismatch.Value);
        Assert.Equal(ErrorType.TypeMismatch, mismatch.ErrorType);
    }

    [Fact]
    public async Task Evaluation_does_not_call_the_server()
    {
        var (server, harness) = await Start(Flags.Bool("f", true));
        await using var _ = server;
        await using var __ = harness;
        await Harness.Eventually(() => harness.Provider.ConnectionState == ConnectionState.Streaming);
        await Task.Delay(300); // let the start-up synchronization settle
        var requests = server.RulesetRequests;

        for (var i = 0; i < 1000; i++)
        {
            await harness.Client.GetBooleanValueAsync("f", false);
        }

        Assert.Equal(requests, server.RulesetRequests);
    }

    [Fact]
    public async Task Initialization_reports_ready()
    {
        var (server, harness) = await Start(Flags.Bool("f", true));
        await using var _ = server;
        await using var __ = harness;

        await Harness.Eventually(() => harness.Events.Contains(ProviderEventTypes.ProviderReady));
        Assert.Equal(server.Version, harness.Provider.RulesetVersion);
    }
}

using System.Net;
using Bunting.Domain;
using Bunting.Evaluation;
using Bunting.Server.Features.Rulesets;
using Microsoft.Extensions.DependencyInjection;

namespace Bunting.Server.Tests;

[Collection("server")]
public class EngineIntegrationTests(ServerFixture fixture)
{
    [Fact]
    public async Task Persisted_configuration_round_trips_into_a_working_ruleset()
    {
        var api = fixture.Client();
        var project = await api.NewProject();
        await api.NewBoolFlag(project, "checkout");

        await api.Post(
            $"/api/v1/projects/{project}/environments/production/segments",
            new { key = "beta", included = new[] { "alice" } });
        var url = $"/api/v1/projects/{project}/environments/production/flags/checkout";
        var put = await api.Put(
            url,
            new
            {
                enabled = true,
                offVariant = "off",
                rules = new object[]
                {
                    new { id = "beta-users", conditions = new[] { new { attribute = "-", @operator = "InSegment", values = new[] { "beta" } } }, serve = new { variant = "on" } },
                    new { id = "turkey", conditions = new[] { new { attribute = "country", @operator = "Equals", values = new[] { "TR" } } }, serve = new { rollout = new { variants = new[] { new { variant = "on", weight = 10000 }, new { variant = "off", weight = 0 } } } } },
                },
                fallthrough = new { variant = "off" },
            },
            Api.ETag(await api.Get(url)));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var env = await Api.Body(await api.Get($"/api/v1/projects/{project}/environments/production"));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<RulesetSnapshotBuilder>()
            .BuildAsync(env.GetProperty("id").GetGuid(), CancellationToken.None);
        var ruleset = CompiledRuleset.Compile(snapshot);

        Assert.Empty(ruleset.Diagnostics);
        Assert.Equal(env.GetProperty("rulesetVersion").GetInt64(), ruleset.Version);

        var alice = ruleset.Evaluate("checkout", new EvaluationContext("alice"), FlagType.Boolean);
        Assert.Equal((EvaluationReason.TargetingMatch, "beta-users", true), (alice.Reason, alice.RuleId, alice.Value.GetBoolean()));

        var turk = ruleset.Evaluate("checkout", new EvaluationContext("bob", new Dictionary<string, object?> { ["country"] = "tr" }));
        Assert.Equal((EvaluationReason.Split, "turkey"), (turk.Reason, turk.RuleId));

        var other = ruleset.Evaluate("checkout", new EvaluationContext("carol"));
        Assert.Equal((EvaluationReason.Default, false), (other.Reason, other.Value.GetBoolean()));
    }

    [Fact]
    public async Task Health_endpoint_reports_database_connectivity()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

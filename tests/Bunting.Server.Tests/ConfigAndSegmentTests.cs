using System.Net;

namespace Bunting.Server.Tests;

[Collection("server")]
public class ConfigAndSegmentTests(ServerFixture fixture)
{
    private readonly Api _api = fixture.Client();

    private static string ConfigUrl(string project, string flag, string env = "production") =>
        $"/api/v1/projects/{project}/environments/{env}/flags/{flag}";

    private async Task<(string Project, long Version)> Setup(string flag = "f")
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, flag);
        var version = Api.ETag(await _api.Get(ConfigUrl(project, flag)));
        return (project, version);
    }

    private static object Config(object? rules = null, object? fallthrough = null, bool enabled = true, string off = "off") => new
    {
        enabled,
        offVariant = off,
        rules = rules ?? Array.Empty<object>(),
        fallthrough = fallthrough ?? new { variant = "off" },
    };

    private static object Rule(string id, object serve, params object[] conditions) => new { id, conditions, serve };

    private static object Cond(string attribute, string op, params string[] values) => new { attribute, @operator = op, values };

    [Fact]
    public async Task Valid_config_is_stored_and_rule_ids_are_generated_when_missing()
    {
        var (project, version) = await Setup();
        var rule = new { id = "", conditions = new[] { Cond("country", "In", "TR", "DE") }, serve = new { variant = "on" } };
        var response = await _api.Put(ConfigUrl(project, "f"), Config(rules: new[] { rule }), version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Api.Body(response);
        Assert.True(body.GetProperty("enabled").GetBoolean());
        var id = body.GetProperty("rules")[0].GetProperty("id").GetString();
        Assert.StartsWith("r_", id);

        var read = await Api.Body(await _api.Get(ConfigUrl(project, "f")));
        Assert.Equal("In", read.GetProperty("rules")[0].GetProperty("conditions")[0].GetProperty("operator").GetString());
    }

    [Theory]
    [InlineData("weights")]
    [InlineData("unknown-variant")]
    [InlineData("unknown-segment")]
    [InlineData("bad-regex")]
    [InlineData("bad-semver")]
    [InlineData("neither-variant-nor-rollout")]
    [InlineData("unknown-off-variant")]
    public async Task Invalid_configs_are_rejected_with_validation_problems(string scenario)
    {
        var (project, version) = await Setup();
        var config = scenario switch
        {
            "weights" => Config(fallthrough: new { rollout = new { variants = new[] { new { variant = "on", weight = 5000 }, new { variant = "off", weight = 4000 } } } }),
            "unknown-variant" => Config(rules: new[] { Rule("r", new { variant = "ghost" }) }),
            "unknown-segment" => Config(rules: new[] { Rule("r", new { variant = "on" }, Cond("-", "InSegment", "ghost")) }),
            "bad-regex" => Config(rules: new[] { Rule("r", new { variant = "on" }, Cond("email", "Matches", "([")) }),
            "bad-semver" => Config(rules: new[] { Rule("r", new { variant = "on" }, Cond("v", "SemVerGreaterThan", "1.x")) }),
            "neither-variant-nor-rollout" => Config(fallthrough: new { }),
            _ => Config(off: "ghost"),
        };

        var response = await _api.Put(ConfigUrl(project, "f"), config, version);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Api.Body(response);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.True(body.GetProperty("errors").TryGetProperty("config", out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
    }

    [Fact]
    public async Task Duplicate_rule_ids_are_rejected()
    {
        var (project, version) = await Setup();
        var rules = new[] { Rule("same", new { variant = "on" }), Rule("same", new { variant = "off" }) };
        var response = await _api.Put(ConfigUrl(project, "f"), Config(rules: rules), version);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rollout_summing_to_10000_is_accepted()
    {
        var (project, version) = await Setup();
        var rollout = new { rollout = new { variants = new[] { new { variant = "on", weight = 1000 }, new { variant = "off", weight = 9000 } } } };
        var response = await _api.Put(ConfigUrl(project, "f"), Config(fallthrough: rollout), version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Kill_switch_patch_toggles_enabled_and_requires_if_match()
    {
        var (project, version) = await Setup();
        Assert.Equal((HttpStatusCode)428, (await _api.Patch(ConfigUrl(project, "f"), new { enabled = true })).StatusCode);

        var on = await _api.Patch(ConfigUrl(project, "f"), new { enabled = true }, version);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await Api.Body(on)).GetProperty("enabled").GetBoolean());

        var off = await _api.Patch(ConfigUrl(project, "f"), new { enabled = false }, Api.ETag(on));
        Assert.False((await Api.Body(off)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Config_changes_in_one_environment_do_not_touch_others()
    {
        var (project, version) = await Setup();
        var devBefore = await _api.RulesetVersion(project, "development");
        var prodBefore = await _api.RulesetVersion(project, "production");
        await _api.Patch(ConfigUrl(project, "f"), new { enabled = true }, version);
        Assert.Equal(devBefore, await _api.RulesetVersion(project, "development"));
        Assert.Equal(prodBefore + 1, await _api.RulesetVersion(project, "production"));
    }

    private static string SegmentsUrl(string project, string env = "production") =>
        $"/api/v1/projects/{project}/environments/{env}/segments";

    [Fact]
    public async Task Segment_lifecycle_and_reference_protection()
    {
        var (project, version) = await Setup();
        var create = await _api.Post(SegmentsUrl(project), new { key = "beta", included = new[] { "alice" }, conditions = new[] { Cond("plan", "Equals", "pro") } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var segVersion = Api.ETag(create);

        var useIt = await _api.Put(
            ConfigUrl(project, "f"),
            Config(rules: new[] { Rule("r", new { variant = "on" }, Cond("-", "InSegment", "beta")) }),
            version);
        Assert.Equal(HttpStatusCode.OK, useIt.StatusCode);

        var blocked = await _api.Delete($"{SegmentsUrl(project)}/beta", segVersion);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("flag:f", (await Api.Body(blocked)).GetProperty("referencedBy").EnumerateArray().Select(e => e.GetString()));

        var cleared = await _api.Put(ConfigUrl(project, "f"), Config(), Api.ETag(useIt));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.Delete($"{SegmentsUrl(project)}/beta", segVersion)).StatusCode);
    }

    [Fact]
    public async Task Segment_cycles_and_unknown_references_are_rejected()
    {
        var (project, _) = await Setup();
        var a = await _api.Post(SegmentsUrl(project), new { key = "a", included = new[] { "x" } });
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        var b = await _api.Post(SegmentsUrl(project), new { key = "b", conditions = new[] { Cond("-", "InSegment", "a") } });
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);

        var cycle = await _api.Put($"{SegmentsUrl(project)}/a", new { conditions = new[] { Cond("-", "InSegment", "b") } }, Api.ETag(a));
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);

        var unknown = await _api.Post(SegmentsUrl(project), new { key = "c", conditions = new[] { Cond("-", "InSegment", "ghost") } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Segment_update_checks_the_version()
    {
        var (project, _) = await Setup();
        var create = await _api.Post(SegmentsUrl(project), new { key = "s", included = new[] { "a" } });
        var v = Api.ETag(create);
        var ok = await _api.Put($"{SegmentsUrl(project)}/s", new { included = new[] { "a", "b" } }, v);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _api.Put($"{SegmentsUrl(project)}/s", new { included = new[] { "c" } }, v)).StatusCode);
    }
}

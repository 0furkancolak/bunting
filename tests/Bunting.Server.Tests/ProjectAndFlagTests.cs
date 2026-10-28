using System.Net;
using System.Text.Json;

namespace Bunting.Server.Tests;

[Collection("server")]
public class ProjectAndFlagTests(ServerFixture fixture)
{
    private readonly Api _api = fixture.Client();

    [Fact]
    public async Task Creating_a_project_creates_the_default_environments()
    {
        var key = await _api.NewProject();
        var body = await Api.Body(await _api.Get($"/api/v1/projects/{key}"));
        var envs = body.GetProperty("environments").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();
        Assert.Equal(["development", "staging", "production"], envs);
    }

    [Fact]
    public async Task Duplicate_project_key_is_a_409_problem()
    {
        var key = await _api.NewProject();
        var response = await _api.Post("/api/v1/projects", new { key, name = "again" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("Bad Key")]
    [InlineData("")]
    [InlineData("-x")]
    public async Task Invalid_project_key_is_a_400_validation_problem(string key)
    {
        var response = await _api.Post("/api/v1/projects", new { key, name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Api.Body(response);
        Assert.True(body.GetProperty("errors").TryGetProperty("key", out _));
    }

    [Fact]
    public async Task Unknown_project_is_404_problem()
    {
        var response = await _api.Get("/api/v1/projects/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task New_flag_exists_disabled_in_every_environment_and_bumps_versions()
    {
        var project = await _api.NewProject();
        var before = await _api.RulesetVersion(project, "production");
        await _api.NewBoolFlag(project, "checkout");

        Assert.Equal(before + 1, await _api.RulesetVersion(project, "production"));
        foreach (var env in new[] { "development", "staging", "production" })
        {
            var config = await Api.Body(await _api.Get($"/api/v1/projects/{project}/environments/{env}/flags/checkout"));
            Assert.False(config.GetProperty("enabled").GetBoolean());
            Assert.Equal("off", config.GetProperty("offVariant").GetString());
            Assert.Equal("off", config.GetProperty("fallthrough").GetProperty("variant").GetString());
        }
    }

    [Fact]
    public async Task Flag_validation_rejects_bad_key_type_mismatch_and_unknown_default()
    {
        var project = await _api.NewProject();
        var url = $"/api/v1/projects/{project}/flags";

        var badKey = await _api.Post(url, new { key = "Bad Key", name = "x", type = "Boolean", variants = new { on = true } });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);

        var wrongType = await _api.Post(url, new { key = "f1", name = "x", type = "Boolean", variants = new { on = "yes" } });
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

        var badDefault = await _api.Post(url, new { key = "f2", name = "x", type = "Boolean", variants = new { on = true }, defaultVariant = "missing" });
        Assert.Equal(HttpStatusCode.BadRequest, badDefault.StatusCode);

        var noVariants = await _api.Post(url, new { key = "f3", name = "x", type = "Boolean", variants = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, noVariants.StatusCode);
    }

    [Fact]
    public async Task Duplicate_flag_key_is_409()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "dup");
        var response = await _api.Post(
            $"/api/v1/projects/{project}/flags",
            new { key = "dup", name = "x", type = "Boolean", variants = new { on = true, off = false } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task List_supports_search_tag_and_archived_filters_and_shows_environment_state()
    {
        var project = await _api.NewProject();
        foreach (var (key, tag) in new[] { ("alpha-flag", "ui"), ("beta-flag", "backend"), ("gamma", "ui") })
        {
            var response = await _api.Post(
                $"/api/v1/projects/{project}/flags",
                new { key, name = key.ToUpperInvariant(), type = "Boolean", variants = new { on = true, off = false }, tags = new[] { tag } });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        async Task<string[]> Keys(string query) =>
            (await Api.Body(await _api.Get($"/api/v1/projects/{project}/flags{query}")))
            .EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToArray();

        Assert.Equal(["alpha-flag", "beta-flag", "gamma"], await Keys(string.Empty));
        Assert.Equal(["alpha-flag", "beta-flag"], await Keys("?search=FLAG"));
        Assert.Equal(["alpha-flag", "gamma"], await Keys("?tag=ui"));
        Assert.Empty(await Keys("?archived=true"));

        var first = (await Api.Body(await _api.Get($"/api/v1/projects/{project}/flags")))[0];
        Assert.False(first.GetProperty("environments").GetProperty("production").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Update_requires_if_match_and_rejects_stale_versions_with_the_current_one()
    {
        var project = await _api.NewProject();
        var version = await _api.NewBoolFlag(project, "upd");
        var url = $"/api/v1/projects/{project}/flags/upd";
        var body = new { name = "Renamed", variants = new { on = true, off = false }, tags = new[] { "t" }, clientSideAvailable = false, archived = false };

        Assert.Equal((HttpStatusCode)428, (await _api.Put(url, body, null)).StatusCode);

        var ok = await _api.Put(url, body, version);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(version + 1, Api.ETag(ok));

        var stale = await _api.Put(url, body, version);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(version + 1, (await Api.Body(stale)).GetProperty("currentVersion").GetInt64());
    }

    [Fact]
    public async Task Removing_a_referenced_variant_is_rejected()
    {
        var project = await _api.NewProject();
        var version = await _api.NewBoolFlag(project, "refd");
        var response = await _api.Put(
            $"/api/v1/projects/{project}/flags/refd",
            new { name = "x", variants = new { on = true }, clientSideAvailable = false, archived = false },
            version);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Flags_must_be_archived_before_deletion()
    {
        var project = await _api.NewProject();
        var version = await _api.NewBoolFlag(project, "del");
        var url = $"/api/v1/projects/{project}/flags/del";
        Assert.Equal(HttpStatusCode.Conflict, (await _api.Delete(url, version)).StatusCode);

        var archived = await _api.Put(
            url,
            new { name = "del", variants = new { on = true, off = false }, clientSideAvailable = false, archived = true },
            version);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.Delete(url, Api.ETag(archived))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Get(url)).StatusCode);
    }

    [Fact]
    public async Task Creating_an_environment_adds_disabled_configs_for_existing_flags()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "carry");
        var create = await _api.Post($"/api/v1/projects/{project}/environments", new { key = "qa", name = "QA" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var config = await Api.Body(await _api.Get($"/api/v1/projects/{project}/environments/qa/flags/carry"));
        Assert.False(config.GetProperty("enabled").GetBoolean());
        Assert.Equal("off", config.GetProperty("offVariant").GetString());
    }

    [Fact]
    public async Task Deleting_a_project_removes_everything()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "gone");
        Assert.Equal(HttpStatusCode.NoContent, (await _api.Delete($"/api/v1/projects/{project}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Get($"/api/v1/projects/{project}/flags")).StatusCode);
    }
}

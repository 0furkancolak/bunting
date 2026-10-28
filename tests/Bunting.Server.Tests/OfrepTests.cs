using System.Net;
using System.Text.Json;

namespace Bunting.Server.Tests;

[Collection("server")]
public class OfrepTests(ServerFixture fixture)
{
    private readonly Api _api = fixture.Client();

    private static string Cfg(string project, string flag) => $"/api/v1/projects/{project}/environments/production/flags/{flag}";

    /// <summary>Project with flag "promo": enabled, 'country in TR' rule -> on, fallthrough off.</summary>
    private async Task<(string Project, HttpClient Server)> Setup(bool clientSide = false)
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "promo", clientSide);
        var put = await _api.Put(
            Cfg(project, "promo"),
            new
            {
                enabled = true,
                offVariant = "off",
                rules = new[] { new { id = "turkey", conditions = new[] { new { attribute = "country", @operator = "Equals", values = new[] { "TR" } } }, serve = new { variant = "on" } } },
                fallthrough = new { variant = "off" },
            },
            Api.ETag(await _api.Get(Cfg(project, "promo"))));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var (_, key) = await _api.NewKey(project, "production", "Server");
        return (project, fixture.WithKey(key));
    }

    [Fact]
    public async Task Single_evaluation_follows_the_ofrep_response_shape()
    {
        var (_, http) = await Setup();

        var match = await http.Ofrep("/ofrep/v1/evaluate/flags/promo", new { targetingKey = "u1", country = "tr" });
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        var body = await Api.Body(match);
        Assert.Equal("promo", body.GetProperty("key").GetString());
        Assert.True(body.GetProperty("value").GetBoolean());
        Assert.Equal("TARGETING_MATCH", body.GetProperty("reason").GetString());
        Assert.Equal("on", body.GetProperty("variant").GetString());
        Assert.Equal("turkey", body.GetProperty("metadata").GetProperty("ruleId").GetString());

        var fallthrough = await Api.Body(await http.Ofrep("/ofrep/v1/evaluate/flags/promo", new { targetingKey = "u1", country = "DE" }));
        Assert.False(fallthrough.GetProperty("value").GetBoolean());
        Assert.Equal("DEFAULT", fallthrough.GetProperty("reason").GetString());
        Assert.False(fallthrough.TryGetProperty("metadata", out _));
    }

    [Fact]
    public async Task Unknown_flag_is_404_with_flag_not_found()
    {
        var (_, http) = await Setup();
        var response = await http.Ofrep("/ofrep/v1/evaluate/flags/nope", new { targetingKey = "u" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await Api.Body(response);
        Assert.Equal("FLAG_NOT_FOUND", body.GetProperty("errorCode").GetString());
        Assert.Equal("nope", body.GetProperty("key").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"context\": 5}")]
    [InlineData("{\"context\": {\"targetingKey\": 5}}")]
    [InlineData("{\"context\": {\"plan\": {\"nested\": true}}}")]
    [InlineData("{\"context\": {\"list\": [1,2]}}")]
    [InlineData("not json")]
    public async Task Invalid_context_is_400_invalid_context(string rawBody)
    {
        var (_, http) = await Setup();
        var request = new HttpRequestMessage(HttpMethod.Post, "/ofrep/v1/evaluate/flags/promo")
        {
            Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json"),
        };
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_CONTEXT", (await Api.Body(response)).GetProperty("errorCode").GetString());

        var bulk = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/ofrep/v1/evaluate/flags") { Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json") });
        Assert.Equal(HttpStatusCode.BadRequest, bulk.StatusCode);
        Assert.Equal("INVALID_CONTEXT", (await Api.Body(bulk)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Missing_targeting_key_for_a_rollout_is_reported_as_TARGETING_KEY_MISSING()
    {
        var (project, http) = await Setup();
        await _api.NewBoolFlag(project, "gradual");
        var url = Cfg(project, "gradual");
        await _api.Put(
            url,
            new { enabled = true, offVariant = "off", rules = Array.Empty<object>(), fallthrough = new { rollout = new { variants = new[] { new { variant = "on", weight = 5000 }, new { variant = "off", weight = 5000 } } } } },
            Api.ETag(await _api.Get(url)));

        var single = await http.Ofrep("/ofrep/v1/evaluate/flags/gradual", new { country = "DE" });
        Assert.Equal(HttpStatusCode.BadRequest, single.StatusCode);
        Assert.Equal("TARGETING_KEY_MISSING", (await Api.Body(single)).GetProperty("errorCode").GetString());

        var bulk = await Api.Body(await http.Ofrep("/ofrep/v1/evaluate/flags", new { country = "DE" }));
        var item = bulk.GetProperty("flags").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "gradual");
        Assert.Equal("TARGETING_KEY_MISSING", item.GetProperty("errorCode").GetString());
        Assert.False(item.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task Bulk_returns_all_non_archived_flags_with_version_metadata_and_etag()
    {
        var (project, http) = await Setup();
        await _api.NewBoolFlag(project, "other");
        var response = await http.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "u", country = "TR" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        var body = await Api.Body(response);
        Assert.Equal(["other", "promo"], body.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToArray());
        Assert.StartsWith("v", body.GetProperty("metadata").GetProperty("version").GetString());
    }

    [Fact]
    public async Task Archived_flags_are_not_in_bulk_but_remain_evaluable_individually()
    {
        var (project, http) = await Setup();
        var version = Api.ETag(await _api.Get($"/api/v1/projects/{project}/flags/promo"));
        await _api.Put($"/api/v1/projects/{project}/flags/promo", new { name = "promo", variants = new { on = true, off = false }, clientSideAvailable = false, archived = true }, version);

        var bulk = await Api.Body(await http.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "u" }));
        Assert.Empty(bulk.GetProperty("flags").EnumerateArray());
        var single = await Api.Body(await http.Ofrep("/ofrep/v1/evaluate/flags/promo", new { targetingKey = "u" }));
        Assert.Equal("DISABLED", single.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task If_none_match_returns_304_until_the_ruleset_or_context_changes()
    {
        var (project, http) = await Setup();
        var context = new { targetingKey = "u", country = "TR" };
        var first = await http.Ofrep("/ofrep/v1/evaluate/flags", context);
        var etag = first.Headers.ETag!.Tag;

        var again = await http.Ofrep("/ofrep/v1/evaluate/flags", context, etag);
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync());

        var otherContext = await http.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "someone-else", country = "TR" }, etag);
        Assert.Equal(HttpStatusCode.OK, otherContext.StatusCode);
        Assert.NotEqual(etag, otherContext.Headers.ETag!.Tag);

        await _api.Patch(Cfg(project, "promo"), new { enabled = false }, Api.ETag(await _api.Get(Cfg(project, "promo"))));
        var afterChange = await http.Ofrep("/ofrep/v1/evaluate/flags", context, etag);
        Assert.Equal(HttpStatusCode.OK, afterChange.StatusCode);
        Assert.NotEqual(etag, afterChange.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Changes_are_visible_immediately_on_the_same_instance()
    {
        var (project, http) = await Setup();
        var ctx = new { targetingKey = "u", country = "DE" };
        Assert.False((await Api.Body(await http.Ofrep("/ofrep/v1/evaluate/flags/promo", ctx))).GetProperty("value").GetBoolean());

        await _api.Patch(Cfg(project, "promo"), new { enabled = false }, Api.ETag(await _api.Get(Cfg(project, "promo"))));

        Assert.Equal("DISABLED", (await Api.Body(await http.Ofrep("/ofrep/v1/evaluate/flags/promo", ctx))).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Typed_values_are_returned_as_json()
    {
        var project = await _api.NewProject();
        foreach (var (key, type, variants, off) in new (string, string, object, string)[]
        {
            ("txt", "String", new { a = "hello" }, "a"),
            ("num", "Integer", new { a = 7 }, "a"),
            ("dbl", "Double", new { a = 1.5 }, "a"),
            ("obj", "Object", new { a = new { limit = 10 } }, "a"),
        })
        {
            var create = await _api.Post($"/api/v1/projects/{project}/flags", new { key, name = key, type, variants, defaultVariant = off });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        var (_, apiKey) = await _api.NewKey(project, "production", "Server");
        var http = fixture.WithKey(apiKey);
        async Task<JsonElement> Value(string flag) =>
            (await Api.Body(await http.Ofrep($"/ofrep/v1/evaluate/flags/{flag}", new { targetingKey = "u" }))).GetProperty("value");

        Assert.Equal("hello", (await Value("txt")).GetString());
        Assert.Equal(7, (await Value("num")).GetInt32());
        Assert.Equal(1.5, (await Value("dbl")).GetDouble());
        Assert.Equal(10, (await Value("obj")).GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Cloudflare_country_header_is_ignored_unless_enabled()
    {
        var (_, http) = await Setup();
        var request = new HttpRequestMessage(HttpMethod.Post, "/ofrep/v1/evaluate/flags/promo")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { context = new { targetingKey = "u" } }, options: Api.Json),
        };
        request.Headers.Add("CF-IPCountry", "TR");
        var body = await Api.Body(await http.SendAsync(request));
        Assert.Equal("DEFAULT", body.GetProperty("reason").GetString()); // default: header not used
    }

    [Fact]
    public async Task Cloudflare_country_header_fills_country_when_enabled_and_never_overrides_the_context()
    {
        var project = await _api.NewProject();
        await _api.NewBoolFlag(project, "promo");
        await _api.Put(
            Cfg(project, "promo"),
            new { enabled = true, offVariant = "off", rules = new[] { new { id = "tr", conditions = new[] { new { attribute = "country", @operator = "Equals", values = new[] { "TR" } } }, serve = new { variant = "on" } } }, fallthrough = new { variant = "off" } },
            Api.ETag(await _api.Get(Cfg(project, "promo"))));
        var (_, apiKey) = await _api.NewKey(project, "production", "Server");

        using var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.Configure<Bunting.Server.Features.Ofrep.OfrepOptions>(s, o => o.UseCloudflareCountry = true)));
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        async Task<string?> Reason(object context)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/ofrep/v1/evaluate/flags/promo")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { context }, options: Api.Json),
            };
            request.Headers.Add("CF-IPCountry", "TR");
            return (await Api.Body(await http.SendAsync(request))).GetProperty("reason").GetString();
        }

        Assert.Equal("TARGETING_MATCH", await Reason(new { targetingKey = "u" }));
        Assert.Equal("DEFAULT", await Reason(new { targetingKey = "u", country = "DE" }));
    }
}
